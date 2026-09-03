using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VideoMerger.Core;

namespace VideoMerger.App
{
    /// <summary>
    /// Jendela utama.
    ///
    /// Aturan thread yang dipegang seluruh berkas ini: thread pekerja tidak
    /// pernah menyentuh widget. Semua yang datang dari pekerja masuk lewat
    /// Dispatcher, dan hanya penangan di thread UI yang mengubah tampilan.
    ///
    /// Kedua tab berbagi satu bendera sibuk, satu pekerja, dan satu tombol
    /// aksi, dengan sengaja: penggabungan dan pembakaran subtitle tidak boleh
    /// berjalan bersamaan dan berebut folder tujuan yang sama.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings = AppSettings.Load();
        private FFmpegTools _tools;

        private readonly ObservableCollection<MergeRow> _rows =
            new ObservableCollection<MergeRow>();
        private readonly ObservableCollection<SubRow> _subRows =
            new ObservableCollection<SubRow>();

        private FFmpegTask _task;                 // penggabungan ATAU hardsub
        private Task _worker;
        private CancellationTokenSource _scanCancel = new CancellationTokenSource();
        private bool _busy;
        private string _resultPath = "";

        // Urutan terakhir yang benar-benar dipilih pengguna. Pilihan di kotak
        // berubah jadi "Urutan manual" begitu satu baris digeser, dan
        // pemindaian baru harus kembali ke pilihan ini - bukan meninggalkan
        // daftarnya dalam urutan mentah, yang terlihat seperti pengurutan
        // diam-diam berhenti bekerja.
        private SortBy _explicitSort = SortBy.Name;

        // Daftar encoder yang ditampilkan: (id, label). Isinya ditentukan
        // hasil pengukuran, bukan daftar tetap.
        private readonly List<Tuple<string, string>> _encoderChoices =
            new List<Tuple<string, string>>();
        private string _autoEncoder = "";          // hasil benchmark, "" = CPU
        private string _benchDetail = "";
        private bool _benchRunning;

        private readonly List<string> _pendingLog = new List<string>();
        private DispatcherTimer _logTimer;
        private List<Tuple<string, string, object>> _sourceOptions =
            new List<Tuple<string, string, object>>();
        private bool _loading = true;

        private static readonly string[] FontChoices =
        {
            "Arial", "Segoe UI", "Tahoma", "Verdana", "Times New Roman",
            "Calibri", "Roboto", "Noto Sans",
        };

        public MainWindow()
        {
            InitializeComponent();
            Title = AppInfo.Name + " " + AppInfo.Version;
            LblVersion.Text = "  v" + AppInfo.Version;

            GridFiles.ItemsSource = _rows;
            GridSubs.ItemsSource = _subRows;

            LoadSettings();
            _loading = false;

            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _logTimer.Tick += (s, e) => FlushLog();
            _logTimer.Start();

            Loaded += (s, e) => DetectFFmpeg();
            Closing += OnClosing;
        }

        // ======================================================== setelan --
        private void LoadSettings()
        {
            foreach (SortBy key in Enum.GetValues(typeof(SortBy)))
                if (key != SortBy.Manual) CmbSort.Items.Add(Labels.Of(key));
            CmbSort.Items.Add(Labels.Of(SortBy.Manual));

            foreach (MergeMode mode in Enum.GetValues(typeof(MergeMode)))
                CmbMode.Items.Add(Labels.Of(mode));

            foreach (string font in FontChoices) CmbFont.Items.Add(font);
            foreach (string ext in Hardsubber.OutputExtensions) CmbContainer.Items.Add(ext);

            _explicitSort = _settings.SortBy;
            if (_explicitSort == SortBy.Manual) _explicitSort = SortBy.Name;
            CmbSort.SelectedItem = Labels.Of(_explicitSort);
            CmbMode.SelectedItem = Labels.Of(_settings.MergeMode);
            ChkBackgroundPriority.IsChecked = _settings.GetBool("background_priority");
            Shell.BackgroundPriority = _settings.GetBool("background_priority");

            TxtFolder.Text = _settings["last_input_dir"];
            ChkRecursive.IsChecked = _settings.GetBool("recursive");
            ChkDesc.IsChecked = _settings.GetBool("sort_desc");
            TxtCrf.Text = _settings.GetInt("crf", 0, 51).ToString(CultureInfo.InvariantCulture);

            TxtSubFolder.Text = _settings["hardsub_input_dir"];
            TxtOutDir.Text = _settings["hardsub_output_dir"];
            TxtSuffix.Text = _settings["hardsub_suffix"];
            CmbContainer.SelectedItem = _settings["hardsub_container"];
            if (CmbContainer.SelectedItem == null) CmbContainer.SelectedIndex = 0;
            TxtSubCrf.Text = _settings.GetInt("hardsub_crf", 0, 51)
                .ToString(CultureInfo.InvariantCulture);
            ChkCopyAudio.IsChecked = _settings.GetBool("hardsub_copy_audio");

            ChkStyle.IsChecked = _settings.GetBool("sub_style_enabled");
            CmbFont.SelectedItem = _settings["sub_font"];
            if (CmbFont.SelectedItem == null) CmbFont.SelectedIndex = 0;
            TxtSubSize.Text = _settings.GetInt("sub_size", 8, 200)
                .ToString(CultureInfo.InvariantCulture);
            ChkBold.IsChecked = _settings.GetBool("sub_bold");
            PaintSwatch(BtnColour, _settings["sub_primary"]);
            PaintSwatch(BtnOutline, _settings["sub_outline_color"]);
            OnStyleToggled(null, null);

            SizeWindow();
            Tabs.SelectedIndex = _settings.GetInt("active_tab", 0, 1);
        }

        /// <summary>
        /// Ukuran awal yang muat isi DAN muat layar.
        ///
        /// Dibatasi ke area kerja karena isi yang lega di 1920x1080 lebih
        /// tinggi daripada layar laptop 1366x768 setelah taskbar dipotong -
        /// dan pada versi sebelumnya itulah yang memotong tombol Gabungkan
        /// sampai penggunanya mengubah ukuran sendiri.
        /// </summary>
        private void SizeWindow()
        {
            double availW = SystemParameters.WorkArea.Width - 40;
            double availH = SystemParameters.WorkArea.Height - 40;
            Width = Math.Min(_settings.GetInt("window_width", 900, 4000), availW);
            Height = Math.Min(_settings.GetInt("window_height", 620, 3000), availH);
            MinWidth = Math.Min(960, availW);
            MinHeight = Math.Min(640, availH);
            if (_settings.GetBool("window_maximized")) WindowState = WindowState.Maximized;
        }

