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
            // H.265 BUKAN milik NVIDIA saja. Intel punya hevc_qsv, AMD punya
            // hevc_amf, dan libx265 mengerjakannya di CPU tanpa perangkat
            // keras apa pun. Daftar lama hanya memuat hevc_nvenc, yang membuat
            // seolah-olah HEVC cuma bisa di kartu NVIDIA.
            new EncoderCandidate { Id = "hevc_qsv", Vendor = "Intel",
                                   Codec = "hevc", Label = "Intel QuickSync (H.265)" },
            new EncoderCandidate { Id = "h264_amf", Vendor = "AMD",
                                   Codec = "h264", Label = "AMD AMF (H.264)" },
            new EncoderCandidate { Id = "hevc_amf", Vendor = "AMD",
                                   Codec = "hevc", Label = "AMD AMF (H.265)" },
            new EncoderCandidate { Id = "libx264", Vendor = "CPU",
                                   Codec = "h264", Label = "CPU (libx264)" },
            new EncoderCandidate { Id = "libx265", Vendor = "CPU",
                                   Codec = "hevc", Label = "CPU (libx265)" },
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

        /// <summary>
        /// Kandidat menurut id-nya, atau null. Ada supaya pemanggil tidak
        /// mengindeks Candidates dengan angka: menyisipkan satu kandidat baru
        /// di tengah daftar pernah membuat tiga tes menunjuk encoder yang
        /// sama sekali berbeda tanpa satu pun galat kompilasi.
        /// </summary>
        public static EncoderCandidate ById(string id)
        {
            foreach (var candidate in Candidates)
                if (candidate.Id == id) return candidate;
            return null;
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
        /// Ubah hasil pengukuran jadi satu baris yang bisa dibaca kembali:
        /// <c>h264_nvenc=297.4;h264_amf=x;libx264=316.0</c>.
        ///
        /// Angkanya disimpan, bukan cuma kalimat siap tampil, supaya daftar
        /// pilihan encoder bisa menyebut kecepatan tiap pilihan DAN mematikan
        /// pilihan yang sudah terbukti tidak jalan. Menyimpannya sebagai
        /// kalimat berarti angka itu harus diurai balik dari teks Indonesia -
        /// yang akan patah begitu kalimatnya diubah sedikit saja.
        /// </summary>
        public static string Serialize(IList<EncoderScore> scores)
        {
            var sb = new StringBuilder();
            foreach (var score in scores)
            {
                if (score == null || score.Candidate == null) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(score.Candidate.Id).Append('=');
                sb.Append(score.Works
                    ? score.Fps.ToString("0.0", CultureInfo.InvariantCulture)
                    : "x");
            }
            return sb.ToString();
        }

        /// <summary>Kebalikan Serialize. Ruas yang tidak dikenali dilewati.</summary>
        public static List<EncoderScore> Deserialize(string text)
        {
            var scores = new List<EncoderScore>();
            if (string.IsNullOrWhiteSpace(text)) return scores;

            foreach (string part in text.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string id = part.Substring(0, eq).Trim();
                string value = part.Substring(eq + 1).Trim();

                EncoderCandidate candidate = null;
                foreach (var c in Candidates)
                    if (c.Id == id) { candidate = c; break; }
                if (candidate == null) continue;

                double fps;
                bool works = double.TryParse(value, NumberStyles.Float,
                                             CultureInfo.InvariantCulture, out fps);
                scores.Add(new EncoderScore
                {
                    Candidate = candidate,
                    Works = works,
                    Fps = works ? fps : 0,
                });
            }
            return scores;
        }

        /// <summary>
        /// Hasil tersimpan kalau masih sah untuk perangkat keras sekarang.
        /// Kembalian false berarti pengukuran perlu dijalankan.
        /// </summary>
        public static bool TryCached(AppSettings settings, FFmpegTools tools,
                                     out string best, out List<EncoderScore> scores)
        {
            best = "";
            scores = new List<EncoderScore>();
            if (settings == null || tools == null) return false;
            string current = Hardware.Fingerprint(tools);
            if (settings[KeyFingerprint] != current) return false;

            scores = Deserialize(settings[KeyDetail]);
            // Sidik jari cocok tetapi angkanya tidak terbaca sama sekali:
            // itu cache dari versi lama, yang menyimpan kalimat siap tampil
            // ("NVENC (H.264): 305 fps; ...") dan bukan pasangan id=fps.
            // Menerimanya membuat daftar pilihan kehilangan seluruh angka
            // DAN tidak pernah mengukur ulang - sidik jarinya kan cocok.
            // Diperlakukan sebagai belum pernah diukur.
            if (scores.Count == 0) return false;

            best = settings[KeyBest];
            return true;
        }

        public static void StoreCache(AppSettings settings, FFmpegTools tools,
                                      IList<EncoderScore> scores, bool complete)
        {
            // `complete` false berarti pengukurannya berhenti di tengah.
            // Cache-nya diikat ke sidik jari perangkat keras, jadi pemenang
            // yang disimpan dari daftar separuh jadi akan bertahan sampai GPU
            // atau versi FFmpeg berubah - bisa berbulan-bulan - tanpa satu pun
            // gejala yang terlihat pengguna. Lebih baik tidak menyimpan apa-apa
            // dan mengukur ulang nanti.
            if (settings == null || tools == null || !complete) return;
            settings.Set(KeyFingerprint, Hardware.Fingerprint(tools));
            settings.Set(KeyBest, Best(scores));
            settings.Set(KeyDetail, Serialize(scores));
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
