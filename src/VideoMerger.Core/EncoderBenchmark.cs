using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoMerger.Core
{
    public class EncoderCandidate
    {
        public string Id = "";          // "h264_nvenc", "libx264", ...
        public string Label = "";
        public string Vendor = "";      // "NVIDIA", "Intel", "AMD", "CPU"
        public string Codec = "h264";   // codec keluaran yang dihasilkannya

        public bool IsHardware => Vendor != "CPU";

        /// <summary>
        /// Boleh dipilih oleh mode otomatis?
        ///
        /// Hanya H.264. H.265 sering paling cepat - pada satu mesin uji
        /// hevc_nvenc mencapai 400 fps melawan 318 fps milik CPU - tetapi
        /// memilihnya diam-diam MENGUBAH CODEC keluaran, dan HEVC persis yang
        /// tidak bisa diputar TV lama, pemutar DVD, dan perangkat USB yang
        /// jadi alasan aplikasi ini ada. Kecepatan bukan alasan yang cukup
        /// untuk menghasilkan berkas yang tidak bisa dibuka penggunanya.
        /// Pilihannya tetap ada di daftar untuk yang memang menginginkannya.
        /// </summary>
        public bool AutoSelectable => Codec == "h264";
    }

    public class EncoderScore
    {
        public EncoderCandidate Candidate;
        public bool Works;
        public double Fps;              // frame per detik yang terukur
        public string Error = "";

        public string Describe()
        {
            if (!Works) return Candidate.Label + ": tidak bisa dipakai";
            return Candidate.Label + ": "
                   + Fps.ToString("0", CultureInfo.InvariantCulture) + " fps";
        }
    }

    /// <summary>
    /// Memilih encoder tercepat dengan MENGUKURNYA, bukan menebak.
    ///
    /// <c>ffmpeg -encoders</c> hanya menyebut apa yang ikut dikompilasi, bukan
    /// apa yang bisa dijalankan mesin ini: h264_amf terdaftar di komputer tanpa
    /// driver AMD dan mati pada frame pertama. Bahkan ketika sebuah encoder
    /// jalan, urutan cepatnya tidak bisa ditebak dari mereknya - QuickSync di
    /// laptop lama sering kalah dari libx264 pada CPU 8 core, sementara NVENC
    /// hampir selalu menang telak. Satu-satunya jawaban yang jujur adalah
    /// menjalankan keempatnya sebentar dan membandingkan angkanya.
    ///
    /// Hasilnya disimpan bersama sidik jari perangkat keras, jadi pengukuran
    /// ini hanya terjadi sekali - dan terjadi lagi kalau kartu grafis, driver,
    /// atau versi FFmpeg-nya berubah.
    /// </summary>
    public static class EncoderBenchmark
    {
        /// <summary>
        /// Urutannya bukan urutan preferensi - itu ditentukan hasil pengukuran.
        /// Ini sekadar daftar yang patut dicoba.
        /// </summary>
        public static readonly EncoderCandidate[] Candidates =
        {
            new EncoderCandidate { Id = "h264_nvenc", Vendor = "NVIDIA",
                                   Codec = "h264", Label = "NVIDIA NVENC (H.264)" },
            new EncoderCandidate { Id = "hevc_nvenc", Vendor = "NVIDIA",
                                   Codec = "hevc", Label = "NVIDIA NVENC (H.265)" },
            new EncoderCandidate { Id = "h264_qsv", Vendor = "Intel",
                                   Codec = "h264", Label = "Intel QuickSync (H.264)" },
            new EncoderCandidate { Id = "h264_amf", Vendor = "AMD",
                                   Codec = "h264", Label = "AMD AMF (H.264)" },
            new EncoderCandidate { Id = "libx264", Vendor = "CPU",
                                   Codec = "h264", Label = "CPU (libx264)" },
        };

        // 720p, 150 frame. Cukup panjang supaya waktu encode mendominasi biaya
        // start proses (~100 ms yang sama untuk semua kandidat), cukup pendek
        // supaya seluruh rangkaian selesai dalam hitungan detik di laptop lama.
        private const string BenchSource =
            "testsrc2=size=1280x720:rate=30:duration=5";
        private const int BenchFrames = 150;
        private const double BenchTimeout = 60.0;

        /// <summary>Encoder yang benar-benar terdaftar di build FFmpeg ini.</summary>
        public static List<EncoderCandidate> Listed(FFmpegTools tools)
        {
            string listed = "";
            try
            {
                var res = Shell.RunCapture(
                    new[] { tools.FFmpeg, "-hide_banner", "-encoders" }, 20);
                listed = res.StdOut ?? "";
            }
            catch (Exception) { }

            var found = new List<EncoderCandidate>();
            foreach (var candidate in Candidates)
                if (listed.IndexOf(" " + candidate.Id + " ",
                                   StringComparison.Ordinal) >= 0)
                    found.Add(candidate);

            // libx264 selalu ikut sebagai patokan; kalau build-nya benar-benar
            // tidak punya, biarkan daftarnya apa adanya.
            return found;
        }

        /// <summary>
        /// Ukur setiap kandidat. `onStep(sudah, total, label)` dipanggil per
        /// kandidat; `cancel()` menghentikan di sela-sela.
        /// </summary>
        public static List<EncoderScore> Measure(
            FFmpegTools tools, TargetSpec target = null,
            Action<int, int, string> onStep = null, Func<bool> cancel = null)
        {
            target = target ?? new TargetSpec();
            var candidates = Listed(tools);
            var scores = new List<EncoderScore>();

            for (int i = 0; i < candidates.Count; i++)
            {
                if (cancel != null && cancel()) break;
                var candidate = candidates[i];
                if (onStep != null) onStep(i, candidates.Count, candidate.Label);
                scores.Add(MeasureOne(tools, candidate, target));
            }
            return scores;
        }

        private static EncoderScore MeasureOne(FFmpegTools tools,
                                               EncoderCandidate candidate,
                                               TargetSpec target)
        {
            var score = new EncoderScore { Candidate = candidate };

            var cmd = new List<string>
            {
                tools.FFmpeg, "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", BenchSource,
            };
            // Bendera yang SAMA PERSIS dengan yang dipakai saat bekerja. Mengukur
            // dengan setelan lain memberi angka yang benar untuk pertanyaan yang
            // salah - preset dan mode rate control sangat menentukan kecepatan.
            cmd.AddRange(Merger.EncoderFlagsFor(candidate.Id, target));
            cmd.AddRange(new[] { "-f", "null", "-" });

            var clock = Stopwatch.StartNew();
            CapturedResult res;
            try
            {
                res = Shell.RunCapture(cmd, BenchTimeout);
            }
            catch (Exception ex)
            {
                score.Error = ex.Message;
                return score;
            }
            clock.Stop();

            if (res.TimedOut)
            {
                score.Error = "terlalu lama (lebih dari "
                              + BenchTimeout.ToString("0", CultureInfo.InvariantCulture)
                              + " detik)";
                return score;
            }
            if (res.ExitCode != 0)
            {
                string[] lines = (res.StdErr ?? "").Trim()
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                score.Error = lines.Length > 0
                    ? lines[lines.Length - 1].Trim() : "gagal";
                return score;
            }

            double seconds = clock.Elapsed.TotalSeconds;
            if (seconds <= 0.001) seconds = 0.001;
            score.Works = true;
            score.Fps = BenchFrames / seconds;
            return score;
        }

        /// <summary>Kandidat tercepat yang benar-benar jalan, atau "" kalau tak ada.</summary>
        public static string Best(IEnumerable<EncoderScore> scores)
        {
            string best = "";
            double bestFps = 0;
            foreach (var score in scores)
            {
                if (!score.Works || !score.Candidate.AutoSelectable) continue;
                if (score.Fps <= bestFps) continue;
                bestFps = score.Fps;
                best = score.Candidate.Id;
            }
            // libx264 adalah nilai bawaan aplikasi, jadi memilihnya secara
            // eksplisit sama dengan tidak memilih apa-apa.
            return best == "libx264" ? "" : best;
        }

        public static string LabelOf(string encoderId)
        {
            if (string.IsNullOrEmpty(encoderId)) return "CPU (libx264)";
            foreach (var candidate in Candidates)
                if (candidate.Id == encoderId) return candidate.Label;
            return encoderId;
        }

        // ------------------------------------------------------------ cache --
        private const string KeyFingerprint = "encoder_bench_fingerprint";
        private const string KeyBest = "encoder_bench_best";
        private const string KeyDetail = "encoder_bench_detail";

        /// <summary>
        /// Hasil tersimpan kalau masih sah untuk perangkat keras sekarang.
        /// `hit` false berarti pengukuran perlu dijalankan.
        /// </summary>
        public static bool TryCached(AppSettings settings, FFmpegTools tools,
                                     out string best, out string detail)
        {
            best = "";
            detail = "";
            if (settings == null || tools == null) return false;
            string current = Hardware.Fingerprint(tools);
            if (settings[KeyFingerprint] != current) return false;
            best = settings[KeyBest];
            detail = settings[KeyDetail];
            return true;
        }

        public static void StoreCache(AppSettings settings, FFmpegTools tools,
                                      IList<EncoderScore> scores)
        {
            if (settings == null || tools == null) return;
            var sb = new StringBuilder();
            foreach (var score in scores)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(score.Describe());
            }
            settings.Set(KeyFingerprint, Hardware.Fingerprint(tools));
            settings.Set(KeyBest, Best(scores));
            settings.Set(KeyDetail, sb.ToString());
            settings.Save();
        }

        public static void ClearCache(AppSettings settings)
        {
            if (settings == null) return;
            settings.Set(KeyFingerprint, "");
            settings.Set(KeyBest, "");
            settings.Set(KeyDetail, "");
            settings.Save();
        }
    }
}