        private void SaveSettings()
        {
            try
            {
                _settings.Set("last_input_dir", TxtFolder.Text);
                _settings.Set("recursive", ChkRecursive.IsChecked == true);
                _settings.Set("sort_desc", ChkDesc.IsChecked == true);
                _settings.SortBy = _explicitSort;
                _settings.MergeMode = SelectedMode();
                _settings.Set("crf", CrfValue(TxtCrf, "crf"));
                _settings.Set("active_tab", Tabs.SelectedIndex);
                _settings.Set("window_maximized", WindowState == WindowState.Maximized);
                if (WindowState == WindowState.Normal)
                {
                    _settings.Set("window_width", (int)Width);
                    _settings.Set("window_height", (int)Height);
                }
                _settings.Set("hardsub_input_dir", TxtSubFolder.Text);
                _settings.Set("hardsub_output_dir", TxtOutDir.Text);
                _settings.Set("hardsub_suffix", TxtSuffix.Text);
                _settings.Set("hardsub_container", CmbContainer.SelectedItem ?? ".mp4");
                _settings.Set("hardsub_crf", CrfValue(TxtSubCrf, "hardsub_crf"));
                _settings.Set("hardsub_copy_audio", ChkCopyAudio.IsChecked == true);
                _settings.Set("sub_style_enabled", ChkStyle.IsChecked == true);
                _settings.Set("sub_font", CmbFont.SelectedItem ?? "Arial");
                _settings.Set("sub_size", ParseInt(TxtSubSize.Text, 24, 8, 200));
                _settings.Set("sub_bold", ChkBold.IsChecked == true);
                _settings.Save();
            }
            catch (Exception) { }
        }

        // ====================================================== FFmpeg --
        private void DetectFFmpeg()
        {
            _tools = FFmpegLocator.Locate(_settings["ffmpeg_dir"]);
            if (_tools != null)
            {
                LblFFmpeg.Text = "FFmpeg " + _tools.ShortVersion + " (" + _tools.Source + ")";
                DotFFmpeg.Fill = (Brush)FindResource("Success");
                ShowHardware();
                RefreshEncoders();
                if (FFmpegUpdater.DueForCheck(_settings)) CheckFFmpegUpdate(true);
                if (!string.IsNullOrWhiteSpace(TxtFolder.Text)) Rescan();
                return;
            }

            LblFFmpeg.Text = "FFmpeg tidak ditemukan";
            LblFFmpeg.Foreground = (Brush)FindResource("Danger");
            DotFFmpeg.Fill = (Brush)FindResource("Danger");

            var answer = MessageBox.Show(
                "Aplikasi ini memerlukan FFmpeg untuk memproses video, tetapi "
                + "FFmpeg tidak ditemukan di komputer ini.\n\n"
                + "Ya\t= Unduh otomatis sekarang (sekitar 80 MB)\n"
                + "Tidak\t= Saya sudah punya, biar saya tunjukkan foldernya\n"
                + "Batal\t= Nanti saja",
                "FFmpeg tidak ditemukan", MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Yes) DownloadFFmpeg();
            else if (answer == MessageBoxResult.No)
            {
                string folder = PickFolder("Pilih folder yang berisi ffmpeg.exe dan ffprobe.exe");
                if (!string.IsNullOrEmpty(folder))
                {
                    _settings.Set("ffmpeg_dir", folder);
                    _settings.Save();
                    DetectFFmpeg();
                }
            }
        }

        /// <summary>
        /// Isi daftar encoder dari hasil pengukuran.
        ///
        /// Dijalankan di latar belakang tanpa kecuali: membaca daftar encoder
        /// saja sudah memakan 100-300 ms, dan mengukurnya beberapa detik.
        /// Versi sebelumnya melakukan yang pertama di thread UI, dan jendelanya
        /// membeku sesaat setiap kali dibuka - persis di komputer lambat yang
        /// paling tidak boleh mengalaminya.
        /// </summary>
        private void RefreshEncoders(bool forceBenchmark = false)
        {
            if (_tools == null || _benchRunning) return;
            var tools = _tools;

            // Diberi nilai awal: operator && memutus jalur ketika
            // forceBenchmark true, sehingga TryCached tidak pernah terpanggil
            // dan variabel out-nya tidak pernah terisi.
            string cachedBest = "", cachedDetail = "";
            bool haveCache = !forceBenchmark
                && EncoderBenchmark.TryCached(_settings, tools,
                                              out cachedBest, out cachedDetail);
            if (haveCache)
            {
                _autoEncoder = cachedBest;
                _benchDetail = cachedDetail;
                BuildEncoderList(EncoderBenchmark.Listed(tools));
                return;
            }

            _benchRunning = true;
            BtnBenchmark.IsEnabled = false;
            LblEncoderNote.Text = "Mengukur kecepatan encoder...";
            // Kotak yang benar-benar kosong terbaca sebagai aplikasi rusak,
            // bukan sebagai sesuatu yang sedang bekerja.
            CmbEncoder.SelectionChanged -= OnEncoderChanged;
            CmbEncoder.Items.Clear();
            CmbEncoder.Items.Add("Mengukur kecepatan...");
            CmbEncoder.SelectedIndex = 0;
            CmbEncoder.IsEnabled = false;
            CmbEncoder.SelectionChanged += OnEncoderChanged;

            Task.Run(() =>
            {
                var listed = EncoderBenchmark.Listed(tools);
                var scores = EncoderBenchmark.Measure(
                    tools, new TargetSpec(),
                    (done, total, label) => Dispatcher.BeginInvoke(new Action(() =>
                        LblEncoderNote.Text = "Mengukur " + (done + 1) + "/" + total
                                              + ": " + label + "..."),
                        DispatcherPriority.Background));

                Dispatcher.Invoke(() =>
                {
                    EncoderBenchmark.StoreCache(_settings, tools, scores);
                    _autoEncoder = EncoderBenchmark.Best(scores);
                    var sb = new StringBuilder();
                    foreach (var score in scores)
                    {
                        if (sb.Length > 0) sb.Append("; ");
                        sb.Append(score.Describe());
                    }
                    _benchDetail = sb.ToString();
                    BuildEncoderList(listed);
                    _benchRunning = false;
                    BtnBenchmark.IsEnabled = true;
                });
            });
        }

        private void BuildEncoderList(List<EncoderCandidate> listed)
        {
            _encoderChoices.Clear();
            // Entri pertama selalu "Otomatis" dan menyebut pemenangnya, supaya
            // penggunanya tahu apa yang sebenarnya akan dipakai - bukan sekadar
            // kata "otomatis" yang tidak menjelaskan apa-apa.
            // Tanpa kurung bersarang: "Otomatis - tercepat (CPU (libx264))"
            // terpotong jadi "...(CPU (libx" di kotak selebar apa pun yang wajar.
            _encoderChoices.Add(Tuple.Create("__auto__",
                "Otomatis - " + EncoderBenchmark.LabelOf(_autoEncoder)));
            _encoderChoices.Add(Tuple.Create("", "CPU (libx264)"));
            foreach (var candidate in listed)
                if (candidate.Vendor != "CPU")
                    _encoderChoices.Add(Tuple.Create(candidate.Id, candidate.Label));

            CmbEncoder.SelectionChanged -= OnEncoderChanged;
            CmbEncoder.Items.Clear();
            foreach (var choice in _encoderChoices) CmbEncoder.Items.Add(choice.Item2);

            string saved = _settings.GetBool("encoder_auto")
                ? "__auto__" : _settings["hwaccel_encoder"];
            int index = _encoderChoices.FindIndex(c => c.Item1 == saved);
            CmbEncoder.SelectedIndex = index >= 0 ? index : 0;
            CmbEncoder.IsEnabled = true;
            CmbEncoder.SelectionChanged += OnEncoderChanged;

            LblEncoderNote.Text = _benchDetail;
            LblEncoderNote.ToolTip = _benchDetail;
        }

        /// <summary>Encoder yang benar-benar dipakai, setelah "Otomatis" diterjemahkan.</summary>
        private string SelectedEncoder()
        {
            int index = CmbEncoder.SelectedIndex;
            if (index < 0 || index >= _encoderChoices.Count) return "";
            string id = _encoderChoices[index].Item1;
            return id == "__auto__" ? _autoEncoder : id;
        }

