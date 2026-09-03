using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace VideoMerger.Core
{
    /// <summary>
    /// Menyusun dan menjalankan perintah ffmpeg yang menghasilkan berkas gabungan.
    ///
    /// Tiga jalur, makin ke bawah makin mahal:
    ///
    ///   COPY      satu kali ffmpeg, concat demuxer + <c>-c copy</c>. Tidak ada
    ///             decoding sama sekali, jadi 100 klip 5 menit selesai dalam
    ///             hitungan detik. Hanya sah bila semua klip berbagi parameter
    ///             codec yang sama.
    ///
    ///   SMART     encode ulang hanya klip yang berbeda dari mayoritas, lalu
    ///             sambung semuanya dengan salin. Klip hasil encode diperiksa
    ///             ulang, dan kalau ternyata masih tidak sejajar, mundur ke
    ///             REENCODE.
    ///
    ///   REENCODE  seragamkan setiap klip ke satu target, lalu sambung dengan
    ///             salin. Sengaja BUKAN satu <c>-filter_complex concat</c>
    ///             besar: dengan 100 masukan itu membuka 100 berkas sekaligus,
    ///             melewati batas panjang baris perintah Windows, dan hanya
    ///             melaporkan satu angka kemajuan yang tidak berarti. Satu
    ///             lintasan per klip memberi kemajuan per klip dan selamat
    ///             dari satu berkas rusak.
    /// </summary>
    public class Merger : FFmpegTask
    {
        /// <summary>
        /// Container yang bisa ditulis ulang ffmpeg dengan andal.
        /// AppInfo.VideoExtensions sengaja lebih luas daripada ini karena
        /// daftar itu menyebut apa yang bisa DIBACA; .dav, .264 dan kawannya
        /// bisa dibongkar tetapi tidak punya muxer keluaran.
        /// </summary>
        public static readonly HashSet<string> MuxableExtensions =
            new HashSet<string>(new[]
            {
                ".mp4", ".m4v", ".mkv", ".mov", ".ts", ".avi", ".webm",
                ".flv", ".mpg",
            }, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Nama codec dari ffprobe -> ejaan encoder yang diterima ffmpeg.
        /// ffprobe melaporkan nama DECODER, yang tidak selalu sama dengan nama
        /// encoder-nya.
        /// </summary>
        public static readonly Dictionary<string, string> VideoEncoders =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "h264", "libx264" }, { "hevc", "libx265" },
                { "vp9", "libvpx-vp9" }, { "vp8", "libvpx" },
                { "av1", "libsvtav1" }, { "theora", "libtheora" },
                { "mpeg4", "mpeg4" }, { "mpeg2video", "mpeg2video" },
            };

        // "opus" dan "vorbis" bisa didekode apa adanya, tetapi hanya libopus
        // dan libvorbis yang bisa meng-encode; meneruskan hasil probe mentah
        // membuat ffmpeg berhenti dengan "Unknown encoder".
        public static readonly Dictionary<string, string> AudioEncoders =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "aac", "aac" }, { "", "aac" }, { "mp3", "libmp3lame" },
                { "ac3", "ac3" }, { "eac3", "eac3" }, { "opus", "libopus" },
                { "vorbis", "libvorbis" }, { "flac", "flac" },
                { "alac", "alac" }, { "pcm_s16le", "pcm_s16le" },
            };

        public static readonly Dictionary<string, string> X264Profiles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "constrained baseline", "baseline" },
                { "baseline", "baseline" },
                { "main", "main" },
                { "high", "high" },
                { "high 10", "high10" },
                { "high 4:2:2", "high422" },
                { "high 4:4:4 predictive", "high444" },
            };

        public MergeJob Job { get; private set; }
        private string _tempDir = "";
        private readonly Stopwatch _clock = new Stopwatch();

        public Merger(FFmpegTools tools, MergeJob job,
                      Action<Progress> onProgress = null,
                      Action<string> onLog = null)
            : base(tools, onProgress, onLog)
        {
            Job = job;
        }

        // ------------------------------------------------ daftar concat I/O --
        /// <summary>
        /// Format satu jalur untuk direktif <c>file '...'</c> pada concat demuxer.
        ///
        /// Backslash asli Windows DIPERTAHANKAN. Menggantinya jadi garis miring
        /// adalah saran yang populer tetapi tidak memperbaiki apa pun sendirian
        /// - tanda kutip yang tidak di-escape tetap gagal - dan justru merusak
        /// jalur UNC (<c>\\server\share</c> akan jadi <c>//server/share</c>).
        ///
        /// Satu-satunya escape yang benar-benar penting adalah tanda kutip
        /// tunggal: ia akan menutup string, jadi ditulis <c>'\''</c> - tutup,
        /// kutip ter-escape, buka lagi.
        /// </summary>
        public static string EscapeConcatPath(string path)
        {
            return Path.GetFullPath(path).Replace("'", "'\\''");
        }

        /// <summary>
        /// Tulis berkas daftar concat lalu kembalikan jalurnya.
        ///
        /// Ditulis sebagai UTF-8 TANPA BOM dengan sengaja: BOM di depan membuat
        /// ffmpeg menolak baris pertamanya dengan <c>unknown keyword '?file'</c>.
        ///
        /// Tidak ada direktif <c>duration</c> yang ditulis. Direktif itu akan
        /// membuat ffprobe melaporkan total panjang masukan concat di muka -
        /// yang tidak dibutuhkan aplikasi ini karena durasinya sudah dijumlahkan
        /// sendiri - dan durasi yang meleset beberapa milidetik dari paket
        /// sebenarnya akan menggeser seluruh segmen sesudahnya.
        /// </summary>
        public static string WriteConcatList(IEnumerable<string> paths, string listPath)
        {
            var sb = new StringBuilder();
            sb.Append("ffconcat version 1.0\n");
            foreach (string path in paths)
                sb.Append("file '").Append(EscapeConcatPath(path)).Append("'\n");
            File.WriteAllText(listPath, sb.ToString(), new UTF8Encoding(false));
            return listPath;
        }

        /// <summary>"1/15360" -> 15360. Mengembalikan 0 bila time base tak dikenal.</summary>
        public static int TimescaleOf(string timeBase)
        {
            if (string.IsNullOrEmpty(timeBase)) return 0;
            string[] bits = timeBase.Split('/');
            int num, den;
            if (bits.Length != 2
                || !int.TryParse(bits[0], NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out num)
                || !int.TryParse(bits[1], NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out den))
                return 0;
            return (num == 1 && den > 0) ? den : 0;
        }

        // ---------------------------------------------------------- utama --
        public string Run()
        {
            _clock.Restart();
            var files = new List<VideoFile>();
            foreach (var f in Job.Files) if (f.Selected && f.Valid) files.Add(f);

            if (files.Count < 1)
                throw new MergeException("Tidak ada video valid yang dipilih.");
            if (files.Count == 1)
                throw new MergeException(
                    "Hanya satu video yang dipilih - tidak ada yang digabung.");

            string outPath = Job.OutputPath;
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                throw new MergeException(
                    "Folder tujuan tidak bisa dibuat atau ditulisi:"
                    + NL2 + outPath + NL2 + ex.Message);
            }
            if (!Job.Overwrite) outPath = Paths.Unique(outPath);
            CheckOutputNotInput(files, outPath);

            MergeMode mode = DecideMode(files);
            CheckInputsReadable(files);
            CheckDisk(files, outPath, mode);

            try
            {
                if (mode == MergeMode.Copy) RunCopy(files, outPath);
                else RunReencode(files, outPath, mode == MergeMode.Smart);
            }
            catch (CancelledException)
            {
                CleanupTemp();
                RemovePartial(outPath);
                Emit(new Progress
                {
                    Stage = Stage.Cancelled,
                    Message = "Dibatalkan oleh pengguna.",
                });
                throw;
            }
            catch (Exception)
            {
                CleanupTemp();
                throw;
            }

            try
            {
                double expected = 0;
                foreach (var f in files) expected += f.Duration;
                VerifyOutput(outPath, expected, files, mode);
            }
            finally
            {
                // Verifikasi melempar pada hasil yang buruk, dan jalur itu dulu
                // melewatkan pembersihan sama sekali - meninggalkan seluruh
                // klip ternormalisasi di disk (puluhan GB untuk pekerjaan
                // panjang) sekaligus mengotori pemindaian folder berikutnya.
                CleanupTemp();
            }

            long size = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            Emit(new Progress
            {
                Stage = Stage.Done,
                Fraction = 1.0,
                OutputSize = size,
                Message = "Selesai dalam "
                          + Humanize.Duration(_clock.Elapsed.TotalSeconds) + ".",
            });
            return outPath;
        }

        private static readonly string NL = Environment.NewLine;
        private static readonly string NL2 = Environment.NewLine + Environment.NewLine;

        // ---------------------------------------------------- verifikasi --
        /// <summary>
        /// Buka setiap masukan sebelum mulai.
        ///
        /// Kalau sebuah berkas hilang atau terkunci di tengah penggabungan,
        /// ffmpeg berhenti di situ, merampungkan container, dan keluar dengan 0.
        /// Pada pekerjaan 8 jam itu berarti "selesai" yang riang di atas video
        /// berdurasi beberapa menit - jadi daftarnya divalidasi di depan, yang
        /// sekaligus menangkap drive jaringan yang putus antara pemindaian dan
        /// penggabungan.
        /// </summary>
        private void CheckInputsReadable(List<VideoFile> files)
        {
            var missing = new List<string>();
            foreach (var f in files)
            {
                try
                {
                    using (var stream = File.Open(f.Path, FileMode.Open,
                                                  FileAccess.Read, FileShare.ReadWrite))
                        stream.ReadByte();
                }
                catch (Exception ex)
                {
                    missing.Add(f.Name + " (" + ex.Message + ")");
                }
            }
            if (missing.Count == 0) return;

            var sb = new StringBuilder();
            sb.Append("Video berikut tidak bisa dibaca lagi (terhapus, dipindah, ")
              .Append("sedang dipakai program lain, atau drive jaringan terputus):")
              .Append(NL2);
            for (int i = 0; i < Math.Min(10, missing.Count); i++)
                sb.Append("- ").Append(missing[i]).Append(NL);
            if (missing.Count > 10)
                sb.Append("... dan ").Append(missing.Count - 10).Append(" lainnya");
            throw new MergeException(sb.ToString());
        }

        /// <summary>
        /// Seberapa jauh durasi hasil boleh meleset sebelum dianggap gagal.
        ///
        /// Persentase adalah bentuk yang salah di sini. Pada ukuran yang jadi
        /// sasaran aplikasi ini - 100 klip @5 menit - jendela 2% berarti 600
        /// detik, sehingga kehilangan DUA klip utuh masih akan dilaporkan
        /// sukses.
        ///
        /// Sebagai gantinya, kelonggaran dihitung dari pergeseran per sambungan
        /// yang terukur (sekitar 20 ms per batas saat menyalin, beberapa ratus
        /// saat encode ulang lewat rantai scale/pad/fps) lalu dibatasi setengah
        /// durasi klip terpendek, yang menjamin kehilangan satu klip mana pun
        /// selalu tertangkap.
        /// </summary>
        public static double DurationTolerance(IList<VideoFile> files, MergeMode mode)
        {
            int joins = Math.Max(0, files.Count - 1);
            double perJoin = mode == MergeMode.Copy ? 0.05 : 0.30;
            double allowance = Math.Max(1.0, perJoin * joins);

            double shortest = 0.0;
            foreach (var f in files)
                if (f.Duration > 0 && (shortest == 0.0 || f.Duration < shortest))
                    shortest = f.Duration;
            if (shortest > 0)
                allowance = Math.Min(allowance, Math.Max(1.0, shortest * 0.5));
            return allowance;
        }

        /// <summary>
        /// Menolak menyebut hasil yang terpotong sebagai sukses.
        ///
        /// Kode keluar ffmpeg tidak bisa dipercaya di sini - nilainya 0 bahkan
        /// ketika ia menyerah di tengah daftar - jadi berkas jadinya diukur
        /// terhadap durasi yang sudah diketahui dari masukannya.
        /// </summary>
        public void VerifyOutput(string outPath, double expected,
                                 IList<VideoFile> files, MergeMode mode)
        {
            if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                throw new MergeException("File hasil tidak terbentuk.");
            if (expected <= 0) return;

            VideoFile probe;
            try
            {
                probe = Prober.ProbeFile(Tools, new VideoFile
                {
                    Path = outPath,
                    Size = new FileInfo(outPath).Length,
                });
            }
            catch (Exception)
            {
                return;   // tidak terukur; jangan menghalangi berkas yang mungkin baik
            }

            double actual = probe.Duration;
            if (actual <= 0)
                throw new MergeException(
                    "File hasil terbentuk tapi durasinya tidak terbaca - "
                    + "kemungkinan besar rusak.");

            double tolerance = DurationTolerance(files, mode);

            if (expected - actual > tolerance)
                throw new MergeException(
                    "Hasil gabungan TERPOTONG dan tidak bisa dipakai." + NL2
                    + "Durasi seharusnya : " + Humanize.Duration(expected) + NL
                    + "Durasi yang jadi  : " + Humanize.Duration(actual) + NL2
                    + "Biasanya ini terjadi karena salah satu video tidak bisa "
                    + "dibaca di tengah proses (drive terlepas, file terkunci, "
                    + "atau file rusak). Coba muat ulang daftar lalu ulangi.");

            // Durasi container bisa terlihat sehat sementara trek videonya
            // sendiri hancur - ketidakcocokan timestamp merentangkan audio ke
            // panjang yang diharapkan dan meninggalkan gambarnya pendek. Jadi
            // stream video diukur terpisah setiap kali ia melaporkan durasi.
            if (probe.VideoDuration > 0 && expected - probe.VideoDuration > tolerance)
                throw new MergeException(
                    "Hasil gabungan rusak: gambar dan suara tidak sama panjang." + NL2
                    + "Durasi seharusnya : " + Humanize.Duration(expected) + NL
                    + "Durasi gambar     : " + Humanize.Duration(probe.VideoDuration) + NL
                    + "Durasi keseluruhan: " + Humanize.Duration(actual) + NL2
                    + "File tidak bisa dipakai. Coba pilih metode "
                    + "\"Encode ulang semua\".");

            if (actual - expected > tolerance)
                Log("Catatan: durasi hasil (" + Humanize.Duration(actual)
                    + ") sedikit lebih panjang dari perkiraan ("
                    + Humanize.Duration(expected) + ").");
        }

        // ------------------------------------------------------ perencanaan --
        private MergeMode DecideMode(List<VideoFile> files)
        {
            MergeMode mode = Job.Mode;
            List<string> reasons;
            bool ok = Prober.CanStreamCopy(files, out reasons);

            if (mode == MergeMode.Auto)
            {
                if (ok)
                {
                    Log("Semua video punya parameter identik -> mode CEPAT "
                        + "(tanpa encode ulang).");
                    return MergeMode.Copy;
                }
                Log("Parameter video berbeda-beda, perlu encode ulang:");
                for (int i = 0; i < Math.Min(5, reasons.Count); i++)
                    Log("  - " + reasons[i]);
                return MergeMode.Smart;
            }
            if (mode == MergeMode.Copy && !ok)
            {
                var sb = new StringBuilder();
                sb.Append("Mode cepat tidak bisa dipakai karena parameter video berbeda:")
                  .Append(NL2);
                for (int i = 0; i < Math.Min(6, reasons.Count); i++)
                    sb.Append("- ").Append(reasons[i]).Append(NL);
                sb.Append(NL).Append("Pakai mode Otomatis atau Encode ulang.");
                throw new MergeException(sb.ToString());
            }
            return mode;
        }

        private void CheckOutputNotInput(List<VideoFile> files, string outPath)
        {
            string target = Path.GetFullPath(outPath);
            foreach (var f in files)
            {
                if (string.Equals(Path.GetFullPath(f.Path), target,
                                  StringComparison.OrdinalIgnoreCase))
                    throw new MergeException(
                        "File keluaran sama dengan salah satu video sumber ("
                        + f.Name + "). Pilih nama lain.");
            }
        }

        /// <summary>
        /// Resolusi dan frame rate mayoritas di antara klip. Dipakai untuk
        /// memperkirakan ukuran hasil dan lama proses tanpa harus membangun
        /// target penuh. Mengembalikan false kalau tidak ada satu klip pun
        /// yang melaporkan dimensi (nilai fallback 1920x1080@30 tetap terisi).
        /// </summary>
        public static bool EstimateGeometry(IList<VideoFile> files,
                                            out int width, out int height, out double fps)
        {
            width = 1920; height = 1080; fps = 30.0;
            if (files == null || files.Count == 0) return false;

            var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
            var rates = new Dictionary<double, int>();
            foreach (var f in files)
            {
                if (f.Width > 0 && f.Height > 0)
                {
                    string key = f.Width + "x" + f.Height;
                    int n;
                    sizes[key] = sizes.TryGetValue(key, out n) ? n + 1 : 1;
                }
                if (f.Fps > 0)
                {
                    int n;
                    rates[f.Fps] = rates.TryGetValue(f.Fps, out n) ? n + 1 : 1;
                }
            }

            bool have = false;
            if (sizes.Count > 0)
            {
                string best = null; int bestCount = -1;
                foreach (var pair in sizes)
                    if (pair.Value > bestCount) { best = pair.Key; bestCount = pair.Value; }
                string[] bits = best.Split('x');
                width = int.Parse(bits[0], CultureInfo.InvariantCulture);
                height = int.Parse(bits[1], CultureInfo.InvariantCulture);
                have = true;
            }
            if (rates.Count > 0)
            {
                double best = fps; int bestCount = -1;
                foreach (var pair in rates)
                    if (pair.Value > bestCount) { best = pair.Key; bestCount = pair.Value; }
                fps = best;
            }
            return have;
        }

        /// <summary>
        /// Perkiraan kasar ukuran berkas jadi. Statik supaya tampilan bisa
        /// menyebut angkanya sebelum proses, memakai rumus yang sama persis
        /// dengan pemeriksaan disk di bawah.
        ///
        /// Salin-langsung mudah: keluarannya adalah masukannya, dikurangi
        /// sedikit karena 100 header container terpisah menyatu jadi satu.
        ///
        /// Encode ulang diperkirakan dari jumlah piksel, bukan dari MB per
        /// detik yang dipatok. Angka tetap meleset jauh ke dua arah - 2,2 MB/s
        /// akan menuntut 126 GB untuk pekerjaan 720p 8 jam yang sebenarnya
        /// menghasilkan sekitar 8 GB, dan menolak mulai di disk mana pun.
        /// </summary>
        public static long EstimateOutputBytes(IList<VideoFile> files, MergeMode mode)
        {
            long totalIn = 0;
            double duration = 0;
            if (files != null)
                foreach (var f in files) { totalIn += f.Size; duration += f.Duration; }

            if (mode == MergeMode.Copy) return (long)(totalIn * 1.02);

            int w, h; double fps;
            EstimateGeometry(files, out w, out h, out fps);
            double pixelsPerSecond = Math.Max(1.0, (double)w * h) * Math.Max(1.0, fps);
            // ~0,08 bit per piksel adalah angka CRF 23 yang murah hati;
            // dibatasi supaya spesifikasi yang aneh tidak menuntut yang tidak
            // masuk akal.
            double bitsPerSecond = Math.Min(25000000.0,
                                            Math.Max(800000.0, pixelsPerSecond * 0.08));
            return (long)(duration * bitsPerSecond / 8.0 * 1.25);
        }

        /// <summary>
        /// Puncak ruang disk yang dibutuhkan. Encode ulang menyimpan klip
        /// ternormalisasi berdampingan dengan berkas akhir sampai penyambungan
        /// selesai, jadi puncaknya kira-kira dua kali ukuran hasil.
        /// </summary>
        public static long PeakDiskNeed(IList<VideoFile> files, MergeMode mode)
        {
            long need = EstimateOutputBytes(files, mode);
            if (mode != MergeMode.Copy) need *= 2;
            return need;
        }

        private void CheckDisk(List<VideoFile> files, string outPath, MergeMode mode)
        {
            long need = PeakDiskNeed(files, mode);

            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            long free = Paths.DiskFree(string.IsNullOrEmpty(dir) ? "." : dir);
            if (free > 0 && need > free)
                throw new MergeException(
                    "Ruang disk kemungkinan tidak cukup." + NL
                    + "Perkiraan dibutuhkan: "
                    + (need / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture)
                    + " GB" + NL + "Tersedia: "
                    + (free / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture)
                    + " GB");
        }

        // ------------------------------------------------------ jalur cepat --
        private void RunCopy(List<VideoFile> files, string outPath)
        {
            _tempDir = MakeTemp(outPath);
            var paths = new List<string>();
            double total = 0;
            foreach (var f in files) { paths.Add(f.Path); total += f.Duration; }
            string listPath = WriteConcatList(paths,
                Path.Combine(_tempDir, "concat.txt"));

            Emit(new Progress
            {
                Stage = Stage.Merging,
                Message = "Menggabungkan (mode cepat)...",
                SecondsTotal = total,
                TotalItems = files.Count,
            });

            var cmd = new List<string>
            {
                Tools.FFmpeg, "-hide_banner", "-y",
                "-f", "concat", "-safe", "0", "-i", listPath,
                "-c", "copy",
                "-map", "0",
                // Dipertahankan karena penyambungan 8 jam bisa mendorong
                // antrean muxing melewati jendela 10 detik bawaannya lalu
                // berhenti. Pendampingnya yang biasa (-fflags +genpts,
                // -avoid_negative_ts) sengaja TIDAK ada di sini: diukur
                // terhadap ffmpeg 8.1.1, keduanya tidak mengubah apa pun untuk
                // concat demuxer, termasuk pada H.264 mentah dan pada masukan
                // yang start time-nya bukan nol.
                "-max_interleave_delta", "0",
            };
            cmd.AddRange(ContainerFlags(outPath));
            cmd.AddRange(new[] { "-progress", "pipe:1", "-nostats", outPath });

            RunFFmpeg(cmd, total, 0.0, 1.0, Stage.Merging, "Menggabungkan");
        }

        // -------------------------------------------------- jalur encode ulang --
        /// <summary>
        /// Pastikan encoder yang diminta benar-benar jalan, kalau tidak mundur ke CPU.
        ///
        /// <c>ffmpeg -encoders</c> mendaftar apa yang ikut dikompilasi, bukan
        /// apa yang bisa dijalankan mesin ini: h264_amf terdaftar di komputer
        /// tanpa driver AMD dan mati pada frame pertama. Meng-encode satu frame
        /// buangan menyelesaikannya dalam waktu jauh di bawah satu detik, yang
        /// jauh lebih baik daripada menemukannya empat jam setelah pekerjaan
        /// delapan jam dimulai. Dinilai HANYA dari kode keluar - sebagian
        /// encoder (SVT-AV1) mencetak banner ke stderr walau berhasil.
        /// </summary>
        private string ResolveEncoder()
        {
            string encoder = Job.HwaccelEncoder;
            if (string.IsNullOrEmpty(encoder)) return "";

            var cmd = new[]
            {
                Tools.FFmpeg, "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=1",
                "-c:v", encoder, "-frames:v", "1", "-f", "null", "-",
            };
            try
            {
                var res = Shell.RunCapture(cmd, 45);
                if (!res.TimedOut && res.ExitCode == 0) return encoder;
            }
            catch (Exception) { }

            Log("Encoder " + encoder + " tidak bisa dipakai di komputer ini "
                + "(driver/GPU tidak mendukung). Beralih ke CPU.");
            return "";
        }

        private void RunReencode(List<VideoFile> files, string outPath, bool smart)
        {
            Job.HwaccelEncoder = ResolveEncoder();
            _tempDir = MakeTemp(outPath);
            TargetSpec target = Job.Target;
            string majority = smart ? MajoritySignature(files) : null;

            var passThrough = new List<VideoFile>();
            var toEncode = new List<VideoFile>();
            foreach (var f in files)
            {
                if (smart && majority != null && f.CopySignature() == majority)
                    passThrough.Add(f);
                else
                    toEncode.Add(f);
            }

            // Klip hasil encode ulang harus mendarat di container yang sama
            // dengan timescale yang sama seperti klip yang akan disambung
            // dengannya. Menulisnya ke .mkv (timebase 1/1000) lalu
            // menyambungnya ke sumber .mp4 (1/15360) persis ketidakcocokan
            // yang diam-diam merentangkan keluaran.
            string tempExt = ".mkv";
            int timescale = 0;

            if (smart && toEncode.Count > 0)
            {
                VideoFile sample = null;
                foreach (var f in files)
                    if (f.CopySignature() == majority) { sample = f; break; }
                string sourceExt = sample != null
                    ? (Path.GetExtension(sample.Path) ?? "").ToLowerInvariant() : "";

                if (sample == null || !MuxableExtensions.Contains(sourceExt))
                {
                    // Klip yang tidak disentuh berada di container yang bisa
                    // dibaca ffmpeg tetapi tidak bisa ditulisnya (.dav dari DVR
                    // Dahua, .264, .divx...), jadi tidak ada cara menghasilkan
                    // segmen yang bisa disalin-sambung dengannya. Meng-encode
                    // ulang semuanya lebih lambat tetapi benar-benar bekerja.
                    Log("Wadah " + (sourceExt.Length > 0 ? sourceExt : "(tidak dikenal)")
                        + " tidak bisa ditulis ulang; semua video di-encode ulang.");
                    smart = false;
                }
                else
                {
                    target = TargetFromSignature(files, majority) ?? target;
                    tempExt = sourceExt;
                    timescale = TimescaleOf(sample.VTimeBase);
                    Log("Mode hemat: " + passThrough.Count
                        + " video dipakai apa adanya, " + toEncode.Count
                        + " video di-encode ulang ke " + target.Describe() + ".");
                }
            }

            if (!smart)
            {
                toEncode = new List<VideoFile>(files);
                passThrough.Clear();
                target = AutoTarget(files);
                // Semuanya di-encode ulang dengan setelan identik, jadi
                // container tunggal apa pun bisa; MKV menghindari penulisan
                // ulang indeks MP4 pada setiap klip.
                tempExt = ".mkv";
                timescale = 0;
                Log("Encode ulang semua video ke " + target.Describe() + ".");
            }

            double totalEncode = 0;
            foreach (var f in toEncode) totalEncode += f.Duration;
            if (totalEncode <= 0) totalEncode = 1.0;

            // Encode ulang mendominasi waktu jalan; sisakan irisan terakhir
            // untuk penyambungannya.
            double encodeSpan = toEncode.Count > 0 ? 0.94 : 0.0;
            double doneSeconds = 0.0;
            var normalised = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int index = 1; index <= toEncode.Count; index++)
            {
                CheckCancel();
                var f = toEncode[index - 1];
                string tempOut = Path.Combine(_tempDir,
                    "norm_" + index.ToString("0000", CultureInfo.InvariantCulture) + tempExt);
                var cmd = NormalizeCmd(f, tempOut, target, timescale);
                double baseFraction = (doneSeconds / totalEncode) * encodeSpan;
                double span = (f.Duration / totalEncode) * encodeSpan;

                Emit(new Progress
                {
                    Stage = Stage.Normalizing,
                    Fraction = baseFraction,
                    CurrentIndex = index,
                    TotalItems = toEncode.Count,
                    Message = "Encode ulang " + index + "/" + toEncode.Count + ": " + f.Name,
                });
                RunFFmpeg(cmd, f.Duration, baseFraction, span, Stage.Normalizing,
                          "Encode " + index + "/" + toEncode.Count,
                          index, toEncode.Count);

                if (!File.Exists(tempOut) || new FileInfo(tempOut).Length == 0)
                    throw new MergeException("Gagal meng-encode ulang: " + f.Name);
                normalised[f.Path] = tempOut;
                doneSeconds += f.Duration;
            }

            double joinBase = encodeSpan;
            if (smart && toEncode.Count > 0)
                joinBase = VerifySmart(files, normalised, target, encodeSpan,
                                       tempExt, timescale);

            var ordered = new List<string>();
            double total = 0;
            foreach (var f in files)
            {
                string mapped;
                ordered.Add(normalised.TryGetValue(f.Path, out mapped) ? mapped : f.Path);
                total += f.Duration;
            }
            string listPath = WriteConcatList(ordered,
                Path.Combine(_tempDir, "concat.txt"));

            Emit(new Progress
            {
                Stage = Stage.Merging,
                Fraction = joinBase,
                Message = "Menyatukan hasil...",
                SecondsTotal = total,
            });

            var joinCmd = new List<string>
            {
                Tools.FFmpeg, "-hide_banner", "-y",
                "-f", "concat", "-safe", "0", "-i", listPath,
                "-c", "copy", "-map", "0",
                "-max_interleave_delta", "0",
            };
            joinCmd.AddRange(ContainerFlags(outPath));
            joinCmd.AddRange(new[] { "-progress", "pipe:1", "-nostats", outPath });
            RunFFmpeg(joinCmd, total, joinBase, 1.0 - joinBase, Stage.Merging,
                      "Menyatukan");
        }

        /// <summary>
        /// Pastikan klip hasil encode benar-benar sejajar dengan klip yang lolos.
        ///
        /// Kalau libx264 menghasilkan parameter yang ternyata masih berbeda,
        /// menyambungnya dengan salin akan menghasilkan berkas yang hanya
        /// segmen pertamanya diputar benar - jadi sisanya ikut di-encode ulang
        /// daripada mengirimkan video rusak.
        /// </summary>
        private double VerifySmart(List<VideoFile> files,
                                   Dictionary<string, string> normalised,
                                   TargetSpec target, double encodeSpan,
                                   string tempExt, int timescale)
        {
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in files)
            {
                string path;
                if (normalised.TryGetValue(f.Path, out path))
                {
                    var probe = Prober.ProbeFile(Tools, new VideoFile
                    {
                        Path = path,
                        Size = new FileInfo(path).Length,
                    });
                    signatures.Add(probe.CopySignature());
                }
                else
                {
                    signatures.Add(f.CopySignature());
                }
                CheckCancel();
            }

            if (signatures.Count <= 1) return encodeSpan;

            Log("Parameter hasil encode masih berbeda dari video asli; "
                + "meng-encode ulang sisanya agar hasil tidak rusak.");

            var remaining = new List<VideoFile>();
            double totalRemaining = 0;
            foreach (var f in files)
                if (!normalised.ContainsKey(f.Path))
                { remaining.Add(f); totalRemaining += f.Duration; }
            if (totalRemaining <= 0) totalRemaining = 1.0;

            // Lintasan ini tidak ada dalam rencana, jadi tidak punya jatah
            // sendiri. Biarkan ia merayap di irisan yang disisakan untuk
            // penyambungan daripada memaku bilahnya di 94% selama waktu yang
            // bisa jadi panjang.
            double fixupSpan = Math.Max(0.0, 1.0 - encodeSpan) * 0.6;
            double done = 0.0;

            for (int index = 1; index <= remaining.Count; index++)
            {
                CheckCancel();
                var f = remaining[index - 1];
                string tempOut = Path.Combine(_tempDir,
                    "fix_" + index.ToString("0000", CultureInfo.InvariantCulture) + tempExt);
                RunFFmpeg(NormalizeCmd(f, tempOut, target, timescale), f.Duration,
                          encodeSpan + (done / totalRemaining) * fixupSpan,
                          (f.Duration / totalRemaining) * fixupSpan,
                          Stage.Normalizing,
                          "Menyamakan " + index + "/" + remaining.Count,
                          index, remaining.Count);
                normalised[f.Path] = tempOut;
                done += f.Duration;
            }
            return encodeSpan + fixupSpan;
        }

        // ------------------------------------------------- penyusun perintah --
        /// <summary>Encode ulang satu klip tepat ke `target`, dengan letterbox bila perlu.</summary>
        private List<string> NormalizeCmd(VideoFile f, string outPath,
                                          TargetSpec target, int timescale)
        {
            var inv = CultureInfo.InvariantCulture;
            // scale menjaga rasio aspek, pad memusatkannya pada kanvas tetap,
            // dan setsar memaku rasio piksel supaya concat melihat geometri
            // yang identik.
            string vf =
                "scale=" + target.Width + ":" + target.Height
                + ":force_original_aspect_ratio=decrease:flags=bicubic,"
                + "pad=" + target.Width + ":" + target.Height
                + ":(ow-iw)/2:(oh-ih)/2:color=black,"
                + "setsar=1,fps=" + target.Fps.ToString("0.####", inv)
                + ",format=" + target.PixFmt;

            var cmd = new List<string>
            {
                Tools.FFmpeg, "-hide_banner", "-y", "-i", f.Path,
            };

            string layout = target.Channels == 2 ? "stereo" : "mono";
            if (!f.HasAudio)
            {
                // Klip tanpa audio akan membuat semua yang sesudahnya lepas
                // sinkron, jadi diberi keheningan dengan parameter audio
                // persis seperti targetnya.
                cmd.AddRange(new[]
                {
                    "-f", "lavfi", "-i",
                    "anullsrc=channel_layout=" + layout
                    + ":sample_rate=" + target.SampleRate.ToString(inv),
                });
                cmd.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0", "-shortest" });
            }
            else
            {
                cmd.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0" });
            }

            cmd.AddRange(new[] { "-vf", vf });
            cmd.AddRange(VideoEncoderFlags(target));

            // async=1 merenggangkan/memampatkan untuk mengisi celah alih-alih
            // membiarkan klip dengan trek audio compang-camping melenceng dari
            // videonya sendiri, dan first_pts=0 memaku treknya ke nol supaya
            // segmen menyatu rapi. Keduanya penting untuk audio CCTV, yang
            // sering berlubang.
            cmd.AddRange(new[]
            {
                "-af", "aresample=" + target.SampleRate.ToString(inv)
                       + ":async=1:first_pts=0,aformat=sample_fmts=fltp:sample_rates="
                       + target.SampleRate.ToString(inv)
                       + ":channel_layouts=" + layout,
                "-c:a", target.AEncoder, "-b:a", target.ABitrate,
                "-ar", target.SampleRate.ToString(inv),
                "-ac", target.Channels.ToString(inv),
            });

            string ext = (Path.GetExtension(outPath) ?? "").ToLowerInvariant();
            if (ext == ".mp4" || ext == ".m4v" || ext == ".mov")
            {
                // Menyamakan timescale tujuan inilah yang membuat segmen ini
                // bisa disalin-sambung dengan klip di sekitarnya yang tak
                // tersentuh.
                if (timescale > 0)
                    cmd.AddRange(new[] { "-video_track_timescale", timescale.ToString(inv) });

                // Sengaja TIDAK ada -avoid_negative_ts/-muxdelay 0 di sini.
                // Keduanya tampak seperti perbaikan bila dilihat sendiri -
                // proses yang setiap segmennya ditulis begini tidak untung dan
                // tidak rugi - tetapi jalur kode ini HANYA berjalan pada mode
                // yang menyambungkan segmen ternormalisasi ke klip ASLI, dan
                // klip asli membawa offset priming AAC yang biasa. Terukur
                // pada campuran itu: dengan bendera tersebut 2 peringatan DTS
                // non-monotonik dan 6,063 detik dari 6,0; tanpa keduanya 0
                // peringatan dan 6,041 detik. Segmen harus mengikuti konvensi
                // timestamp yang sama dengan berkas yang diapitnya.
            }

            cmd.AddRange(new[]
            {
                "-map_metadata", "-1", "-map_chapters", "-1",
                "-progress", "pipe:1", "-nostats", outPath,
            });
            return cmd;
        }

        private List<string> VideoEncoderFlags(TargetSpec target)
        {
            string encoder = !string.IsNullOrEmpty(Job.HwaccelEncoder)
                ? Job.HwaccelEncoder : target.VEncoder;
            return EncoderFlagsFor(encoder, target);
        }

        /// <summary>
        /// Bendera encoder untuk satu encoder dan satu target.
        ///
        /// Publik dan statis supaya benchmark encoder bisa memakai bendera yang
        /// SAMA PERSIS dengan yang dipakai saat bekerja. Mengukur kecepatan
        /// dengan setelan lain memberi angka yang benar untuk pertanyaan yang
        /// salah: preset dan mode rate control sangat menentukan hasilnya.
        /// </summary>
        public static List<string> EncoderFlagsFor(string encoder, TargetSpec target)
        {
            var inv = CultureInfo.InvariantCulture;
            target = target ?? new TargetSpec();
            if (string.IsNullOrEmpty(encoder)) encoder = target.VEncoder;

            if (encoder == "h264_nvenc" || encoder == "hevc_nvenc")
            {
                // -cq milik NVENC BUKAN -crf milik x264. libx264 -crf 23
                // terukur 1743 kbps sedangkan h264_nvenc -cq 23 memberi
                // 7070 kbps - berkas 4x lebih besar untuk angka nominal yang
                // sama. Offset yang tepat bergantung isi (+10 cocok pada satu
                // klip, +5 pada klip lain) dan menyamakan bitrate pun
                // meremehkan kualitas, karena NVENC kurang efisien per bit.
                // +5 memilih berpihak pada kualitas.
                return new List<string>
                {
                    "-c:v", encoder, "-preset", "p5", "-rc", "vbr",
                    "-cq", Math.Min(51, target.Crf + 5).ToString(inv),
                    "-pix_fmt", target.PixFmt,
                };
            }
            if (encoder == "h264_qsv" || encoder == "hevc_qsv")
                return new List<string>
                {
                    "-c:v", encoder, "-global_quality", target.Crf.ToString(inv),
                    "-pix_fmt", target.PixFmt,
                };
            if (encoder == "h264_amf" || encoder == "hevc_amf")
                return new List<string>
                {
                    "-c:v", encoder, "-quality", "balanced", "-rc", "cqp",
                    "-qp_i", target.Crf.ToString(inv),
                    "-qp_p", target.Crf.ToString(inv),
                    "-pix_fmt", target.PixFmt,
                };

            var flags = new List<string>
            {
                "-c:v", encoder, "-preset", target.Preset,
                "-crf", target.Crf.ToString(inv), "-pix_fmt", target.PixFmt,
            };
            if (!string.IsNullOrEmpty(target.VProfile)
                && (encoder == "libx264" || encoder == "libx265"))
            {
                // Tanpa ini libx264 selalu menulis High. Sumber CCTV umumnya
                // Main atau Baseline, sehingga klip hasil encode tidak pernah
                // cocok dengan yang tak tersentuh dan mode "perbaiki yang beda
                // saja" diam-diam mundur jadi meng-encode ulang semuanya.
                flags.Add("-profile:v");
                flags.Add(target.VProfile);
            }
            // GOP tetap membuat setiap klip ternormalisasi identik strukturnya.
            flags.Add("-g");
            flags.Add(Math.Max(1, (int)Math.Round(target.Fps * 2)).ToString(inv));
            return flags;
        }

        private List<string> ContainerFlags(string outPath)
        {
            string ext = (Path.GetExtension(outPath) ?? "").ToLowerInvariant();
            if (ext == ".mp4" || ext == ".m4v" || ext == ".mov")
            {
                var flags = new List<string>();
                if (Job.Faststart) { flags.Add("-movflags"); flags.Add("+faststart"); }
                // Stream metadata berwaktu / data tidak bisa hidup di MP4 dan
                // akan membatalkan muxing; membuangnya tidak berbahaya untuk
                // penggabungan video.
                flags.AddRange(new[] { "-dn", "-map", "-0:d?", "-map", "-0:t?" });
                return flags;
            }
            return new List<string>();
        }

        // ------------------------------------------------- pemilihan target --
        /// <summary>
        /// Sidik tanda tangan salin yang paling banyak dipakai. Publik supaya
        /// tampilan bisa menandai klip minoritas - yang justru akan di-encode
        /// ulang pada mode Hemat.
        /// </summary>
        public static string MajoritySignature(List<VideoFile> files)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in files)
            {
                string key = f.CopySignature();
                int n;
                counts[key] = counts.TryGetValue(key, out n) ? n + 1 : 1;
            }
            string best = null;
            int bestCount = -1;
            foreach (var pair in counts)
                if (pair.Value > bestCount) { best = pair.Key; bestCount = pair.Value; }
            return best;
        }

        /// <summary>Bangun TargetSpec yang mereproduksi kelompok mayoritas persis.</summary>
        private static TargetSpec TargetFromSignature(List<VideoFile> files,
                                                      string signature)
        {
            VideoFile sample = null;
            foreach (var f in files)
                if (f.CopySignature() == signature) { sample = f; break; }
            if (sample == null) return null;

            string encoder;
            if (!VideoEncoders.TryGetValue(sample.VCodec ?? "", out encoder))
                encoder = "libx264";

            string profile = "";
            if (encoder == "libx264" || encoder == "libx265")
                X264Profiles.TryGetValue((sample.VProfile ?? "").Trim(), out profile);

            string audio;
            if (!AudioEncoders.TryGetValue(sample.ACodec ?? "", out audio))
                audio = "aac";

            return new TargetSpec
            {
                // yuv420p menuntut dimensi genap; sumber selebar 1919 piksel
                // akan membuat libx264 menolak mulai.
                Width = sample.Width + (sample.Width % 2),
                Height = sample.Height + (sample.Height % 2),
                Fps = sample.Fps > 0 ? sample.Fps : 30.0,
                PixFmt = string.IsNullOrEmpty(sample.PixFmt) ? "yuv420p" : sample.PixFmt,
                VEncoder = encoder,
                VProfile = profile ?? "",
                AEncoder = audio,
                SampleRate = sample.SampleRate > 0 ? sample.SampleRate : 48000,
                Channels = sample.Channels > 0 ? sample.Channels : 2,
            };
        }

        private TargetSpec AutoTarget(List<VideoFile> files)
        {
            TargetSpec spec = Job.Target.Clone();

            var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
            var rates = new Dictionary<double, int>();
            foreach (var f in files)
            {
                if (f.Width > 0 && f.Height > 0)
                {
                    string key = f.Width + "x" + f.Height;
                    int n;
                    sizes[key] = sizes.TryGetValue(key, out n) ? n + 1 : 1;
                }
                if (f.Fps > 0)
                {
                    int n;
                    rates[f.Fps] = rates.TryGetValue(f.Fps, out n) ? n + 1 : 1;
                }
            }

            if (sizes.Count > 0)
            {
                string best = null; int bestCount = -1;
                foreach (var pair in sizes)
                    if (pair.Value > bestCount) { best = pair.Key; bestCount = pair.Value; }
                string[] bits = best.Split('x');
                spec.Width = int.Parse(bits[0], CultureInfo.InvariantCulture);
                spec.Height = int.Parse(bits[1], CultureInfo.InvariantCulture);
            }
            if (rates.Count > 0)
            {
                double best = spec.Fps; int bestCount = -1;
                foreach (var pair in rates)
                    if (pair.Value > bestCount) { best = pair.Key; bestCount = pair.Value; }
                spec.Fps = best;
            }
            if (spec.Width % 2 != 0) spec.Width++;
            if (spec.Height % 2 != 0) spec.Height++;
            return spec;
        }

        // ------------------------------------------------ folder sementara --
        /// <summary>
        /// Folder sementara di volume yang sama dengan keluaran, supaya
        /// pemindahannya murah. Menyertakan id objek selain pid supaya dua
        /// penggabungan dari satu proses (atau dua jendela aplikasi) tidak
        /// berbagi - lalu saling menghapus - berkas kerjanya.
        /// </summary>
        private string MakeTemp(string outPath)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (string.IsNullOrEmpty(parent)) parent = ".";
            SweepStaleTemp(parent);
            string path = Path.Combine(parent, ".vmerge_tmp_"
                + Process.GetCurrentProcess().Id + "_"
                + GetHashCode().ToString("x"));
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                throw new MergeException(
                    "Tidak bisa menulis di folder tujuan:" + NL2 + parent + NL2
                    + ex.Message + NL2
                    + "Pilih folder lain (mis. Documents atau drive D:).");
            }
            return path;
        }

        /// <summary>
        /// Buang folder kerja milik proses yang sudah tidak hidup.
        ///
        /// Jalur normal - selesai, gagal, dibatalkan, jendela ditutup - selalu
        /// membersihkan miliknya sendiri. Yang tidak bisa ditangani dari dalam
        /// adalah proses yang mati mendadak: dimatikan lewat Task Manager,
        /// listrik padam, Windows restart paksa. Sisanya bisa puluhan GB klip
        /// ternormalisasi yang diam di folder tujuan selamanya, dan pengguna
        /// tidak punya cara tahu itu apa.
        ///
        /// Nama foldernya memuat PID pembuatnya, jadi "masih dipakai atau
        /// tidak" bisa dijawab: PID yang tidak lagi ada berarti aman dihapus.
        /// PID yang masih hidup dilewati - bisa jadi itu jendela kedua yang
        /// sedang bekerja di folder yang sama.
        /// </summary>
        public static void SweepStaleTemp(string folder)
        {
            string[] entries;
            try { entries = Directory.GetDirectories(folder, ".vmerge_tmp_*"); }
            catch (Exception) { return; }

            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                string[] bits = name.Split('_');
                int pid;
                // ".vmerge", "tmp", "<pid>", "<id>"
                if (bits.Length < 4
                    || !int.TryParse(bits[2], NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out pid))
                    continue;
                if (IsAlive(pid)) continue;
                try { Directory.Delete(entry, true); } catch (Exception) { }
            }
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using (Process.GetProcessById(pid)) return true;
            }
            catch (ArgumentException)
            {
                return false;      // tidak ada proses dengan PID itu
            }
            catch (Exception)
            {
                // Tidak boleh diakses karena hak akses: anggap masih hidup
                // daripada menghapus folder yang mungkin sedang dipakai.
                return true;
            }
        }

        private void CleanupTemp()
        {
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch (Exception) { }
            }
            _tempDir = "";
        }

        /// <summary>Keluaran yang setengah tertulis tidak bisa diputar; jangan ditinggalkan.</summary>
        private static void RemovePartial(string outPath)
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); }
            catch (Exception) { }
        }
    }
}
