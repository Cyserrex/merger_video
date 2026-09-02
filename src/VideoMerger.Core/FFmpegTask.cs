using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace VideoMerger.Core
{
    /// <summary>Pekerjaan tidak bisa dilanjutkan; pesannya untuk pengguna.</summary>
    public class MergeException : Exception
    {
        public MergeException(string message) : base(message) { }
    }

    /// <summary>Dilempar ketika pengguna membatalkan.</summary>
    public class CancelledException : Exception
    {
        public CancelledException() : base("Dibatalkan.") { }
    }

    /// <summary>
    /// Menjalankan satu proses ffmpeg: kemajuan, pembatalan, dan deteksi gagal.
    ///
    /// Bagian yang mahal dipelajari bukan cara meluncurkannya, melainkan cara
    /// mengetahui ffmpeg benar-benar gagal - karena kode keluarnya sering 0
    /// padahal hasilnya salah:
    ///
    ///   * masukan yang tidak bisa dibuka di tengah jalan hanya dicatat di log,
    ///     lalu ffmpeg merampungkan apa yang sudah ada dan keluar dengan 0
    ///   * menekan 'q' untuk membatalkan juga menghasilkan kode keluar 0
    ///
    /// Jadi kode keluar cuma satu dari tiga sinyal di sini, bersama bendera
    /// pembatalan dan pemindaian ekor stderr.
    /// </summary>
    public abstract class FFmpegTask
    {
        /// <summary>
        /// Baris yang dicatat ffmpeg saat menyerah pada sebuah masukan
        /// tetapi tetap keluar dengan sukses.
        /// </summary>
        public static readonly string[] FatalLogMarkers =
        {
            "impossible to open",
            "error opening input",
            "error during demuxing",
        };

        public FFmpegTools Tools { get; private set; }

        private readonly object _procLock = new object();
        private Process _proc;
        private volatile bool _cancelled;

        public Action<Progress> OnProgress;
        public Action<string> OnLog;

        protected FFmpegTask(FFmpegTools tools, Action<Progress> onProgress = null,
                             Action<string> onLog = null)
        {
            Tools = tools;
            OnProgress = onProgress;
            OnLog = onLog;
        }

        // -- kendali ---------------------------------------------------------
        public bool IsCancelled => _cancelled;

        /// <summary>Minta ffmpeg berhenti. Aman dipanggil dari thread UI.</summary>
        public void Cancel()
        {
            _cancelled = true;
            Process proc;
            lock (_procLock) proc = _proc;
            if (proc == null) return;
            try { if (proc.HasExited) return; } catch (InvalidOperationException) { return; }

            // 'q' membuat ffmpeg mengosongkan buffer dan menutup container
            // dengan rapi; kalau macet, watchdog di bawah yang menuntaskan.
            try
            {
                proc.StandardInput.Write("q");
                proc.StandardInput.Flush();
            }
            catch (Exception) { }

            // Pembaca hanya menyadari bendera batal saat blok kemajuan
            // berikutnya tiba. Kalau ffmpeg macet, blok itu tidak akan pernah
            // datang dan pembacanya menunggu selamanya - jadi eskalasinya
            // berjalan di pewaktu sendiri, bukan di belakang pembaca itu.
            var watchdog = new Thread(() =>
            {
                try
                {
                    if (!proc.WaitForExit(10000)) Shell.TerminateTree(proc);
                }
                catch (Exception) { }
            });
            watchdog.IsBackground = true;
            watchdog.Start();
        }

        protected void CheckCancel()
        {
            if (_cancelled) throw new CancelledException();
        }

        // -- pelaporan -------------------------------------------------------
        protected void Log(string text)
        {
            if (OnLog != null && !string.IsNullOrEmpty(text))
                OnLog(text.TrimEnd());
        }

        protected void Emit(Progress p)
        {
            if (OnProgress != null) OnProgress(p);
        }

        // -- pengurai kemajuan ----------------------------------------------
        /// <summary>
        /// Detik yang sudah diproses, dari satu blok <c>-progress</c>.
        ///
        /// <c>out_time_us</c> adalah acuannya. <c>out_time_ms</c> adalah salah
        /// nama yang sudah lama ada di ffmpeg - isinya juga mikrodetik - jadi
        /// dibagi 1e6, bukan 1e3.
        /// </summary>
        public static double ParseProgressTime(IDictionary<string, string> fields)
        {
            foreach (string key in new[] { "out_time_us", "out_time_ms" })
            {
                string raw;
                if (!fields.TryGetValue(key, out raw)) continue;
                if (string.IsNullOrEmpty(raw) || raw == "N/A"
                    || raw == "-9223372036854775807") continue;
                long micro;
                if (long.TryParse(raw, NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out micro) && micro >= 0)
                    return micro / 1e6;
            }

            string text;
            if (fields.TryGetValue("out_time", out text)
                && !string.IsNullOrEmpty(text) && text != "N/A")
            {
                bool negative = text.StartsWith("-", StringComparison.Ordinal);
                string[] bits = text.TrimStart('-').Split(':');
                double hh, mm, ss;
                if (bits.Length == 3
                    && double.TryParse(bits[0], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out hh)
                    && double.TryParse(bits[1], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out mm)
                    && double.TryParse(bits[2], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out ss))
                {
                    double value = hh * 3600 + mm * 60 + ss;
                    return negative ? 0.0 : value;
                }
            }
            return 0.0;
        }

        // -- jalannya --------------------------------------------------------
        protected void RunFFmpeg(IList<string> cmd, double duration, double baseFraction,
                                 double span, Stage stage, string label,
                                 int currentIndex = 0, int totalItems = 0,
                                 string workingDirectory = null)
        {
            CheckCancel();
            Log("$ " + Quote(cmd));

            Process proc = Shell.StartStreaming(cmd, workingDirectory);
            lock (_procLock) _proc = proc;

            var stderrTail = new List<string>();
            var tailLock = new object();

            var reader = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = proc.StandardError.ReadLine()) != null)
                    {
                        line = line.TrimEnd();
                        if (line.Length == 0) continue;
                        lock (tailLock)
                        {
                            stderrTail.Add(line);
                            if (stderrTail.Count > 40)
                                stderrTail.RemoveRange(0, stderrTail.Count - 40);
                        }
                        Log(line);
                    }
                }
                catch (Exception) { }
            });
            reader.IsBackground = true;
            reader.Start();

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var clock = Stopwatch.StartNew();
            double lastEmit = -1;

            try
            {
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (_cancelled) break;
                    line = line.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq);
                    string value = line.Substring(eq + 1);
                    fields[key] = value;
                    if (key != "progress") continue;

                    double seconds = ParseProgressTime(fields);
                    double fracLocal = duration > 0
                        ? Math.Min(1.0, seconds / duration) : 0.0;

                    double speed;
                    string speedText;
                    if (!fields.TryGetValue("speed", out speedText)
                        || !double.TryParse(speedText.TrimEnd('x'), NumberStyles.Float,
                                            CultureInfo.InvariantCulture, out speed))
                        speed = 0.0;

                    long outSize;
                    string sizeText;
                    if (!fields.TryGetValue("total_size", out sizeText)
                        || !long.TryParse(sizeText, NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out outSize))
                        outSize = 0;

                    double remaining = speed > 0.01 ? (duration - seconds) / speed : 0.0;
                    double now = clock.Elapsed.TotalSeconds;

                    // Pembatas laju: ffmpeg mengirim blok ~2x per detik per
                    // berkas, dan antrean UI tidak butuh lebih dari itu.
                    if (now - lastEmit >= 0.20 || value == "end")
                    {
                        lastEmit = now;
                        if (value == "end")
                        {
                            // Semuanya sudah tertulis, tetapi ffmpeg belum
                            // selesai: +faststart kini menulis ulang seluruh
                            // berkas untuk memindahkan indeks ke depan, yang
                            // pada keluaran 30 GB memakan waktu menit. Tanpa
                            // ini bilahnya diam di 100% dan aplikasi tampak
                            // menggantung.
                            Emit(new Progress
                            {
                                Stage = Stage.Finalizing,
                                Fraction = baseFraction + span,
                                Message = "Menyusun indeks video (bisa lama untuk file besar)...",
                                CurrentIndex = currentIndex,
                                TotalItems = totalItems,
                            });
                            fields.Clear();
                            continue;
                        }
                        Emit(new Progress
                        {
                            Stage = stage,
                            Fraction = baseFraction + span * fracLocal,
                            Message = label + " - " + Humanize.Duration(seconds)
                                      + " / " + Humanize.Duration(duration),
                            CurrentIndex = currentIndex,
                            TotalItems = totalItems,
                            SecondsDone = seconds,
                            SecondsTotal = duration,
                            Speed = speed,
                            EtaSeconds = remaining,
                            OutputSize = outSize,
                        });
                    }
                    fields.Clear();
                }
            }
            catch (Exception) { /* pipa tertutup saat dibatalkan */ }

            if (_cancelled)
            {
                // Cancel() sudah mengirim 'q'. Beri ffmpeg beberapa detik
                // untuk menutup container dengan benar sebelum pohon
                // prosesnya dibunuh, supaya tidak ada ffmpeg yatim yang masih
                // menulis ke folder sementara yang sebentar lagi dihapus.
                // Perhatikan ffmpeg keluar dengan 0 setelah 'q' - itulah
                // sebabnya bendera batal, bukan kode keluar, yang menentukan.
                try { if (!proc.WaitForExit(8000)) Shell.TerminateTree(proc); }
                catch (Exception) { }
                reader.Join(2000);
                lock (_procLock) _proc = null;
                proc.Dispose();
                throw new CancelledException();
            }

            proc.WaitForExit();
            reader.Join(5000);
            int code = proc.ExitCode;
            lock (_procLock) _proc = null;
            proc.Dispose();

            string[] tail;
            lock (tailLock) tail = stderrTail.ToArray();

            // Masukan yang tidak bisa dibuka TIDAK membuat ffmpeg gagal. Ia
            // mencatat satu baris, berhenti membaca daftar di situ,
            // merampungkan apa yang ada, lalu keluar dengan 0. Menangkap baris
            // log itu satu-satunya cara membedakannya dari sukses sungguhan.
            foreach (string entry in tail)
            {
                string lowered = entry.ToLowerInvariant();
                foreach (string marker in FatalLogMarkers)
                {
                    if (lowered.Contains(marker))
                        throw new MergeException(
                            "FFmpeg gagal membuka salah satu video di tengah proses, "
                            + "sehingga hasilnya tidak lengkap."
                            + Environment.NewLine + Environment.NewLine + entry);
                }
            }

            if (code != 0)
            {
                if (_cancelled) throw new CancelledException();
                string detail = tail.Length > 0
                    ? string.Join(Environment.NewLine,
                                  Slice(tail, Math.Max(0, tail.Length - 8)))
                    : "(tidak ada pesan)";
                throw new MergeException(
                    "FFmpeg gagal (kode " + code + ")."
                    + Environment.NewLine + Environment.NewLine + detail);
            }
        }

        private static string[] Slice(string[] source, int start)
        {
            var result = new string[source.Length - start];
            Array.Copy(source, start, result, 0, result.Length);
            return result;
        }

        private static string Quote(IList<string> cmd)
        {
            var sb = new StringBuilder();
            foreach (string part in cmd)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(part.IndexOf(' ') >= 0 ? "\"" + part + "\"" : part);
            }
            return sb.ToString();
        }
    }
}