        private void OnEncoderChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            int index = CmbEncoder.SelectedIndex;
            if (index < 0 || index >= _encoderChoices.Count) return;
            string id = _encoderChoices[index].Item1;
            _settings.Set("encoder_auto", id == "__auto__");
            if (id != "__auto__") _settings.Set("hwaccel_encoder", id);
            _settings.Save();
        }

        private void OnRunBenchmark(object sender, RoutedEventArgs e)
        {
            if (BusyGuard()) return;
            EncoderBenchmark.ClearCache(_settings);
            RefreshEncoders(true);
        }

        private void OnPerformanceChanged(object sender, RoutedEventArgs e)
        {
            bool on = ChkBackgroundPriority.IsChecked == true;
            Shell.BackgroundPriority = on;
            _settings.Set("background_priority", on);
            _settings.Save();
        }

        // ------------------------------------------- pembaruan FFmpeg --
        private void OnCheckFFmpegUpdate(object sender, RoutedEventArgs e)
        {
            if (_tools == null) { DetectFFmpeg(); return; }
            if (BusyGuard()) return;
            CheckFFmpegUpdate(false);
        }

        /// <summary>
        /// `silent` = pemeriksaan berkala saat aplikasi dibuka: hanya
        /// memberitahu kalau memang ada versi baru, dan tidak pernah
        /// memunculkan kotak dialog kalau jaringannya mati.
        /// </summary>
        private void CheckFFmpegUpdate(bool silent)
        {
            var tools = _tools;
            if (tools == null) return;
            BtnFFmpegUpdate.IsEnabled = false;
            if (!silent) BtnFFmpegUpdate.Content = "Memeriksa...";

            Task.Run(() =>
            {
                var check = FFmpegUpdater.Check(tools);
                Dispatcher.Invoke(() => OnUpdateChecked(check, silent));
            });
        }

        private void OnUpdateChecked(UpdateCheck check, bool silent)
        {
            BtnFFmpegUpdate.IsEnabled = true;
            BtnFFmpegUpdate.Content = "Periksa pembaruan";
            FFmpegUpdater.MarkChecked(_settings);

            if (!check.Checked)
            {
                if (!silent)
                    MessageBox.Show("Gagal memeriksa pembaruan FFmpeg.\n\n"
                                    + check.Error, AppInfo.Name,
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!check.UpdateAvailable)
            {
                if (!silent)
                    MessageBox.Show("FFmpeg sudah versi terbaru (" + check.Latest + ").",
                                    AppInfo.Name, MessageBoxButton.OK,
                                    MessageBoxImage.Information);
                return;
            }

            BtnFFmpegUpdate.Content = "Perbarui ke " + check.Latest;

            if (!check.Managed)
            {
                // FFmpeg ini milik winget/chocolatey/scoop atau ditaruh sendiri
                // oleh penggunanya. Menimpanya berarti diam-diam mengambil alih
                // pemasangan yang bukan kita buat, dan pembaruan berikutnya
                // dari alat aslinya akan bertabrakan.
                if (silent) return;
                MessageBox.Show(
                    "Tersedia FFmpeg " + check.Latest + " (terpasang " + check.Installed
                    + ").\n\nFFmpeg ini dipasang lewat " + _tools.Source
                    + ", bukan oleh aplikasi ini, jadi pembaruannya sebaiknya lewat "
                    + "alat yang sama - misalnya:\n\n    winget upgrade Gyan.FFmpeg",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show(
                    "Tersedia FFmpeg " + check.Latest + " (terpasang "
                    + check.Installed + ").\n\nUnduh dan pasang sekarang? "
                    + "Ukurannya sekitar 80 MB.", AppInfo.Name,
                    MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes)
                UpdateFFmpeg();
        }

        private void UpdateFFmpeg()
        {
            if (BusyGuard()) return;
            _scanCancel = new CancellationTokenSource();
            var token = _scanCancel.Token;
            SetBusy(true, "Memperbarui FFmpeg...");

            _worker = Task.Run(() =>
            {
                FFmpegTools tools = null;
                try
                {
                    tools = FFmpegUpdater.Update(
                        (done, total, msg) => Report(new Progress
                        {
                            Stage = Stage.Scanning,
                            Fraction = total > 0 ? (double)done / total : 0,
                            Message = msg + " " + Humanize.Size(done)
                                      + (total > 0 ? " / " + Humanize.Size(total) : ""),
                        }),
                        () => token.IsCancellationRequested);
                }
                catch (Exception) { tools = null; }
                Dispatcher.Invoke(() =>
                {
                    OnFFmpegReady(tools);
                    if (tools != null)
                    {
                        // Versi FFmpeg ikut masuk sidik jari benchmark, jadi
                        // angka lamanya sudah tidak berlaku.
                        EncoderBenchmark.ClearCache(_settings);
                        RefreshEncoders(true);
                    }
                });
            });
        }

        private void DownloadFFmpeg()
        {
            _scanCancel = new CancellationTokenSource();
            var token = _scanCancel.Token;
            SetBusy(true, "Mengunduh FFmpeg...");

            _worker = Task.Run(() =>
            {
                FFmpegTools tools = null;
                try
                {
                    tools = FFmpegLocator.DownloadAndInstall(
                        (done, total, msg) => Report(new Progress
                        {
                            Stage = Stage.Scanning,
                            Fraction = total > 0 ? (double)done / total : 0,
                            Message = msg + " " + Humanize.Size(done)
                                      + (total > 0 ? " / " + Humanize.Size(total) : ""),
                        }),
                        () => token.IsCancellationRequested);
                }
                catch (Exception)
                {
                    // %APPDATA% yang tidak bisa ditulisi dulu melempar langsung
                    // dari thread ini dan jendelanya tinggal diam bertuliskan
                    // "mengunduh" sampai dimatikan paksa.
                    tools = null;
                }
                Dispatcher.Invoke(() => OnFFmpegReady(tools));
            });
        }

        private void OnFFmpegReady(FFmpegTools tools)
        {
            SetBusy(false, "Siap.");
            Bar.Value = 0;
            if (tools != null)
            {
                _settings.Set("ffmpeg_dir", "");
                _settings.Save();
                _tools = tools;
                LblFFmpeg.Text = "FFmpeg " + tools.ShortVersion + " (" + tools.Source + ")";
                LblFFmpeg.Foreground = (Brush)FindResource("Muted");
                DotFFmpeg.Fill = (Brush)FindResource("Success");
                RefreshEncoders();
                MessageBox.Show("FFmpeg berhasil dipasang.", AppInfo.Name);
            }
            else
            {
                MessageBox.Show(
                    "Gagal mengunduh FFmpeg. Periksa koneksi internet, atau unduh "
                    + "manual dari https://www.gyan.dev/ffmpeg/builds/ lalu letakkan "
                    + "ffmpeg.exe dan ffprobe.exe di folder aplikasi ini.",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ============================================= tab 1: gabungkan --
        /// <summary>True kalau ada proses berjalan, artinya pemanggil harus berhenti.</summary>
        private bool BusyGuard()
        {
            if (_busy) SystemSounds();
            return _busy;
        }

        private static void SystemSounds()
        {
            try { System.Media.SystemSounds.Beep.Play(); } catch (Exception) { }
        }

        private void OnFolderKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Rescan();
        }

        private void OnChooseFolder(object sender, RoutedEventArgs e)
        {
            if (BusyGuard()) return;
            string folder = PickFolder("Pilih folder berisi video", TxtFolder.Text);
            if (!string.IsNullOrEmpty(folder)) { TxtFolder.Text = folder; Rescan(); }
        }

        private void OnRescan(object sender, RoutedEventArgs e) => Rescan();

        private void Rescan()
        {
            string folder = (TxtFolder.Text ?? "").Trim();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            if (BusyGuard()) return;
            if (_tools == null) { DetectFFmpeg(); if (_tools == null) return; }

            _settings.Set("last_input_dir", folder);
            _settings.Set("recursive", ChkRecursive.IsChecked == true);
            _settings.Save();

            _scanCancel = new CancellationTokenSource();
            var token = _scanCancel.Token;
            bool recursive = ChkRecursive.IsChecked == true;
            var tools = _tools;

            _rows.Clear();
            SetBusy(true, "Memindai folder...");

            _worker = Task.Run(() =>
            {
                try
                {
                    var found = Scanner.ScanFolder(folder, recursive,
                                                   () => token.IsCancellationRequested);
                    if (found.Count == 0)
                    {
                        Dispatcher.Invoke(() => OnScanDone(found));
                        return;
                    }
                    Report(new Progress
                    {
                        Stage = Stage.Probing,
                        Message = "Memeriksa " + found.Count + " file video...",
                    });
                    Prober.ProbeMany(tools, found, 8,
                        (done, total, v) => Report(new Progress
                        {
                            Stage = Stage.Probing,
                            Fraction = (double)done / Math.Max(1, total),
                            Message = "Memeriksa video " + done + "/" + total + "...",
                        }),
                        () => token.IsCancellationRequested);
                    Dispatcher.Invoke(() => OnScanDone(found));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => Fail("Gagal memindai folder:\n" + ex.Message));
                }
            });
        }

        private void OnScanDone(List<VideoFile> found)
        {
            SetBusy(false, "");
            Bar.Value = 0;

            // Tentukan nama keluaran DULU, baru buang dari hasil pindai.
            // Kebalikannya membuat penyaringan ini tidak berguna pada awal yang
            // bersih (kolomnya masih kosong), sehingga berkas hasil sesi
            // sebelumnya ikut terdaftar, tercentang, dan digabungkan ke dalam
            // dirinya sendiri pada proses berikutnya.
            if (string.IsNullOrWhiteSpace(TxtOutput.Text))
            {
                string folder = !string.IsNullOrWhiteSpace(TxtFolder.Text)
                    ? TxtFolder.Text : _settings["last_output_dir"];
                TxtOutput.Text = Path.Combine(folder ?? "", SuggestName());
            }
            string target = (TxtOutput.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(target))
            {
                try
                {
                    string full = Path.GetFullPath(target);
                    found.RemoveAll(f => string.Equals(Path.GetFullPath(f.Path), full,
                                                       StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception) { }
            }

            // Pemindaian yang dibatalkan meninggalkan berkas yang belum
            // diperiksa. Berkas itu tidak rusak - kita hanya belum tahu - jadi
            // katakan begitu, alih-alih menghitungnya sebagai rusak sambil
            // membiarkannya tercentang dan ikut tergabung.
            int unchecked_ = 0;
            foreach (var f in found)
            {
                if (f.Probed) continue;
                unchecked_++;
                f.Selected = false;
                f.Error = "Belum diperiksa - pemindaian dibatalkan";
            }

            _rows.Clear();
            foreach (var f in found) AddRow(f);

            if (found.Count == 0)
            {
                LblStatus.Text = "Tidak ada file video di folder ini.";
                UpdateSummary();
                return;
            }

            // Pemindaian baru tidak punya urutan buatan tangan untuk dijaga.
            CmbSort.SelectedItem = Labels.Of(_explicitSort);
            ApplySort();

            int valid = found.Count(f => f.Valid);
            int broken = found.Count - valid - unchecked_;
            var parts = new List<string> { valid + " video siap digabung" };
            if (broken > 0) parts.Add(broken + " dilewati (rusak/bukan video)");
            if (unchecked_ > 0) parts.Add(unchecked_ + " belum diperiksa karena dibatalkan");
            LblStatus.Text = string.Join(", ", parts) + ".";
        }

        private void AddRow(VideoFile file)
        {
            var row = new MergeRow(file);
            row.SelectionChanged += (s, e) => UpdateSummary();
            _rows.Add(row);
        }

        private string SuggestName()
        {
            string folder = (TxtFolder.Text ?? "").Trim().TrimEnd('\\', '/');
            string baseName = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(baseName)) baseName = "gabungan";
            return baseName + " - gabungan.mp4";
        }

        private void OnSortChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ApplySort();
        }

        private void ApplySort()
        {
            if (BusyGuard()) return;
            SortBy key = SelectedSort();
            bool desc = ChkDesc.IsChecked == true;

            if (key == SortBy.Manual)
            {
                // Tidak ada yang perlu diurutkan ulang, tetapi "Menurun" tetap
                // punya arti jelas pada urutan buatan tangan: balikkan. Tanpa
                // ini kotak centangnya tampak rusak bagi siapa pun yang sudah
                // menggeser baris.
                if (desc != _settings.GetBool("sort_desc"))
                {
                    var reversed = _rows.Reverse().ToList();
                    _rows.Clear();
                    foreach (var r in reversed) _rows.Add(r);
                }
                _settings.Set("sort_desc", desc);
                Renumber();
                return;
            }

            _explicitSort = key;
            var files = _rows.Select(r => r.File).ToList();
            var sorted = FileSorter.Sort(files, key, desc);
            var byPath = _rows.ToDictionary(r => r.File.Path, r => r,
                                            StringComparer.OrdinalIgnoreCase);
            _rows.Clear();
            foreach (var f in sorted)
            {
                MergeRow row;
                if (byPath.TryGetValue(f.Path, out row)) _rows.Add(row);
            }
            _settings.SortBy = key;
            _settings.Set("sort_desc", desc);
            Renumber();
        }

        private SortBy SelectedSort()
        {
            string label = CmbSort.SelectedItem as string ?? "";
            foreach (SortBy key in Enum.GetValues(typeof(SortBy)))
                if (Labels.Of(key) == label) return key;
            return SortBy.Name;
        }

        private MergeMode SelectedMode()
        {
            string label = CmbMode.SelectedItem as string ?? "";
            foreach (MergeMode mode in Enum.GetValues(typeof(MergeMode)))
                if (Labels.Of(mode) == label) return mode;
            return MergeMode.Auto;
        }

        private void OnModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) UpdateSummary();
        }

        private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
        private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

        private void Move(int delta)
        {
            if (BusyGuard()) return;
            var selected = GridFiles.SelectedItems.Cast<MergeRow>().ToList();
            if (selected.Count == 0) return;

            var list = _rows.ToList();
            var indices = selected.Select(r => list.IndexOf(r)).ToList();
            var moved = FileSorter.MoveItems(list, indices, delta);

            _rows.Clear();
            foreach (var r in list) _rows.Add(r);
            CmbSort.SelectedItem = Labels.Of(SortBy.Manual);
            Renumber();

            GridFiles.SelectedItems.Clear();
            foreach (int i in moved)
                if (i >= 0 && i < _rows.Count) GridFiles.SelectedItems.Add(_rows[i]);
            if (moved.Count > 0) GridFiles.ScrollIntoView(_rows[moved[0]]);
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            if (BusyGuard()) return;
            var selected = GridFiles.SelectedItems.Cast<MergeRow>().ToList();
            if (selected.Count == 0) return;
            // Lepaskan pilihan SEBELUM daftarnya diubah: tanpa ini penekanan
            // Delete berikutnya menghapus baris yang tidak pernah dipilih
            // pengguna, karena WPF memindahkan pilihan ke baris sesudahnya.
            GridFiles.SelectedItems.Clear();
            foreach (var row in selected) _rows.Remove(row);
            Renumber();
        }

        private void OnCheckAll(object sender, RoutedEventArgs e) => SetAll(true);
        private void OnUncheckAll(object sender, RoutedEventArgs e) => SetAll(false);

        private void SetAll(bool value)
        {
            if (BusyGuard()) return;
            foreach (var row in _rows) row.Selected = value;
            UpdateSummary();
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete) { OnRemove(sender, null); e.Handled = true; }
            else if (e.Key == Key.Space)
            {
                if (BusyGuard()) { e.Handled = true; return; }
                foreach (var row in GridFiles.SelectedItems.Cast<MergeRow>().ToList())
                    row.Selected = !row.Selected;
                UpdateSummary();
                e.Handled = true;
            }
        }

        private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var row = GridFiles.SelectedItem as MergeRow;
            if (row != null) Paths.RevealInExplorer(row.File.Path);
        }

        private void Renumber()
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].Index = i + 1;
            UpdateSummary();
        }

        /// <summary>
        /// Tabel kosong yang benar-benar kosong hanya terlihat seperti aplikasi
        /// yang rusak. Pesan ini yang memberi tahu apa yang harus dilakukan.
        /// </summary>
        private void UpdateEmptyStates()
        {
            LblEmptyFiles.Visibility = _rows.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
            LblEmptySubs.Visibility = _subRows.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowHardware()
        {
            // WMI bisa memakan ratusan milidetik di mesin lama, jadi tidak
            // pernah di thread UI.
            Task.Run(() =>
            {
                string summary = Hardware.Summary();
                Dispatcher.BeginInvoke(new Action(() => LblHardware.Text = summary),
                                       DispatcherPriority.Background);
            });
        }

        private void UpdateSummary()
        {
            var chosen = _rows.Where(r => r.Selected && r.File.Valid)
                              .Select(r => r.File).ToList();
            double total = chosen.Sum(f => f.Duration);
            long size = chosen.Sum(f => f.Size);
            string text = chosen.Count + " video dipilih  |  total "
                          + Humanize.Duration(total) + "  |  " + Humanize.Size(size);
            if (chosen.Count >= 2)
            {
                List<string> reasons;
                bool ok = Prober.CanStreamCopy(chosen, out reasons);
                text += "  |  " + (ok ? "dapat digabung cepat (tanpa encode ulang)"
                                      : "perlu encode ulang");
            }
            LblSummary.Text = text;
            UpdateEmptyStates();
        }

        private void OnChooseOutput(object sender, RoutedEventArgs e)
        {
            if (BusyGuard()) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Simpan video gabungan sebagai",
                FileName = SuggestName(),
                DefaultExt = ".mp4",
                Filter = "Video MP4|*.mp4|Matroska MKV|*.mkv|QuickTime MOV|*.mov"
                         + "|Semua file|*.*",
                InitialDirectory = FirstExisting(_settings["last_output_dir"],
                                                 TxtFolder.Text),
            };
            if (dialog.ShowDialog() == true)
            {
                TxtOutput.Text = dialog.FileName;
                _settings.Set("last_output_dir", Path.GetDirectoryName(dialog.FileName));
                _settings.Save();
            }
        }

        // ========================================== tab 1: penggabungan --
        private void StartMerge()
        {
            if (_busy || _tools == null) return;

            // Pemindaian yang diinterupsi meninggalkan berkas yang belum
            // dilihat siapa pun. Menggabung sekarang akan diam-diam
            // menghasilkan video yang hanya berisi bagian yang sempat
            // diperiksa - 66 klip dari 480, tanpa peringatan apa pun.
            int unchecked_ = _rows.Count(r => !r.File.Probed);
            if (unchecked_ > 0)
            {
                MessageBox.Show(
                    unchecked_ + " video belum sempat diperiksa karena pemindaian "
                    + "dibatalkan.\n\nKalau digabung sekarang, video-video itu "
                    + "TIDAK akan ikut.\n\nTekan \"Muat Ulang\" dan tunggu sampai "
                    + "pemeriksaan selesai.", AppInfo.Name,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var chosen = _rows.Where(r => r.Selected && r.File.Valid)
                              .Select(r => r.File).ToList();
            if (chosen.Count < 2)
            {
                MessageBox.Show("Pilih minimal 2 video yang valid untuk digabungkan.",
                                AppInfo.Name, MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            string output = (TxtOutput.Text ?? "").Trim();
            if (string.IsNullOrEmpty(output))
            {
                OnChooseOutput(null, null);
                output = (TxtOutput.Text ?? "").Trim();
                if (string.IsNullOrEmpty(output)) return;
            }
            if (File.Exists(output)
                && MessageBox.Show("File berikut sudah ada dan akan ditimpa:\n\n"
                                   + output + "\n\nLanjutkan?", AppInfo.Name,
                                   MessageBoxButton.YesNo, MessageBoxImage.Question)
                   != MessageBoxResult.Yes)
                return;

            MergeMode mode = SelectedMode();
            string encoder = SelectedEncoder();

            double total = chosen.Sum(f => f.Duration);
            bool copyable = false;
            if (mode == MergeMode.Auto || mode == MergeMode.Copy)
            {
                List<string> reasons;
                copyable = Prober.CanStreamCopy(chosen, out reasons);
            }
            if (!copyable && total > 3600
                && MessageBox.Show(
                    "Video harus di-encode ulang karena parameternya berbeda.\n\n"
                    + "Total durasi " + Humanize.Duration(total) + " - proses ini "
                    + "bisa memakan waktu berjam-jam.\n\nLanjutkan?", AppInfo.Name,
                    MessageBoxButton.YesNo, MessageBoxImage.Question)
                   != MessageBoxResult.Yes)
                return;

            int crf = CrfValue(TxtCrf, "crf");
            // Pilihan encoder disimpan oleh OnEncoderChanged, bukan di sini.
            // Menyimpannya sekarang akan menulis hasil "Otomatis" ke dalam
            // preferensi MANUAL, sehingga mematikan mode otomatis nanti
            // memberi pilihan yang sebenarnya tidak pernah ditunjuk pengguna.
            _settings.Set("last_output_dir", Path.GetDirectoryName(output) ?? "");
            SaveSettings();

            var job = new MergeJob
            {
                Files = chosen,
                OutputPath = output,
                Mode = mode,
                Target = new TargetSpec { Crf = crf, Preset = _settings["preset"] },
                HwaccelEncoder = encoder,
                Faststart = _settings.GetBool("faststart"),
            };

            var merger = new Merger(_tools, job, Report, AppendLog);
            _task = merger;
            _resultPath = output;
            SetBusy(true, "Memulai...");
            ClearLog();
            BtnOpen.IsEnabled = false;

            _worker = Task.Run(() =>
            {
                try
                {
                    string path = merger.Run();
                    Dispatcher.Invoke(() => OnMergeDone(path));
                }
                catch (CancelledException)
                {
                    Dispatcher.Invoke(OnCancelled);
                }
                catch (MergeException ex)
                {
                    Dispatcher.Invoke(() => Fail(ex.Message));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => Fail("Kesalahan tak terduga:\n" + ex.Message));
                }
            });
        }

        private void OnMergeDone(string path)
        {
            SetBusy(false, "Selesai.");
            Bar.Value = 1000;
            BtnOpen.IsEnabled = true;
            _resultPath = path;
            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (MessageBox.Show("Penggabungan selesai.\n\n" + path + "\nUkuran: "
                                + Humanize.Size(size) + "\n\nBuka folder hasil sekarang?",
                                AppInfo.Name, MessageBoxButton.YesNo,
                                MessageBoxImage.Information) == MessageBoxResult.Yes)
                Paths.RevealInExplorer(path);
        }

        // ============================================ tab 2: hardsub --
        private void OnSubFolderKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) SubRescan();
        }

        private void OnSubChooseFolder(object sender, RoutedEventArgs e)
        {
            if (BusyGuard()) return;
            string folder = PickFolder("Pilih folder berisi video", TxtSubFolder.Text);
            if (!string.IsNullOrEmpty(folder)) { TxtSubFolder.Text = folder; SubRescan(); }
        }

        private void OnSubChooseFiles(object sender, RoutedEventArgs e)
        {
            if (BusyGuard()) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pilih video",
                Multiselect = true,
                Filter = "Video|*.mp4;*.mkv;*.avi;*.mov;*.ts;*.m4v;*.webm|Semua file|*.*",
                InitialDirectory = FirstExisting(TxtSubFolder.Text),
            };
            if (dialog.ShowDialog() != true) return;

            var videos = dialog.FileNames.Select(p => new VideoFile
            {
                Path = p,
                Size = SafeLength(p),
            }).ToList();
            LoadSubVideos(videos);
        }

        private void OnSubRescan(object sender, RoutedEventArgs e) => SubRescan();

        private void SubRescan()
        {
            string folder = (TxtSubFolder.Text ?? "").Trim();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            if (BusyGuard()) return;
            if (_tools == null) { DetectFFmpeg(); if (_tools == null) return; }

            _settings.Set("hardsub_input_dir", folder);
            _settings.Save();

            var found = Scanner.ScanFolder(folder, false);
            if (found.Count == 0)
            {
                _subRows.Clear();
                UpdateSubSummary();
                LblStatus.Text = "Tidak ada file video di folder ini.";
                return;
            }
            LoadSubVideos(FileSorter.Sort(found, SortBy.Name));
        }

        private void LoadSubVideos(List<VideoFile> videos)
        {
            var tools = _tools;
            if (tools == null) return;

            _subRows.Clear();
            _scanCancel = new CancellationTokenSource();
            var token = _scanCancel.Token;
            SetBusy(true, "Memeriksa " + videos.Count + " video...");

            _worker = Task.Run(() =>
            {
                try
                {
                    Prober.ProbeMany(tools, videos, 8, null,
                                     () => token.IsCancellationRequested);
                    var usable = videos.Where(v => v.Valid).ToList();
                    var items = Hardsubber.CollectSources(
                        tools, usable, () => token.IsCancellationRequested);
                    Dispatcher.Invoke(() => OnSubScanDone(items));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => Fail("Gagal memeriksa video:\n" + ex.Message));
                }
            });
        }

        private void OnSubScanDone(List<HardsubItem> items)
        {
            SetBusy(false, "");
            Bar.Value = 0;
            _subRows.Clear();
            foreach (var item in items)
            {
                var row = new SubRow(item);
                row.SelectionChanged += (s, e) => UpdateSubSummary();
                _subRows.Add(row);
            }
            for (int i = 0; i < _subRows.Count; i++) _subRows[i].Index = i + 1;

            int ready = items.Count(i => i.HasSource);
            int without = items.Count - ready;
            var parts = new List<string> { ready + " video siap dibakar subtitle" };
            if (without > 0) parts.Add(without + " tanpa subtitle");
            LblStatus.Text = string.Join(", ", parts) + ".";
            UpdateSubSummary();
            SyncPicker();
        }

        private void UpdateSubSummary()
        {
            var chosen = _subRows.Where(r => r.Selected).ToList();
            double total = chosen.Sum(r => r.Item.Video.Duration);
            long size = chosen.Sum(r => r.Item.Video.Size);
            LblSubSummary.Text = chosen.Count + " video dipilih  |  "
                + Humanize.Duration(total) + "  |  " + Humanize.Size(size)
                + "  |  selalu encode ulang";
            UpdateEmptyStates();
        }

        private void OnSubCheckAll(object sender, RoutedEventArgs e) => SetSubAll(true);
        private void OnSubUncheckAll(object sender, RoutedEventArgs e) => SetSubAll(false);

        private void SetSubAll(bool value)
        {
            if (BusyGuard()) return;
            foreach (var row in _subRows) row.Selected = value;
            UpdateSubSummary();
        }

        private void OnSubRemove(object sender, RoutedEventArgs e)
        {
            if (BusyGuard()) return;
            var selected = GridSubs.SelectedItems.Cast<SubRow>().ToList();
            if (selected.Count == 0) return;
            GridSubs.SelectedItems.Clear();
            foreach (var row in selected) _subRows.Remove(row);
            for (int i = 0; i < _subRows.Count; i++) _subRows[i].Index = i + 1;
            UpdateSubSummary();
        }

        private void OnSubGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete) { OnSubRemove(sender, null); e.Handled = true; }
        }

        private void OnSubDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var row = GridSubs.SelectedItem as SubRow;
            if (row != null) Paths.RevealInExplorer(row.Item.Video.Path);
        }

        private void OnSubSelectionChanged(object sender, SelectionChangedEventArgs e)
            => SyncPicker();

        /// <summary>Tampilkan pilihan milik baris yang sedang disorot.</summary>
        private void SyncPicker()
        {
            var targets = GridSubs.SelectedItems.Cast<SubRow>().ToList();
            CmbSubSource.SelectionChanged -= OnSubSourceChanged;
            CmbSubSource.Items.Clear();

            if (targets.Count == 0)
            {
                CmbSubSource.IsEnabled = false;
                CmbSubSource.SelectionChanged += OnSubSourceChanged;
                return;
            }

            var item = targets[0].Item;
            _sourceOptions = OptionsFor(item);
            foreach (var option in _sourceOptions) CmbSubSource.Items.Add(option.Item1);
            CmbSubSource.IsEnabled = _sourceOptions.Count > 0;

            foreach (var option in _sourceOptions)
            {
                if (option.Item2 == "track" && ReferenceEquals(item.Track, option.Item3))
                { CmbSubSource.SelectedItem = option.Item1; break; }
                if (option.Item2 == "file"
                    && string.Equals(item.ExternalPath, (string)option.Item3,
                                     StringComparison.OrdinalIgnoreCase))
                { CmbSubSource.SelectedItem = option.Item1; break; }
            }
            CmbSubSource.SelectionChanged += OnSubSourceChanged;
        }

        /// <summary>Setiap subtitle yang bisa dipakai video ini.</summary>
        private static List<Tuple<string, string, object>> OptionsFor(HardsubItem item)
        {
            var options = new List<Tuple<string, string, object>>();
            foreach (var track in item.Tracks)
                if (track.Burnable)
                    options.Add(Tuple.Create("Di dalam video: " + track.Label,
                                             "track", (object)track));
            foreach (string path in item.Sidecars)
                options.Add(Tuple.Create("Berkas: " + Path.GetFileName(path),
                                         "file", (object)path));
            if (!string.IsNullOrEmpty(item.ExternalPath)
                && !item.Sidecars.Any(p => string.Equals(p, item.ExternalPath,
                                                         StringComparison.OrdinalIgnoreCase)))
                options.Add(Tuple.Create(
                    "Berkas: " + Path.GetFileName(item.ExternalPath),
                    "file", (object)item.ExternalPath));
            return options;
        }

        /// <summary>
        /// Terapkan subtitle terpilih ke setiap baris yang disorot.
        ///
        /// Kalau beberapa baris dipilih, yang disalin adalah JENISNYA, bukan
        /// nilainya: tiap episode memakai trek #2 miliknya sendiri, bukan objek
        /// trek milik episode pertama. Sebaliknya akan membakar subtitle berkas
        /// pertama ke semua berkas.
        /// </summary>
        private void OnSubSourceChanged(object sender, SelectionChangedEventArgs e)
        {
            string chosen = CmbSubSource.SelectedItem as string;
            if (chosen == null) return;
            var entry = _sourceOptions.FirstOrDefault(o => o.Item1 == chosen);
            if (entry == null) return;

            var targets = GridSubs.SelectedItems.Cast<SubRow>().ToList();
            for (int index = 0; index < targets.Count; index++)
            {
                var item = targets[index].Item;
                if (entry.Item2 == "track")
                {
                    var wanted = (SubtitleTrack)entry.Item3;
                    var match = item.Tracks.FirstOrDefault(
                        t => t.StreamIndex == wanted.StreamIndex && t.Burnable);
                    if (match == null && index > 0) continue;   // baris itu tak punya
                    item.Track = match ?? wanted;
                    item.ExternalPath = "";
                }
                else
                {
                    string path = (string)entry.Item3;
                    // Untuk baris lain, utamakan berkas pendamping miliknya sendiri.
                    item.ExternalPath = index == 0 ? path
                        : (item.Sidecars.Count > 0 ? item.Sidecars[0] : path);
                    item.Track = null;
                }
                item.Error = "";
                item.Selected = true;
                targets[index].Refresh();
            }
            UpdateSubSummary();
        }

        private void OnSubChooseFile(object sender, RoutedEventArgs e)
        {
            if (BusyGuard()) return;
            var targets = GridSubs.SelectedItems.Cast<SubRow>().ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("Pilih dulu baris video di daftar di atas.", AppInfo.Name);
                return;
            }
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pilih berkas subtitle",
                Filter = "Berkas subtitle|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.smi;*.ttml"
                         + "|SubRip (.srt)|*.srt|Advanced SubStation (.ass)|*.ass"
                         + "|Semua file|*.*",
                InitialDirectory = Path.GetDirectoryName(targets[0].Item.Video.Path),
            };
            if (dialog.ShowDialog() != true) return;

            foreach (var row in targets)
            {
                row.Item.ExternalPath = dialog.FileName;
                row.Item.Track = null;
                row.Item.Error = "";
                row.Item.Selected = true;
                row.Refresh();
            }
            UpdateSubSummary();
            SyncPicker();
        }

        private void OnStyleToggled(object sender, RoutedEventArgs e)
        {
            bool on = ChkStyle.IsChecked == true;
            foreach (var control in new Control[] { CmbFont, TxtSubSize, BtnColour,
                                                    BtnOutline, ChkBold })
                control.IsEnabled = on;
        }

        private void OnPickPrimary(object sender, RoutedEventArgs e)
            => PickColour("sub_primary", BtnColour);

        private void OnPickOutline(object sender, RoutedEventArgs e)
            => PickColour("sub_outline_color", BtnOutline);

        private void PickColour(string key, Control button)
        {
            using (var dialog = new System.Windows.Forms.ColorDialog())
            {
                var current = ParseColour(_settings[key]);
                dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                dialog.FullOpen = true;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string hex = "#" + dialog.Color.R.ToString("X2")
                             + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
                _settings.Set(key, hex);
                PaintSwatch(button, hex);
            }
        }

        private static void PaintSwatch(Control button, string hex)
        {
            var colour = ParseColour(hex);
            button.Background = new SolidColorBrush(colour);
        }

        private static Color ParseColour(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex ?? "#FFFFFF"); }
            catch (Exception) { return Colors.White; }
        }

        private void OnChooseOutDir(object sender, RoutedEventArgs e)
        {
            if (BusyGuard()) return;
            string folder = PickFolder("Folder untuk menyimpan hasil",
                                       FirstExisting(TxtOutDir.Text, TxtSubFolder.Text));
            if (!string.IsNullOrEmpty(folder)) TxtOutDir.Text = folder;
        }

        private void StartHardsub()
        {
            if (_busy || _tools == null) return;

            var chosen = _subRows.Where(r => r.Selected).Select(r => r.Item).ToList();
            if (chosen.Count == 0)
            {
                MessageBox.Show("Pilih minimal satu video untuk diberi subtitle.",
                                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var missing = chosen.Where(i => !i.HasSource).ToList();
            if (missing.Count > 0)
            {
                MessageBox.Show(
                    missing.Count + " video belum punya subtitle.\n\nPilih barisnya "
                    + "lalu tentukan subtitle di kotak \"Subtitle untuk baris "
                    + "terpilih\", atau lepas centangnya.", AppInfo.Name,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string suffix = TxtSuffix.Text ?? "";
            string outDir = (TxtOutDir.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(suffix) && string.IsNullOrEmpty(outDir))
            {
                MessageBox.Show(
                    "Tanpa akhiran nama dan tanpa folder tujuan, hasilnya akan "
                    + "menimpa video aslinya.\n\nIsi salah satu dari keduanya.",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double total = chosen.Sum(i => i.Video.Duration);
            if (total > 1800
                && MessageBox.Show(
                    "Membakar subtitle selalu meng-encode ulang gambar.\n\n"
                    + chosen.Count + " video, total " + Humanize.Duration(total)
                    + " - proses ini bisa memakan waktu lama.\n\nLanjutkan?",
                    AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question)
                   != MessageBoxResult.Yes)
                return;

            int crf = CrfValue(TxtSubCrf, "hardsub_crf");
            string encoder = SelectedEncoder();
            SaveSettings();

            var job = new HardsubJob
            {
                Items = chosen,
                OutputDir = outDir,
                Suffix = suffix,
                Container = (CmbContainer.SelectedItem as string) ?? ".mp4",
                Style = new SubtitleStyle
                {
                    Enabled = ChkStyle.IsChecked == true,
                    Font = (CmbFont.SelectedItem as string) ?? "Arial",
                    Size = ParseInt(TxtSubSize.Text, 24, 8, 200),
                    Primary = _settings["sub_primary"],
                    OutlineColor = _settings["sub_outline_color"],
                    Outline = _settings.GetDouble("sub_outline"),
                    Bold = ChkBold.IsChecked == true,
                    MarginV = _settings.GetInt("sub_margin_v", 0, 400),
                },
                Target = new TargetSpec { Crf = crf, Preset = _settings["preset"] },
                HwaccelEncoder = encoder,
                CopyAudio = ChkCopyAudio.IsChecked == true,
                Faststart = _settings.GetBool("faststart"),
            };

            var task = new Hardsubber(_tools, job, Report, AppendLog);
            _task = task;
            SetBusy(true, "Memulai...");
            ClearLog();
            BtnOpen.IsEnabled = false;

            _worker = Task.Run(() =>
            {
                try
                {
                    var result = task.Run();
                    Dispatcher.Invoke(() => OnHardsubDone(result));
                }
                catch (CancelledException)
                {
                    Dispatcher.Invoke(OnCancelled);
                }
                catch (MergeException ex)
                {
                    Dispatcher.Invoke(() => Fail(ex.Message));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => Fail("Kesalahan tak terduga:\n" + ex.Message));
                }
            });
        }

        private void OnHardsubDone(HardsubResult result)
        {
            SetBusy(false, "Selesai.");
            Bar.Value = 1000;
            foreach (var row in _subRows) row.Refresh();

            if (result.Done.Count > 0)
            {
                _resultPath = result.Done[0];
                BtnOpen.IsEnabled = true;
            }

            var sb = new StringBuilder();
            sb.AppendLine(result.Done.Count + " video selesai dibakar subtitle.");
            if (result.Failed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(result.Failed.Count + " gagal:");
                foreach (var fail in result.Failed.Take(6))
                    sb.AppendLine("  - " + fail.Key + ": " + fail.Value.Split('\n')[0]);
            }
            sb.AppendLine();
            sb.Append("Buka folder hasil sekarang?");

            if (MessageBox.Show(sb.ToString(), AppInfo.Name, MessageBoxButton.YesNo,
                                MessageBoxImage.Information) == MessageBoxResult.Yes
                && result.Done.Count > 0)
                Paths.RevealInExplorer(result.Done[0]);
        }

        // ==================================================== bersama --
        private void OnTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || BtnPrimary == null) return;
            BtnPrimary.Content = Tabs.SelectedIndex == 0
                ? "GABUNGKAN VIDEO" : "BAKAR SUBTITLE";
        }

        private void OnPrimary(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedIndex == 0) StartMerge();
            else StartHardsub();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            _scanCancel.Cancel();
            if (_task != null) _task.Cancel();
            LblStatus.Text = "Membatalkan...";
            BtnCancel.IsEnabled = false;
        }

        private void OnCancelled()
        {
            SetBusy(false, "Dibatalkan.");
            Bar.Value = 0;
        }

        private void Fail(string message)
        {
            SetBusy(false, "Gagal.");
            Bar.Value = 0;
            MessageBox.Show(message, AppInfo.Name, MessageBoxButton.OK,
                            MessageBoxImage.Error);
        }

        private void OnOpenResult(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_resultPath) && File.Exists(_resultPath))
                Paths.RevealInExplorer(_resultPath);
        }

        /// <summary>Dipanggil dari thread pekerja; menyeberang ke thread UI di sini.</summary>
        private void Report(Progress p)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Bar.Value = Math.Max(0, Math.Min(1000, p.Fraction * 1000));
                if (!string.IsNullOrEmpty(p.Message)) LblStatus.Text = p.Message;

                var bits = new List<string>();
                if (p.Speed > 0)
                    bits.Add(p.Speed.ToString("0.00", CultureInfo.InvariantCulture) + "x");
                if (p.EtaSeconds > 0) bits.Add("sisa " + Humanize.Eta(p.EtaSeconds));
                if (p.OutputSize > 0) bits.Add(Humanize.Size(p.OutputSize));
                LblEta.Text = string.Join("   ", bits);
            }), DispatcherPriority.Background);
        }

        /// <summary>
        /// Baris log dikumpulkan lalu ditulis serentak: ffmpeg bisa
        /// mengeluarkan ratusan baris per detik, dan menyisipkannya satu per
        /// satu ke TextBox membuat jendelanya tersendat.
        /// </summary>
        private void AppendLog(string line)
        {
            lock (_pendingLog)
            {
                _pendingLog.Add(line);
                if (_pendingLog.Count > 2000) _pendingLog.RemoveRange(0, 1000);
            }
        }

        private void FlushLog()
        {
            string[] lines;
            lock (_pendingLog)
            {
                if (_pendingLog.Count == 0) return;
                lines = _pendingLog.ToArray();
                _pendingLog.Clear();
            }
            if (LogBox.Visibility != Visibility.Visible) return;

            TxtLog.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);

            int limit = _settings.GetInt("keep_log_lines", 50, 100000);
            if (TxtLog.LineCount > limit * 2)
            {
                var kept = TxtLog.Text.Split('\n');
                TxtLog.Text = string.Join("\n", kept.Skip(kept.Length - limit));
            }
            TxtLog.ScrollToEnd();
        }

        private void ClearLog()
        {
            lock (_pendingLog) _pendingLog.Clear();
            TxtLog.Clear();
        }

        private void OnToggleLog(object sender, RoutedEventArgs e)
        {
            LogBox.Visibility = ChkLog.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetBusy(bool busy, string status = "")
        {
            _busy = busy;
            BtnPrimary.IsEnabled = !busy;
            BtnCancel.IsEnabled = busy;
            if (!string.IsNullOrEmpty(status)) LblStatus.Text = status;
            Mouse.OverrideCursor = busy ? Cursors.AppStarting : null;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_busy)
            {
                if (MessageBox.Show("Proses masih berjalan. Batalkan dan keluar?",
                                    AppInfo.Name, MessageBoxButton.YesNo,
                                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                OnCancel(null, null);
                // Pekerjanya berjalan di thread pool. Menutup jendela sekarang
                // akan meninggalkan folder .vmerge_tmp_* - yang untuk pekerjaan
                // panjang bisa berisi puluhan GB klip ternormalisasi - selamanya.
                // Beri waktu sebentar untuk membereskan dirinya sendiri.
                LblStatus.Text = "Menghentikan dan membersihkan...";
                var worker = _worker;
                if (worker != null)
                {
                    try { worker.Wait(TimeSpan.FromSeconds(15)); }
                    catch (Exception) { }
                }
            }
            SaveSettings();
        }

        // ===================================================== pembantu --
        private int CrfValue(TextBox box, string key)
        {
            // Kotak teks bisa dikosongkan pengguna; membacanya sebagai angka
            // lalu gagal di dalam penangan tombol membuat tombolnya tampak
            // tidak melakukan apa-apa sama sekali.
            int value = ParseInt(box.Text, _settings.GetInt(key, 0, 51), 0, 51);
            box.Text = value.ToString(CultureInfo.InvariantCulture);
            return value;
        }

        private static int ParseInt(string text, int fallback, int low, int high)
        {
            int value;
            if (!int.TryParse((text ?? "").Trim(), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out value))
                value = fallback;
            return Math.Max(low, Math.Min(high, value));
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (Exception) { return 0; }
        }

        private static string FirstExisting(params string[] candidates)
        {
            foreach (string candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                    return candidate;
            return "";
        }

        private static string PickFolder(string title, string start = "")
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = title;
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
                    dialog.SelectedPath = start;
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    ? dialog.SelectedPath : "";
            }
        }
    }
}
