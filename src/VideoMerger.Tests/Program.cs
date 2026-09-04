using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using VideoMerger.Core;
using static VideoMerger.Tests.Harness;

namespace VideoMerger.Tests
{
    public static class Program
    {
        private static string _work;

        public static int Main(string[] args)
        {
            Tools = FFmpegLocator.Locate();
            if (Tools == null)
            {
                Console.WriteLine("FFmpeg tidak ditemukan - tes dilewati.");
                return 2;
            }
            Console.WriteLine("FFmpeg " + Tools.Version);

            _work = Path.Combine(Path.GetTempPath(),
                "vmerge_test_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(_work);

            var sections = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("kompatibilitas", TestCompatibility),
                new KeyValuePair<string, Action>("escaping", TestEscaping),
                new KeyValuePair<string, Action>("pengurutan", TestSorting),
                new KeyValuePair<string, Action>("hasil terpotong", TestTruncationGuard),
                new KeyValuePair<string, Action>("toleransi", TestTolerance),
                new KeyValuePair<string, Action>("jebakan windows", TestWindowsTraps),
                new KeyValuePair<string, Action>("progres", TestProgress),
                new KeyValuePair<string, Action>("mode hemat", TestSmartSelection),
                new KeyValuePair<string, Action>("keluaran bersih", TestCleanOutput),
                new KeyValuePair<string, Action>("hardsub", TestHardsub),
                new KeyValuePair<string, Action>("pengaturan", TestSettings),
                new KeyValuePair<string, Action>("encoder", TestEncoderSelection),
                new KeyValuePair<string, Action>("pembaruan", TestUpdateCheck),
                new KeyValuePair<string, Action>("perkiraan", TestEstimates),
                new KeyValuePair<string, Action>("riwayat folder", TestRecentFolders),
            };

            try
            {
                // Tiap bagian diisolasi: satu kesalahan dulu membatalkan
                // seluruh proses setelah hanya mencetak yang lulus, yang
                // terbaca persis seperti proses yang bersih kecuali kalau
                // kode keluarnya diperiksa.
                foreach (var section in sections)
                {
                    try
                    {
                        section.Value();
                    }
                    catch (Exception ex)
                    {
                        Failures.Add("bagian '" + section.Key + "' menabrak error: "
                                     + ex.Message);
                        Console.WriteLine("  ERROR bagian '" + section.Key + "': " + ex);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(_work, true); } catch (Exception) { }
            }

            Console.WriteLine();
            Console.WriteLine(new string('=', 58));
            if (Failures.Count > 0)
            {
                Console.WriteLine(Passes + " lulus, " + Failures.Count + " GAGAL:");
                foreach (string f in Failures) Console.WriteLine("  - " + f);
                return 1;
            }
            Console.WriteLine("SEMUA " + Passes + " TES LULUS");
            return 0;
        }

        private static string[] BaseSource(string size = "640x480", string rate = "30",
                                           string duration = "5")
        {
            return new[]
            {
                "-f", "lavfi", "-i", "testsrc=size=" + size + ":rate=" + rate
                    + ":duration=" + duration,
                "-f", "lavfi", "-i", "sine=frequency=440:duration=" + duration,
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-ar", "48000", "-ac", "2", "-shortest",
            };
        }

        // ================================================== [1] kompatibilitas
        private static void TestCompatibility()
        {
            Console.WriteLine("\n[1] Deteksi kompatibilitas stream copy");
            string baseFile = Make(Path.Combine(_work, "base.mp4"), BaseSource());
            var a = Probed(baseFile);
            Check("berkas dasar terbaca", a.Valid, a.Error);

            string twin = Make(Path.Combine(_work, "twin.mp4"), BaseSource());
            var b = Probed(twin);
            List<string> reasons;
            Check("dua klip identik boleh disalin",
                  Prober.CanStreamCopy(new[] { a, b }, out reasons),
                  string.Join("; ", reasons));

            // -- time base: kerusakan diam-diam yang paling mahal -------------
            string slow = Make(Path.Combine(_work, "tb.mp4"),
                "-f", "lavfi", "-i", "testsrc=size=640x480:rate=30:duration=5",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=5",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-ar", "48000", "-ac", "2", "-shortest",
                "-video_track_timescale", "30000");
            var c = Probed(slow);

            if (a.VTimeBase != c.VTimeBase)
            {
                // Buktikan dulu kerusakannya nyata, baru periksa aplikasi menolaknya.
                double duration = ConcatTo(new[] { baseFile, slow },
                    Path.Combine(_work, "tb_join.mp4"), _work);
                Check("BUKTI: time base berbeda merusak hasil diam-diam",
                      duration > 10.5,
                      duration.ToString("0.00", CultureInfo.InvariantCulture)
                      + " detik dari 10,0");
                Check("time base berbeda ditolak",
                      !Prober.CanStreamCopy(new[] { a, c }, out reasons),
                      "diterima padahal berbeda");
            }

            // -- frame rate ---------------------------------------------------
            string ntsc = Make(Path.Combine(_work, "ntsc.mp4"),
                BaseSource("640x480", "30000/1001"));
            var d = Probed(ntsc);
            Check("frame rate berbeda ditolak",
                  !Prober.CanStreamCopy(new[] { a, d }, out reasons),
                  "30 vs 29,97 diterima");

            // -- resolusi -----------------------------------------------------
            string small = Make(Path.Combine(_work, "small.mp4"),
                BaseSource("320x240"));
            Check("resolusi berbeda ditolak",
                  !Prober.CanStreamCopy(new[] { a, Probed(small) }, out reasons),
                  "diterima");

            // -- tanpa audio --------------------------------------------------
            string mute = Make(Path.Combine(_work, "mute.mp4"),
                "-f", "lavfi", "-i", "testsrc=size=640x480:rate=30:duration=5",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p");
            var e = Probed(mute);
            Check("klip tanpa audio ditolak untuk salin",
                  !Prober.CanStreamCopy(new[] { a, e }, out reasons), "diterima");

            // -- sample rate audio --------------------------------------------
            string sr = Make(Path.Combine(_work, "sr.mp4"),
                "-f", "lavfi", "-i", "testsrc=size=640x480:rate=30:duration=5",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=5",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-ar", "44100", "-ac", "2", "-shortest");
            Check("sample rate audio berbeda ditolak",
                  !Prober.CanStreamCopy(new[] { a, Probed(sr) }, out reasons),
                  "diterima");

            // -- berkas rusak --------------------------------------------------
            string broken = Path.Combine(_work, "rusak.mp4");
            File.WriteAllBytes(broken, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 });
            var f = Probed(broken);
            Check("berkas rusak ditandai tidak valid", !f.Valid, "dianggap valid");

            string empty = Path.Combine(_work, "kosong.mp4");
            File.WriteAllBytes(empty, new byte[0]);
            Check("berkas 0 byte ditandai tidak valid", !Probed(empty).Valid,
                  "dianggap valid");
        }

        // ======================================================= [2] escaping
        private static void TestEscaping()
        {
            Console.WriteLine("\n[2] Escaping jalur pada daftar concat");

            Check("backslash Windows dipertahankan",
                  Merger.EscapeConcatPath(@"C:\Video\a.mp4").Contains(@"\"),
                  Merger.EscapeConcatPath(@"C:\Video\a.mp4"));

            string quoted = Merger.EscapeConcatPath(@"C:\Video\it's here.mp4");
            Check("kutip tunggal di-escape jadi '\\''",
                  quoted.Contains("'\\''"), quoted);

            // Nama berkas dengan kutip tunggal sungguhan harus benar-benar bisa
            // digabung, bukan sekadar lolos pemeriksaan string.
            string tricky = Path.Combine(_work, "it's a 'test' [x].mp4");
            Make(tricky, BaseSource("320x240", "25", "2"));
            string second = Path.Combine(_work, "biasa.mp4");
            Make(second, BaseSource("320x240", "25", "2"));
            double duration = ConcatTo(new[] { tricky, second },
                Path.Combine(_work, "kutip_join.mp4"), _work);
            Check("berkas dengan kutip tunggal benar-benar tergabung",
                  duration > 3.5,
                  duration.ToString("0.00", CultureInfo.InvariantCulture));

            string listPath = Merger.WriteConcatList(new[] { tricky },
                Path.Combine(_work, "bom.txt"));
            byte[] head = File.ReadAllBytes(listPath);
            Check("daftar concat ditulis tanpa BOM",
                  head.Length >= 3 && !(head[0] == 0xEF && head[1] == 0xBB
                                        && head[2] == 0xBF),
                  "ada BOM di depan");

            string text = File.ReadAllText(listPath);
            Check("tidak ada direktif duration di daftar",
                  !text.Contains("duration"), text);

            // Pengutipan argumen Windows. Jalur TANPA spasi tidak dikutip
            // sama sekali, dan backslash di ujungnya memang tidak berbahaya di
            // luar tanda kutip - jebakannya baru muncul saat jalurnya berspasi
            // sehingga harus dikutip, karena backslash terakhir lalu
            // meng-escape tanda kutip penutup dan menelan argumen berikutnya.
            string plain = Shell.BuildArguments(new[] { @"D:\Video\", "-y" });
            Check("jalur tanpa spasi tidak dikutip berlebihan",
                  plain == @"D:\Video\ -y", plain);

            // Diharapkan persis:  "D:\My Video\\" -y
            // Backslash terakhir digandakan supaya ia tidak meng-escape tanda
            // kutip penutup; tanpa itu "-y" ikut tertelan ke dalam argumen
            // pertama dan ffmpeg menerima satu argumen yang tidak dikenalnya.
            string spaced = Shell.BuildArguments(new[] { @"D:\My Video\", "-y" });
            Check("jalur berspasi berakhiran backslash tidak menelan argumen",
                  spaced == @"""D:\My Video\\"" -y", spaced);

            string quotedArg = Shell.BuildArguments(new[] { "a\"b" });
            Check("tanda kutip di dalam argumen di-escape",
                  quotedArg == "\"a\\\"b\"", quotedArg);
        }

        // ===================================================== [3] pengurutan
        private static void TestSorting()
        {
            Console.WriteLine("\n[3] Pengurutan");

            Check("urutan alami: video2 sebelum video10",
                  NaturalOrder.Compare("video2.mp4", "video10.mp4") < 0, "");
            Check("urutan alami tanpa Win32 juga benar",
                  NaturalOrder.CompareNatural("video2.mp4", "video10.mp4") < 0, "");
            Check("nol di depan lebih dulu (video05 sebelum video5)",
                  NaturalOrder.CompareNatural("video05", "video5") < 0, "");
            Check("beda huruf besar-kecil tetap punya urutan pasti",
                  NaturalOrder.Compare("a.mp4", "A.mp4") != 0,
                  "dianggap sama persis");

            var ts = TimestampParser.Parse("CH01_20240105080000.dav");
            Check("pola CCTV 14 digit terurai",
                  ts.HasValue && ts.Value == new DateTime(2024, 1, 5, 8, 0, 0),
                  ts.ToString());

            var hik = TimestampParser.Parse("ch1_20240105080000123.mp4");
            Check("varian Hikvision (milidetik) terurai",
                  hik.HasValue && hik.Value == new DateTime(2024, 1, 5, 8, 0, 0),
                  hik.ToString());

            var dash = TimestampParser.Parse("2024-01-05 08.00.00.mp4");
            Check("pola bertanda pisah terurai",
                  dash.HasValue && dash.Value == new DateTime(2024, 1, 5, 8, 0, 0),
                  dash.ToString());

            Check("bulan 13 ditolak, bukan ditebak",
                  !TimestampParser.Parse("20241305080000.mp4").HasValue, "diterima");
            Check("nama tanpa tanggal mengembalikan kosong",
                  !TimestampParser.Parse("rekaman.mp4").HasValue, "diterima");

            // Kestabilan: berkas tanpa tanggal harus mempertahankan urutan nama.
            var files = new List<VideoFile>
            {
                new VideoFile { Path = @"C:\b.mp4" },
                new VideoFile { Path = @"C:\a.mp4" },
                new VideoFile { Path = @"C:\c.mp4" },
            };
            var sorted = FileSorter.Sort(files, SortBy.MediaCreated);
            Check("pengurutan stabil: tanpa metadata tetap urut nama",
                  sorted[0].Name == "a.mp4" && sorted[1].Name == "b.mp4"
                  && sorted[2].Name == "c.mp4",
                  sorted[0].Name + "," + sorted[1].Name + "," + sorted[2].Name);

            var order = new List<string> { "a", "b", "c", "d" };
            var moved = FileSorter.MoveItems(order, new[] { 2 }, -1);
            Check("geser ke atas menukar baris",
                  order[1] == "c" && order[2] == "b" && moved[0] == 1,
                  string.Join(",", order));

            var top = new List<string> { "a", "b" };
            FileSorter.MoveItems(top, new[] { 0 }, -1);
            Check("baris teratas tidak bergerak saat digeser naik",
                  top[0] == "a", string.Join(",", top));
        }

        // =============================================== [4] hasil terpotong
        private static void TestTruncationGuard()
        {
            Console.WriteLine("\n[4] Penjagaan hasil terpotong (ffmpeg rc=0 walau gagal)");
            string folder = Path.Combine(_work, "potong");
            Directory.CreateDirectory(folder);

            var files = new List<VideoFile>();
            for (int i = 1; i <= 3; i++)
            {
                string path = Make(Path.Combine(folder, "v" + i + ".mp4"),
                    BaseSource("320x240", "25", "2"));
                files.Add(Probed(path));
            }

            // Buktikan dulu: berkas hilang di tengah daftar tetap menghasilkan
            // exit 0 dari ffmpeg mentah.
            string listPath = Merger.WriteConcatList(
                new[] { files[0].Path, Path.Combine(folder, "hilang.mp4"), files[2].Path },
                Path.Combine(folder, "list.txt"));
            var raw = Shell.RunCapture(new[]
            {
                Tools.FFmpeg, "-hide_banner", "-loglevel", "error", "-y",
                "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy",
                Path.Combine(folder, "mentah.mp4"),
            }, 120);
            Check("BUKTI: ffmpeg keluar 0 walau satu berkas hilang",
                  raw.ExitCode == 0, "kode " + raw.ExitCode);

            // Aplikasi harus menolaknya lebih dulu, saat pemeriksaan masukan.
            // Salin seluruh parameter dari berkas yang nyata: kalau tidak,
            // yang menolak adalah pemeriksaan kompatibilitas (parameternya
            // kosong semua) dan penjagaan keterbacaan tidak pernah teruji.
            var ghost = Probed(files[0].Path);
            ghost.Path = Path.Combine(folder, "hilang.mp4");
            ghost.Selected = true;
            var job = new MergeJob
            {
                Files = new List<VideoFile> { files[0], ghost, files[2] },
                OutputPath = Path.Combine(folder, "hasil.mp4"),
                Mode = MergeMode.Copy,
            };
            try
            {
                new Merger(Tools, job, Silent, Quiet).Run();
                Check("berkas hilang ditolak sebelum mulai", false, "tidak menolak");
            }
            catch (MergeException ex)
            {
                Check("berkas hilang ditolak sebelum mulai",
                      ex.Message.Contains("tidak bisa dibaca"), ex.Message);
            }

            // Verifikasi durasi menangkap hasil yang terpotong.
            string shortOut = Make(Path.Combine(folder, "pendek.mp4"),
                BaseSource("320x240", "25", "2"));
            var merger = new Merger(Tools, new MergeJob(), Silent, Quiet);
            try
            {
                merger.VerifyOutput(shortOut, 6.0, files, MergeMode.Copy);
                Check("durasi hasil yang kurang ditolak", false, "diterima");
            }
            catch (MergeException ex)
            {
                Check("durasi hasil yang kurang ditolak",
                      ex.Message.Contains("TERPOTONG"), ex.Message);
            }

            // Penanda log fatal harus dikenali walau kode keluarnya 0.
            Check("penanda log 'impossible to open' dikenali",
                  Array.IndexOf(FFmpegTask.FatalLogMarkers, "impossible to open") >= 0,
                  "");

            // -- sisa folder kerja dari proses yang mati mendadak -------------
            // Proses yang dibunuh Task Manager atau mati karena listrik padam
            // tidak sempat membersihkan dirinya, dan sisanya bisa puluhan GB.
            string sweep = Path.Combine(folder, "sapu");
            Directory.CreateDirectory(sweep);
            string dead = Path.Combine(sweep, ".vmerge_tmp_999999_deadbeef");
            string mine = Path.Combine(sweep,
                ".vmerge_tmp_" + Process.GetCurrentProcess().Id + "_live");
            Directory.CreateDirectory(dead);
            Directory.CreateDirectory(mine);
            File.WriteAllText(Path.Combine(dead, "sisa.mkv"), "x");

            Merger.SweepStaleTemp(sweep);
            Check("folder kerja milik proses mati dihapus",
                  !Directory.Exists(dead), dead);
            Check("folder kerja proses yang masih hidup TIDAK disentuh",
                  Directory.Exists(mine), mine);
        }

        // ====================================================== [5] toleransi
        private static void TestTolerance()
        {
            Console.WriteLine("\n[5] Toleransi durasi");

            // 100 klip @5 menit: 2% akan jadi 600 detik - dua klip utuh.
            var many = new List<VideoFile>();
            for (int i = 0; i < 100; i++) many.Add(new VideoFile { Duration = 300 });

            double copyTol = Merger.DurationTolerance(many, MergeMode.Copy);
            Check("toleransi salin jauh di bawah satu klip",
                  copyTol < 300 * 0.5 + 0.01 && copyTol < 30,
                  copyTol.ToString("0.00", CultureInfo.InvariantCulture));

            double reTol = Merger.DurationTolerance(many, MergeMode.Reencode);
            Check("toleransi encode ulang dibatasi setengah klip terpendek",
                  reTol <= 150.0,
                  reTol.ToString("0.00", CultureInfo.InvariantCulture));

            // Klip pendek: batasnya harus mengecil ikut klip terpendek.
            var shortClips = new List<VideoFile>();
            for (int i = 0; i < 10; i++) shortClips.Add(new VideoFile { Duration = 2 });
            double shortTol = Merger.DurationTolerance(shortClips, MergeMode.Copy);
            Check("klip pendek dapat toleransi kecil",
                  shortTol <= 1.0 + 0.001,
                  shortTol.ToString("0.00", CultureInfo.InvariantCulture));

            Check("kehilangan satu klip selalu melebihi toleransi",
                  300.0 > Merger.DurationTolerance(many, MergeMode.Reencode),
                  "");
        }

        // ================================================ [6] jebakan windows
        private static void TestWindowsTraps()
        {
            Console.WriteLine("\n[6] Jebakan Windows");

            Check("normalisasi pecahan: 90000/3003 == 30000/1001",
                  Prober.NormaliseFraction("90000/3003")
                  == Prober.NormaliseFraction("30000/1001"),
                  Prober.NormaliseFraction("90000/3003"));
            Check("pecahan sampah dikembalikan apa adanya",
                  Prober.NormaliseFraction("abc") == "abc", "");
            Check("pembagi nol tidak menabrak",
                  Prober.NormaliseFraction("1/0") == "1/0", "");
            Check("laju 0/0 jadi 0", Math.Abs(Prober.ParseRate("0/0")) < 1e-9, "");

            Check("timescale dari time base", Merger.TimescaleOf("1/15360") == 15360,
                  Merger.TimescaleOf("1/15360").ToString());
            Check("time base non-1 pembilang diabaikan",
                  Merger.TimescaleOf("2/15360") == 0, "");

            Check("warna ASS dibalik ke BGR",
                  SubtitleStyle.AssColour("#FF8800") == "&H000088FF",
                  SubtitleStyle.AssColour("#FF8800"));
            Check("warna tidak sah mundur ke putih",
                  SubtitleStyle.AssColour("xyz") == "&H00FFFFFF", "");

            string unique = Paths.Unique(Path.Combine(_work, "base.mp4"));
            Check("nama unik dibuat saat berkas sudah ada",
                  unique.Contains("(2)"), unique);

            // Junction: pemindaian rekursif tidak boleh menelusurinya.
            string real = Path.Combine(_work, "nyata");
            Directory.CreateDirectory(real);
            Make(Path.Combine(real, "x.mp4"), BaseSource("320x240", "25", "1"));
            string link = Path.Combine(_work, "tautan");
            var mk = Shell.RunCapture(new[] { "cmd", "/c", "mklink", "/J", link, real }, 30);
            if (mk.ExitCode == 0)
            {
                var found = Scanner.ScanFolder(_work, true);
                int fromLink = 0;
                foreach (var f in found)
                    if (f.Path.IndexOf("tautan", StringComparison.OrdinalIgnoreCase) >= 0)
                        fromLink++;
                Check("junction tidak ditelusuri saat pindai rekursif",
                      fromLink == 0, fromLink + " berkas dari junction");
            }
            else
            {
                Check("junction tidak ditelusuri saat pindai rekursif", true,
                      "(mklink tidak tersedia, dilewati)");
            }
        }

        // ======================================================== [7] progres
        private static void TestProgress()
        {
            Console.WriteLine("\n[7] Penguraian progres");

            // out_time_ms adalah salah nama yang sudah lama: isinya mikrodetik.
            var fields = new Dictionary<string, string> { { "out_time_ms", "5000000" } };
            Check("out_time_ms dibaca sebagai mikrodetik",
                  Math.Abs(FFmpegTask.ParseProgressTime(fields) - 5.0) < 0.001,
                  FFmpegTask.ParseProgressTime(fields).ToString(
                      CultureInfo.InvariantCulture));

            fields = new Dictionary<string, string> { { "out_time_us", "2500000" } };
            Check("out_time_us dibaca benar",
                  Math.Abs(FFmpegTask.ParseProgressTime(fields) - 2.5) < 0.001, "");

            fields = new Dictionary<string, string> { { "out_time", "00:00:07.50" } };
            Check("out_time berformat jam dibaca benar",
                  Math.Abs(FFmpegTask.ParseProgressTime(fields) - 7.5) < 0.001, "");

            fields = new Dictionary<string, string>
            { { "out_time_us", "-9223372036854775807" } };
            Check("nilai sentinel negatif diabaikan",
                  Math.Abs(FFmpegTask.ParseProgressTime(fields)) < 0.001, "");

            fields = new Dictionary<string, string> { { "out_time_us", "N/A" } };
            Check("N/A tidak menabrak",
                  Math.Abs(FFmpegTask.ParseProgressTime(fields)) < 0.001, "");

            // Progres nyata harus naik dan berakhir mendekati 1.
            string folder = Path.Combine(_work, "prog");
            Directory.CreateDirectory(folder);
            var files = new List<VideoFile>();
            for (int i = 1; i <= 3; i++)
                files.Add(Probed(Make(Path.Combine(folder, "p" + i + ".mp4"),
                    BaseSource("320x240", "25", "2"))));

            var seen = new List<double>();
            var job = new MergeJob
            {
                Files = files,
                OutputPath = Path.Combine(folder, "keluar.mp4"),
                Mode = MergeMode.Copy,
            };
            new Merger(Tools, job, p => seen.Add(p.Fraction), Quiet).Run();

            bool monotonic = true;
            for (int i = 1; i < seen.Count; i++)
                if (seen[i] < seen[i - 1] - 0.001) { monotonic = false; break; }
            Check("progres tidak pernah mundur", monotonic, "");
            Check("progres berakhir di 1.0",
                  seen.Count > 0 && Math.Abs(seen[seen.Count - 1] - 1.0) < 0.001,
                  seen.Count > 0 ? seen[seen.Count - 1].ToString("0.000",
                      CultureInfo.InvariantCulture) : "kosong");
            Check("hasil gabungan berdurasi benar",
                  Math.Abs(RealDuration(job.OutputPath) - 6.0) < 0.5,
                  RealDuration(job.OutputPath).ToString("0.00",
                      CultureInfo.InvariantCulture));
        }

        // ====================================================== [8] mode hemat
        private static void TestSmartSelection()
        {
            Console.WriteLine("\n[8] Mode hemat hanya meng-encode yang berbeda");
            string folder = Path.Combine(_work, "hemat");
            Directory.CreateDirectory(folder);

            var files = new List<VideoFile>();
            for (int i = 1; i <= 5; i++)
            {
                string size = i == 3 ? "320x240" : "640x480";
                files.Add(Probed(Make(Path.Combine(folder, "s" + i + ".mp4"),
                    BaseSource(size, "25", "2"))));
            }

            int encodes = 0;
            var job = new MergeJob
            {
                Files = files,
                OutputPath = Path.Combine(folder, "hemat.mp4"),
                Mode = MergeMode.Smart,
                Target = new TargetSpec { Preset = "ultrafast" },
            };
            new Merger(Tools, job,
                p => { if (p.Stage == Stage.Normalizing && p.CurrentIndex > 0) encodes = Math.Max(encodes, p.CurrentIndex); },
                Quiet).Run();

            Check("hanya klip yang menyimpang di-encode ulang",
                  encodes <= 2, encodes + " klip di-encode");
            Check("durasi hasil mode hemat benar",
                  Math.Abs(RealDuration(job.OutputPath) - 10.0) < 0.8,
                  RealDuration(job.OutputPath).ToString("0.00",
                      CultureInfo.InvariantCulture));
        }

        // ================================================= [9] keluaran bersih
        private static void TestCleanOutput()
        {
            Console.WriteLine("\n[9] Hasil tiap mode bebas peringatan timestamp");
            string folder = Path.Combine(_work, "bersih");
            Directory.CreateDirectory(folder);

            var files = new List<VideoFile>();
            for (int i = 1; i <= 5; i++)
            {
                string size = i == 5 ? "320x180" : "640x360";
                files.Add(Probed(Make(Path.Combine(folder, "b" + i + ".mp4"),
                    BaseSource(size, "25", "1"))));
            }

            foreach (MergeMode mode in new[]
                     { MergeMode.Auto, MergeMode.Smart, MergeMode.Reencode })
            {
                string outPath = Path.Combine(_work, "bersih_" + mode + ".mp4");
                var job = new MergeJob
                {
                    Files = files,
                    OutputPath = outPath,
                    Mode = mode,
                    Target = new TargetSpec { Preset = "ultrafast" },
                };
                new Merger(Tools, job, Silent, Quiet).Run();

                var problems = DecodeWarnings(outPath);
                Check("mode " + mode + ": tidak ada peringatan saat dibaca ulang",
                      problems.Count == 0,
                      problems.Count > 0 ? problems[0] : "");
                Check("mode " + mode + ": durasi mendekati 5,0 detik",
                      Math.Abs(RealDuration(outPath) - 5.0) < 0.6,
                      RealDuration(outPath).ToString("0.000",
                          CultureInfo.InvariantCulture));
            }
        }

        // ======================================================== [10] hardsub
        private const string SrtText =
            "1\n00:00:00,200 --> 00:00:04,800\nSUBTITLE UJI COBA\n\n";

        private static void TestHardsub()
        {
            Console.WriteLine("\n[10] Hardsub (subtitle permanen)");
            string folder = Path.Combine(_work, "hardsub");
            Directory.CreateDirectory(folder);

            // Sumber HITAM POLOS: piksel terang apa pun setelahnya hanya bisa
            // berasal dari teks yang benar-benar digambar.
            string black = Make(Path.Combine(folder, "episode01_src.mp4"),
                "-f", "lavfi", "-i", "color=c=black:size=640x360:rate=25:duration=5",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=5",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-ar", "48000", "-ac", "2", "-shortest");

            string srt = Path.Combine(folder, "episode01.srt");
            File.WriteAllText(srt, SrtText, new System.Text.UTF8Encoding(false));

            string mkv = Path.Combine(folder, "episode01.mkv");
            var mux = Shell.RunCapture(new[]
            {
                Tools.FFmpeg, "-hide_banner", "-loglevel", "error", "-y",
                "-i", black, "-i", srt, "-map", "0", "-map", "1",
                "-c", "copy", "-c:s", "srt", mkv,
            }, 120);
            if (mux.ExitCode != 0) throw new Exception("mux gagal: " + mux.StdErr);

            Check("sumber: trek subtitle terbaca", SubtitleStreamCount(mkv) == 1,
                  SubtitleStreamCount(mkv).ToString());
            double dark = Brightest(mkv);
            Check("sumber: gambarnya hitam polos (softsub tidak tergambar)",
                  dark < 40, "YMAX=" + dark);   // 16 = hitam pada rentang terbatas

            var tracks = Subtitles.ListTracks(Tools, mkv);
            Check("deteksi trek subtitle di dalam MKV", tracks.Count == 1,
                  tracks.Count.ToString());
            Check("trek dikenali sebagai teks",
                  tracks.Count > 0 && tracks[0].IsText,
                  tracks.Count > 0 ? tracks[0].Codec : "-");

            // -- bakar dari trek tertanam --------------------------------------
            var video = Probed(mkv);
            var item = new HardsubItem { Video = video, Track = tracks[0], Tracks = tracks };
            var job = new HardsubJob
            {
                Items = new List<HardsubItem> { item },
                OutputDir = folder,
                Container = ".mp4",
                Target = new TargetSpec { Preset = "ultrafast", Crf = 20 },
            };
            var result = new Hardsubber(Tools, job, Silent, Quiet).Run();

            Check("hardsub menghasilkan satu berkas", result.Done.Count == 1,
                  result.Done.Count.ToString());
            if (result.Done.Count == 0) return;
            string outPath = result.Done[0];

            double lit = Brightest(outPath);
            Check("teks benar-benar terbakar ke gambar", lit > 150,
                  "YMAX=" + lit + " (sumber " + dark + ")");
            Check("trek subtitle lunak dibuang dari hasil",
                  SubtitleStreamCount(outPath) == 0,
                  SubtitleStreamCount(outPath).ToString());
            Check("durasi hasil sama dengan sumber",
                  Math.Abs(RealDuration(outPath) - RealDuration(mkv)) < 0.35,
                  RealDuration(outPath).ToString("0.000", CultureInfo.InvariantCulture));
            Check("audio ikut terbawa", Probed(outPath).HasAudio, "tidak ada audio");

            // -- bakar dari .srt di sebelahnya ---------------------------------
            var sidecars = Subtitles.SidecarSubs(mkv);
            Check("berkas .srt di samping video ditemukan",
                  sidecars.Count > 0 && string.Equals(sidecars[0], srt,
                      StringComparison.OrdinalIgnoreCase),
                  string.Join(",", sidecars));

            var plain = Probed(Make(Path.Combine(folder, "polos.mp4"),
                "-f", "lavfi", "-i", "color=c=black:size=640x360:rate=25:duration=5",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"));
            var extJob = new HardsubJob
            {
                Items = new List<HardsubItem>
                { new HardsubItem { Video = plain, ExternalPath = srt } },
                OutputDir = folder,
                Suffix = " - eksternal",
                Container = ".mkv",
                Target = new TargetSpec { Preset = "ultrafast", Crf = 20 },
            };
            var ext = new Hardsubber(Tools, extJob, Silent, Quiet).Run();
            Check("hardsub dari berkas .srt terpisah berhasil", ext.Done.Count == 1,
                  ext.Failed.Count > 0 ? ext.Failed[0].Value : "");
            if (ext.Done.Count > 0)
                Check("teks dari .srt terpisah ikut terbakar",
                      Brightest(ext.Done[0]) > 150,
                      "YMAX=" + Brightest(ext.Done[0]));

            // -- gaya benar-benar sampai ke libass ------------------------------
            var styledJob = new HardsubJob
            {
                Items = new List<HardsubItem>
                { new HardsubItem { Video = plain, ExternalPath = srt } },
                OutputDir = folder,
                Suffix = " - besar",
                Container = ".mp4",
                Style = new SubtitleStyle { Enabled = true, Size = 48, Primary = "#FFFFFF" },
                Target = new TargetSpec { Preset = "ultrafast", Crf = 20 },
            };
            var styled = new Hardsubber(Tools, styledJob, Silent, Quiet).Run();
            if (styled.Done.Count > 0 && ext.Done.Count > 0)
            {
                // Font lebih besar melukis lebih banyak piksel menyala.
                // Membandingkan LUAS, bukan puncak kecerahan, itulah yang
                // membuat tes ini peka pada setelan ukuran - puncaknya putih
                // pada kedua ukuran.
                long big = LitArea(styled.Done[0]);
                long small = LitArea(ext.Done[0]);
                Check("ukuran font pada gaya subtitle benar-benar berpengaruh",
                      big > small * 1.3, "besar=" + big + " kecil=" + small);
            }

            // -- menolak menimpa masukannya sendiri -----------------------------
            var sameJob = new HardsubJob
            {
                Items = new List<HardsubItem>
                { new HardsubItem { Video = video, Track = tracks[0] } },
                OutputDir = folder,
                Suffix = "",
                Container = ".mkv",
            };
            try
            {
                new Hardsubber(Tools, sameJob, Silent, Quiet).Run();
                Check("menolak menimpa video aslinya", false, "tidak menolak");
            }
            catch (MergeException ex)
            {
                Check("menolak menimpa video aslinya",
                      ex.Message.Contains("sama dengan"), ex.Message);
            }

            // -- video tanpa subtitle dilaporkan, bukan ditebak ------------------
            var collected = Hardsubber.CollectSources(Tools, new[] { plain });
            Check("video tanpa subtitle ditandai, bukan diproses diam-diam",
                  collected.Count > 0 && !collected[0].Selected
                  && collected[0].Error.Length > 0, "");

            // -- trek forced tidak dipilih otomatis -----------------------------
            var withForced = new List<SubtitleTrack>
            {
                new SubtitleTrack { StreamIndex = 0, Codec = "subrip",
                                    Language = "eng", Forced = true },
                new SubtitleTrack { StreamIndex = 1, Codec = "subrip",
                                    Language = "eng" },
            };
            var picked = Subtitles.PickDefaultTrack(withForced);
            Check("trek 'forced' tidak dipilih kalau ada trek penuh",
                  picked != null && !picked.Forced, "memilih trek forced");

            var indoLast = new List<SubtitleTrack>
            {
                new SubtitleTrack { StreamIndex = 0, Codec = "subrip", Language = "eng",
                                    Default = true },
                new SubtitleTrack { StreamIndex = 1, Codec = "subrip", Language = "ind" },
            };
            Check("trek berbahasa Indonesia diutamakan",
                  Subtitles.PickDefaultTrack(indoLast).Language == "ind", "");
        }

        // ===================================================== [11] pengaturan
        private static void TestSettings()
        {
            Console.WriteLine("\n[11] Pengaturan tahan berkas rusak");

            var settings = new AppSettings();
            settings.Set("crf", 999);
            Check("nilai di luar jangkauan dijepit saat dibaca",
                  settings.GetInt("crf", 0, 51) == 51,
                  settings.GetInt("crf", 0, 51).ToString());

            settings["crf"] = "tinggi";
            Check("nilai bukan angka mundur ke bawaan, bukan menabrak",
                  settings.GetInt("crf", 0, 51) == 23,
                  settings.GetInt("crf", 0, 51).ToString());

            settings["sort_key"] = "tidak_ada";
            Check("kunci pengurutan tak dikenal mundur ke Name",
                  settings.SortBy == SortBy.Name, settings.SortBy.ToString());

            settings["merge_mode"] = "??";
            Check("mode tak dikenal mundur ke Auto",
                  settings.MergeMode == MergeMode.Auto, "");

            Check("bool menerima 'ya'",
                  new AppSettingsProbe("ya").Value, "");
            Check("bool menolak sampah",
                  !new AppSettingsProbe("mungkin").Value, "");
        }

        // ============================================ [12] encoder & update
        private static void TestEncoderSelection()
        {
            Console.WriteLine("\n[12] Pemilihan encoder otomatis");

            var listed = EncoderBenchmark.Listed(Tools);
            Check("daftar encoder terbaca dari FFmpeg", listed.Count > 0,
                  listed.Count.ToString());

            bool hasCpu = false;
            foreach (var c in listed) if (c.Id == "libx264") hasCpu = true;
            Check("libx264 ada sebagai patokan", hasCpu, "");

            // Bendera yang dipakai benchmark HARUS sama dengan yang dipakai
            // saat bekerja - kalau tidak, angkanya benar untuk pertanyaan yang
            // salah.
            var flags = Merger.EncoderFlagsFor("libx264",
                                               new TargetSpec { Preset = "veryfast" });
            Check("bendera encoder memuat preset produksi",
                  flags.Contains("veryfast"), string.Join(" ", flags));

            var nvenc = Merger.EncoderFlagsFor("h264_nvenc", new TargetSpec { Crf = 23 });
            Check("NVENC pakai -cq, bukan -crf",
                  nvenc.Contains("-cq") && !nvenc.Contains("-crf"),
                  string.Join(" ", nvenc));
            Check("offset cq NVENC = crf + 5",
                  nvenc[nvenc.IndexOf("-cq") + 1] == "28",
                  nvenc[nvenc.IndexOf("-cq") + 1]);

            // Pengukuran sungguhan. libx264 pasti jalan, jadi hasilnya harus
            // punya minimal satu yang bekerja dengan fps di atas nol.
            var scores = EncoderBenchmark.Measure(Tools);
            Check("pengukuran menghasilkan angka", scores.Count > 0,
                  scores.Count.ToString());

            bool anyWorks = false;
            double bestFps = 0;
            foreach (var score in scores)
            {
                if (!score.Works) continue;
                anyWorks = true;
                if (score.Fps > bestFps) bestFps = score.Fps;
            }
            Check("minimal satu encoder benar-benar jalan", anyWorks, "");
            Check("fps terukur masuk akal (> 1)", bestFps > 1,
                  bestFps.ToString("0.0", CultureInfo.InvariantCulture));

            // Best() harus memilih yang tercepat, dan menerjemahkan libx264
            // jadi "" karena itu memang nilai bawaan aplikasi.
            var fake = new List<EncoderScore>
            {
                new EncoderScore { Candidate = EncoderBenchmark.ById("libx264"),
                                   Works = true, Fps = 50 },
                new EncoderScore { Candidate = EncoderBenchmark.ById("h264_nvenc"),
                                   Works = true, Fps = 300 },
            };
            Check("yang tercepat yang dipilih",
                  EncoderBenchmark.Best(fake) == "h264_nvenc",
                  EncoderBenchmark.Best(fake));

            var cpuOnly = new List<EncoderScore>
            {
                new EncoderScore { Candidate = EncoderBenchmark.ById("libx264"),
                                   Works = true, Fps = 50 },
                new EncoderScore { Candidate = EncoderBenchmark.ById("h264_nvenc"),
                                   Works = false, Fps = 0 },
            };
            Check("encoder yang tidak jalan tidak pernah dipilih",
                  EncoderBenchmark.Best(cpuOnly) == "", EncoderBenchmark.Best(cpuOnly));

            // H.265 sering paling cepat, tetapi memilihnya diam-diam mengubah
            // codec keluaran jadi HEVC - persis yang tidak bisa diputar TV lama
            // dan pemutar DVD yang jadi alasan aplikasi ini ada.
            var hevcFastest = new List<EncoderScore>
            {
                new EncoderScore { Candidate = EncoderBenchmark.ById("hevc_nvenc"),
                                   Works = true, Fps = 400 },
                new EncoderScore { Candidate = EncoderBenchmark.ById("libx264"),
                                   Works = true, Fps = 318 },
            };
            Check("H.265 tidak pernah dipilih otomatis walau paling cepat",
                  EncoderBenchmark.Best(hevcFastest) == "",
                  EncoderBenchmark.Best(hevcFastest));
            Check("H.265 tetap ada di daftar untuk dipilih manual",
                  !EncoderBenchmark.ById("hevc_nvenc").AutoSelectable
                  && EncoderBenchmark.ById("hevc_nvenc").Codec == "hevc", "");

            // Angkanya harus bertahan lewat penyimpanan, bukan cuma
            // kalimatnya: daftar pilihan encoder menyebut fps tiap pilihan dan
            // mematikan yang tidak jalan, jadi keduanya harus terbaca kembali.
            var sample = new List<EncoderScore>
            {
                new EncoderScore { Candidate = EncoderBenchmark.ById("h264_nvenc"),
                                   Works = true, Fps = 297.4 },
                new EncoderScore { Candidate = EncoderBenchmark.ById("h264_amf"),
                                   Works = false },
            };
            var round = EncoderBenchmark.Deserialize(
                EncoderBenchmark.Serialize(sample));
            Check("serialisasi mempertahankan jumlah encoder",
                  round.Count == 2, round.Count.ToString());
            Check("serialisasi mempertahankan fps",
                  round.Count == 2 && round[0].Works
                  && Math.Abs(round[0].Fps - 297.4) < 0.05,
                  round.Count == 2 ? round[0].Fps.ToString("0.0",
                      CultureInfo.InvariantCulture) : "-");
            Check("serialisasi mempertahankan status tidak-jalan",
                  round.Count == 2 && !round[1].Works
                  && round[1].Candidate.Id == "h264_amf", "");
            Check("teks rusak tidak menabrak",
                  EncoderBenchmark.Deserialize("sampah;=;x=1;h264_nvenc").Count == 0,
                  "");

            // Cache dari versi LAMA menyimpan kalimat siap tampil, bukan
            // pasangan id=fps. Sidik jarinya tetap cocok, jadi menerimanya
            // membuat daftar pilihan kehilangan seluruh angkanya DAN tidak
            // pernah mengukur ulang. Harus diperlakukan sebagai belum diukur.
            var stale = new AppSettings();
            stale.Set("encoder_bench_fingerprint", Hardware.Fingerprint(Tools));
            stale.Set("encoder_bench_best", "");
            stale.Set("encoder_bench_detail",
                      "NVIDIA NVENC (H.264): 305 fps; CPU (libx264): 317 fps");
            string staleBest;
            List<EncoderScore> staleScores;
            Check("cache format lama dianggap belum diukur",
                  !EncoderBenchmark.TryCached(stale, Tools, out staleBest,
                                              out staleScores),
                  "diterima, sehingga fps tidak akan pernah muncul lagi");

            // H.265 bukan milik NVIDIA saja: Intel dan AMD punya encoder
            // perangkat kerasnya sendiri, dan libx265 mengerjakannya di CPU.
            var hevcIds = new List<string>();
            foreach (var c in EncoderBenchmark.Candidates)
                if (c.Codec == "hevc") hevcIds.Add(c.Id);
            Check("kandidat H.265 mencakup NVIDIA, Intel, AMD, dan CPU",
                  hevcIds.Contains("hevc_nvenc") && hevcIds.Contains("hevc_qsv")
                  && hevcIds.Contains("hevc_amf") && hevcIds.Contains("libx265"),
                  string.Join(",", hevcIds));
            Check("tidak satu pun H.265 dipilih otomatis",
                  EncoderBenchmark.Candidates.All(c => c.Codec != "hevc"
                                                       || !c.AutoSelectable), "");

            // Cache hanya sah untuk sidik jari yang sama.
            var settings = new AppSettings();
            EncoderBenchmark.StoreCache(settings, Tools, scores, true);
            string best;
            List<EncoderScore> cached;
            Check("hasil tersimpan terbaca kembali",
                  EncoderBenchmark.TryCached(settings, Tools, out best, out cached), "");
            settings.Set("encoder_bench_fingerprint", "perangkat-lain");
            Check("sidik jari berbeda membatalkan cache",
                  !EncoderBenchmark.TryCached(settings, Tools, out best, out cached), "");

            // Hasil separuh jadi TIDAK BOLEH tersimpan. Cache-nya diikat ke
            // sidik jari perangkat keras, jadi pemenang yang salah bertahan
            // sampai GPU atau versi FFmpeg berubah - bisa berbulan-bulan, tanpa
            // gejala apa pun yang terlihat pengguna.
            var partial = new AppSettings();
            EncoderBenchmark.StoreCache(partial, Tools, scores, false);
            Check("pengukuran yang dibatalkan tidak ikut tersimpan",
                  !EncoderBenchmark.TryCached(partial, Tools, out best, out cached),
                  "hasil separuh jadi masuk cache");

            // Pembatalan benar-benar memotong di tengah, bukan sekadar
            // menandai: kalau tidak, menekan Batalkan tetap berarti menunggu
            // seluruh kandidat selesai.
            var clock = Stopwatch.StartNew();
            var cut = EncoderBenchmark.Measure(Tools, new TargetSpec(), null,
                                               () => true);
            clock.Stop();
            Check("pembatalan menghentikan pengukuran sebelum kandidat pertama",
                  cut.Count == 0,
                  cut.Count + " kandidat terlanjur diukur");
            Check("pembatalan berlangsung seketika",
                  clock.Elapsed.TotalSeconds < 2.0,
                  clock.Elapsed.TotalSeconds.ToString("0.0",
                      CultureInfo.InvariantCulture) + " detik");
        }

        private static void TestUpdateCheck()
        {
            Console.WriteLine("\n[13] Pemeriksaan pembaruan FFmpeg");

            // Versi terpasang SELALU membawa akhiran build, versi di server
            // tidak. Membandingkannya sebagai teks membuat setiap pemeriksaan
            // melaporkan "ada versi baru" selamanya.
            Check("akhiran build diabaikan saat membandingkan",
                  FFmpegUpdater.CompareVersions("8.1.1-full_build-www.gyan.dev",
                                                "8.1.1") == 0, "");
            Check("versi lebih lama terdeteksi",
                  FFmpegUpdater.CompareVersions("7.1", "8.1.1") < 0, "");
            Check("versi lebih baru terdeteksi",
                  FFmpegUpdater.CompareVersions("8.2", "8.1.1") > 0, "");
            Check("jumlah ruas berbeda dibandingkan benar",
                  FFmpegUpdater.CompareVersions("8.1", "8.1.0") == 0, "");
            Check("ruas bergaya git (6.1n) terbaca",
                  FFmpegUpdater.CompareVersions("6.1n", "6.1") == 0, "");

            // FFmpeg dari winget/PATH bukan milik aplikasi, jadi tidak boleh
            // ditimpa - pembaruan berikutnya dari alat aslinya akan bertabrakan.
            Check("FFmpeg dari luar tidak dianggap milik aplikasi",
                  !FFmpegUpdater.IsManagedByApp(Tools),
                  Tools.Source + " -> " + Tools.FFmpeg);

            var own = new FFmpegTools
            {
                FFmpeg = Path.Combine(FFmpegLocator.InstallDir(), "bin", "ffmpeg.exe"),
                FFprobe = Path.Combine(FFmpegLocator.InstallDir(), "bin", "ffprobe.exe"),
            };
            Check("FFmpeg di folder aplikasi dianggap milik aplikasi",
                  FFmpegUpdater.IsManagedByApp(own), own.FFmpeg);

            // Folder TETANGGA yang namanya kebetulan berawalan sama. Tanpa
            // pemisah folder di ujung, perbandingan awalan ikut mencocokinya
            // dan aplikasi menawarkan pembaruan otomatis untuk pemasangan yang
            // bukan miliknya.
            var sibling = new FFmpegTools
            {
                FFmpeg = FFmpegLocator.InstallDir() + "-manual"
                         + Path.DirectorySeparatorChar + "bin"
                         + Path.DirectorySeparatorChar + "ffmpeg.exe",
            };
            Check("folder tetangga berawalan sama BUKAN milik aplikasi",
                  !FFmpegUpdater.IsManagedByApp(sibling), sibling.FFmpeg);

            // Inti dari cara aplikasi memperbarui FFmpeg milik alat lain:
            // salinannya sendiri TIDAK menimpa apa pun, ia hanya diletakkan di
            // folder yang dicari lebih dulu. Kalau urutan ini bergeser,
            // pembaruan akan tampak berhasil lalu diam-diam tidak terpakai.
            var order = FFmpegLocator.SearchOrder();
            int mine = -1, winget = -1, system = -1;
            for (int i = 0; i < order.Count; i++)
            {
                if (mine < 0 && order[i].Key == "unduhan aplikasi") mine = i;
                if (winget < 0 && order[i].Key == "winget") winget = i;
                if (system < 0 && order[i].Key == "terpasang di sistem") system = i;
            }
            Check("folder unduhan aplikasi ada di daftar pencarian", mine >= 0,
                  mine.ToString());
            Check("unduhan aplikasi dicari sebelum chocolatey/scoop",
                  mine >= 0 && system > mine, "unduhan=" + mine + " sistem=" + system);
            if (winget >= 0)
                Check("unduhan aplikasi dicari sebelum winget", mine < winget,
                      "unduhan=" + mine + " winget=" + winget);
            else
                Check("unduhan aplikasi dicari sebelum winget", true,
                      "(winget tidak terpasang di mesin ini, dilewati)");

            // Folder pilihan pengguna tetap paling diutamakan.
            var manual = FFmpegLocator.SearchOrder(@"D:\ffmpeg");
            Check("folder pilihan pengguna mengalahkan semuanya",
                  manual.Count > 0 && manual[0].Key == "pengaturan",
                  manual.Count > 0 ? manual[0].Key : "kosong");

            // -- sisa pemasangan yang gagal -----------------------------------
            // Unduhan yang dibatalkan atau gagal dulu meninggalkan folder
            // kosong permanen di %APPDATA%, dan folder itu tetap terbaca
            // sebagai "unduhan aplikasi" di daftar pencarian.
            string fakeTarget = Path.Combine(_work, "ffmpeg_gagal");
            string fakeBin = Path.Combine(fakeTarget, "bin");
            Directory.CreateDirectory(fakeBin);
            FFmpegLocator.CleanEmptyInstall(fakeBin, fakeTarget);
            Check("pemasangan kosong dibuang", !Directory.Exists(fakeTarget),
                  fakeTarget);

            // Setengah jadi juga tidak berguna: satu exe tanpa pasangannya
            // membuat setiap berkas dilaporkan rusak.
            Directory.CreateDirectory(fakeBin);
            File.WriteAllText(Path.Combine(fakeBin, "ffmpeg.exe"), "x");
            FFmpegLocator.CleanEmptyInstall(fakeBin, fakeTarget);
            Check("pemasangan setengah jadi dibuang", !Directory.Exists(fakeTarget),
                  fakeTarget);

            // Yang lengkap tidak boleh disentuh sama sekali.
            Directory.CreateDirectory(fakeBin);
            File.WriteAllText(Path.Combine(fakeBin, "ffmpeg.exe"), "x");
            File.WriteAllText(Path.Combine(fakeBin, "ffprobe.exe"), "x");
            FFmpegLocator.CleanEmptyInstall(fakeBin, fakeTarget);
            Check("pemasangan lengkap TIDAK ikut terhapus",
                  File.Exists(Path.Combine(fakeBin, "ffmpeg.exe")), fakeBin);

            // Penjadwalan: mati kalau tidak diminta, hidup kalau belum pernah.
            var settings = new AppSettings();
            settings.Set("ffmpeg_auto_check", false);
            Check("pemeriksaan otomatis bisa dimatikan",
                  !FFmpegUpdater.DueForCheck(settings), "");
            settings.Set("ffmpeg_auto_check", true);
            Check("belum pernah diperiksa berarti waktunya memeriksa",
                  FFmpegUpdater.DueForCheck(settings), "");
            FFmpegUpdater.MarkChecked(settings);
            Check("baru diperiksa berarti belum waktunya lagi",
                  !FFmpegUpdater.DueForCheck(settings), settings["ffmpeg_last_check"]);

            // Sidik jari perangkat keras harus berubah kalau FFmpeg-nya berubah.
            var other = new FFmpegTools { FFmpeg = Tools.FFmpeg, FFprobe = Tools.FFprobe,
                                          Version = "0.0.0" };
            Check("sidik jari ikut versi FFmpeg",
                  Hardware.Fingerprint(Tools) != Hardware.Fingerprint(other), "");
            Check("sidik jari stabil untuk masukan sama",
                  Hardware.Fingerprint(Tools) == Hardware.Fingerprint(Tools), "");
        }


        // =================================================== [14] perkiraan
        /// <summary>Klip buatan dengan geometri tertentu, tanpa menyentuh disk.</summary>
        private static VideoFile FakeClip(int w, int h, double fps,
                                          double duration, long size)
        {
            return new VideoFile
            {
                Path = @"C:\uji\" + w + "x" + h + "_" + size + ".mp4",
                Width = w, Height = h, Fps = fps, Duration = duration, Size = size,
                Valid = true, Selected = true, HasVideo = true,
                VCodec = "h264", PixFmt = "yuv420p", VTimeBase = "1/15360",
                VRate = "30/1", Sar = "1:1",
            };
        }

        /// <summary>
        /// Fungsi perkiraan yang dipakai tampilan SEBELUM proses dimulai:
        /// ukuran hasil, puncak ruang disk, dan geometri keluaran.
        ///
        /// Semuanya fungsi murni dan gampang meleset tanpa gejala - perkiraan
        /// yang terlalu besar menolak memulai proses yang sebenarnya muat,
        /// perkiraan yang terlalu kecil membiarkannya mati di tengah jalan
        /// karena disk penuh.
        /// </summary>
        private static void TestEstimates()
        {
            Console.WriteLine("\n[14] Perkiraan ukuran, ruang disk, dan geometri");

            // -- geometri: mayoritas, bukan yang pertama --------------------
            var mixed = new List<VideoFile>
            {
                FakeClip(1920, 1080, 30, 10, 100),      // satu 1080p30
                FakeClip(640, 480, 25, 10, 100),        // tiga 480p25
                FakeClip(640, 480, 25, 10, 100),
                FakeClip(640, 480, 25, 10, 100),
            };
            int w, h; double fps;
            bool have = Merger.EstimateGeometry(mixed, out w, out h, out fps);
            Check("geometri memakai resolusi mayoritas",
                  have && w == 640 && h == 480, w + "x" + h);
            Check("geometri memakai fps mayoritas",
                  Math.Abs(fps - 25) < 0.01,
                  fps.ToString("0.##", CultureInfo.InvariantCulture));

            Check("daftar kosong mundur ke 1920x1080 tanpa menabrak",
                  !Merger.EstimateGeometry(new List<VideoFile>(), out w, out h, out fps)
                  && w == 1920 && h == 1080, w + "x" + h);
            Check("null tidak menabrak",
                  !Merger.EstimateGeometry(null, out w, out h, out fps), "");
            Check("klip yang belum diperiksa mundur ke nilai bawaan",
                  !Merger.EstimateGeometry(
                      new List<VideoFile> { new VideoFile { Duration = 10 } },
                      out w, out h, out fps) && w == 1920, w + "x" + h);

            // -- ukuran hasil ------------------------------------------------
            // 100 klip 720p25 @5 menit: kasus yang jadi sasaran aplikasi ini.
            var many = new List<VideoFile>();
            for (int i = 0; i < 100; i++)
                many.Add(FakeClip(1280, 720, 25, 300, 300L * 1024 * 1024));

            long inBytes = 100L * 300 * 1024 * 1024;
            long copyBytes = Merger.EstimateOutputBytes(many, MergeMode.Copy);
            Check("mode salin diperkirakan sebesar masukannya",
                  copyBytes > inBytes && copyBytes < (long)(inBytes * 1.1),
                  Humanize.Size(copyBytes) + " dari " + Humanize.Size(inBytes));

            long reBytes = Merger.EstimateOutputBytes(many, MergeMode.Reencode);
            // Rumus lama yang memakai MB/detik tetap menuntut 126 GB di sini,
            // dan itu menolak memulai di disk mana pun yang wajar.
            Check("encode ulang 720p 8 jam diperkirakan wajar (1-30 GB)",
                  reBytes > 1L * 1024 * 1024 * 1024
                  && reBytes < 30L * 1024 * 1024 * 1024,
                  Humanize.Size(reBytes));
            Check("encode ulang jauh lebih kecil daripada masukannya",
                  reBytes < copyBytes,
                  Humanize.Size(reBytes) + " vs " + Humanize.Size(copyBytes));

            // Resolusi memang harus mengubah perkiraan - kalau tidak, rumusnya
            // mengabaikan geometri dan angkanya cuma hiasan.
            var small = new List<VideoFile>();
            for (int i = 0; i < 100; i++)
                small.Add(FakeClip(640, 480, 25, 300, 300L * 1024 * 1024));
            Check("sumber beresolusi kecil diperkirakan lebih kecil",
                  Merger.EstimateOutputBytes(small, MergeMode.Reencode) < reBytes,
                  Humanize.Size(Merger.EstimateOutputBytes(small, MergeMode.Reencode)));

            // -- puncak ruang disk -------------------------------------------
            Check("puncak disk mode salin sama dengan ukuran hasil",
                  Merger.PeakDiskNeed(many, MergeMode.Copy) == copyBytes, "");
            // Klip ternormalisasi hidup berdampingan dengan berkas akhir
            // sampai penyambungan selesai.
            Check("puncak disk encode ulang dua kali ukuran hasil",
                  Merger.PeakDiskNeed(many, MergeMode.Reencode) == reBytes * 2, "");

            Check("daftar kosong tidak menabrak",
                  Merger.EstimateOutputBytes(new List<VideoFile>(),
                                             MergeMode.Copy) == 0, "");
            Check("null tidak menabrak",
                  Merger.EstimateOutputBytes(null, MergeMode.Reencode) == 0, "");

            // -- tanda tangan mayoritas ---------------------------------------
            var odd = FakeClip(320, 240, 25, 10, 100);
            var group = new List<VideoFile>
            {
                FakeClip(640, 480, 25, 10, 100),
                FakeClip(640, 480, 25, 10, 100),
                odd,
            };
            string majority = Merger.MajoritySignature(group);
            Check("mayoritas bukan klip yang menyimpang",
                  majority == group[0].CopySignature()
                  && majority != odd.CopySignature(), "");

            // -- format drive dan deteksi FAT32 -------------------------------
            string fmt = Paths.DriveFormat(Path.GetTempPath());
            Check("format drive terbaca", !string.IsNullOrEmpty(fmt), fmt);
            // Jalur yang belum ada harus naik ke folder induknya, bukan gagal:
            // folder tujuan sering belum dibuat saat perkiraan dihitung.
            Check("jalur yang belum ada tetap mengembalikan format",
                  !string.IsNullOrEmpty(Paths.DriveFormat(
                      Path.Combine(Path.GetTempPath(), "belum", "ada", "sama", "sekali"))),
                  "");

            // exFAT memuat huruf "FAT" tetapi TIDAK punya batas 4 GB.
            // Menyamakannya berarti memperingatkan orang tanpa alasan.
            foreach (var pair in new[]
                     {
                         Tuple.Create("FAT32", true), Tuple.Create("FAT", true),
                         Tuple.Create("exFAT", false), Tuple.Create("NTFS", false),
                     })
            {
                bool isFat = pair.Item1.IndexOf("FAT",
                                 StringComparison.OrdinalIgnoreCase) >= 0
                             && pair.Item1.IndexOf("exFAT",
                                 StringComparison.OrdinalIgnoreCase) < 0;
                Check("deteksi batas 4 GB untuk " + pair.Item1,
                      isFat == pair.Item2, "isFat=" + isFat);
            }
        }

        // ============================================== [15] riwayat folder
        /// <summary>
        /// Daftar folder terakhir disimpan sebagai satu baris dipisah "|" di
        /// dalam berkas pengaturan yang juga berformat baris key=value.
        /// Jalur Windows penuh backslash dan jalur UNC diawali dua backslash,
        /// jadi kalau escape-nya salah daftarnya kembali dalam keadaan rusak -
        /// dan tidak ada gejala apa pun sampai seseorang membukanya.
        /// </summary>
        private static void TestRecentFolders()
        {
            Console.WriteLine("\n[15] Daftar folder terakhir bertahan lewat penyimpanan");

            string cfg = AppSettings.ConfigPath();
            string backup = cfg + ".testbak";
            bool had = File.Exists(cfg);
            if (had) File.Copy(cfg, backup, true);
            try
            {
                string[] folders =
                {
                    @"C:\Video\Rekaman CCTV",
                    @"D:\Arsip\2024",
                    @"\\server\share\video",
                };
                string joined = string.Join("|", folders);

                var write = new AppSettings();
                write.Set("recent_input_dirs", joined);
                Check("pengaturan tersimpan", write.Save(), "");

                var read = AppSettings.Load();
                string got = read["recent_input_dirs"];
                Check("daftar terbaca kembali persis sama", got == joined, got);

                string[] parts = got.Split('|');
                Check("jumlah folder tetap", parts.Length == 3,
                      parts.Length.ToString(CultureInfo.InvariantCulture));
                Check("backslash tunggal tidak berlipat",
                      parts.Length > 0 && parts[0] == folders[0],
                      parts.Length > 0 ? parts[0] : "-");
                Check("jalur UNC dua backslash utuh",
                      parts.Length > 2 && parts[2] == folders[2],
                      parts.Length > 2 ? parts[2] : "-");

                // Kunci yang tidak dikenal harus diabaikan, bukan ikut tersimpan:
                // berkas dari versi lebih baru tidak boleh menyeret setelan yang
                // tidak dimengerti versi ini.
                var stray = AppSettings.Load();
                Check("kunci tak dikenal tidak muncul dari mana-mana",
                      stray["kunci_yang_tidak_ada"] == "", "");
            }
            finally
            {
                if (had) { File.Copy(backup, cfg, true); File.Delete(backup); }
                else if (File.Exists(cfg)) File.Delete(cfg);
            }
        }

        private class AppSettingsProbe
        {
            public readonly bool Value;
            public AppSettingsProbe(string raw)
            {
                var s = new AppSettings();
                s["recursive"] = raw;
                Value = s.GetBool("recursive");
            }
        }
    }
}
