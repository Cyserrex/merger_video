using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using VideoMerger.Core;

namespace VideoMerger.Tests
{
    /// <summary>
    /// Perkakas bersama untuk rangkaian tes.
    ///
    /// Setiap kasus di sini pertama kali ditemui sebagai kerusakan DIAM-DIAM:
    /// ffmpeg keluar dengan 0, tidak mencetak peringatan, dan menghasilkan
    /// berkas yang salah. Karena itu tesnya mengukur berkas hasilnya, bukan
    /// mempercayai kode keluar.
    /// </summary>
    public static class Harness
    {
        public static FFmpegTools Tools;
        public static readonly List<string> Failures = new List<string>();
        public static int Passes;

        public static void Check(string name, bool condition, string detail = "")
        {
            if (condition)
            {
                Passes++;
                Console.WriteLine("  LULUS  " + name);
            }
            else
            {
                Failures.Add(name + ": " + detail);
                Console.WriteLine("  GAGAL  " + name + "  " + detail);
            }
        }

        /// <summary>Bikin berkas media dengan ffmpeg. Melempar kalau gagal.</summary>
        public static string Make(string path, params string[] args)
        {
            var cmd = new List<string>
            {
                Tools.FFmpeg, "-hide_banner", "-loglevel", "error", "-y",
            };
            cmd.AddRange(args);
            cmd.Add(path);
            var res = Shell.RunCapture(cmd, 120);
            if (res.ExitCode != 0)
                throw new Exception("Gagal membuat " + Path.GetFileName(path)
                                    + ": " + res.StdErr);
            return path;
        }

        public static VideoFile Probed(string path)
        {
            return Prober.ProbeFile(Tools, new VideoFile
            {
                Path = path,
                Size = new FileInfo(path).Length,
            });
        }

        public static double RealDuration(string path)
        {
            var res = Shell.RunCapture(new[]
            {
                Tools.FFprobe, "-v", "error", "-show_entries", "format=duration",
                "-of", "default=nk=1:nw=1", path,
            }, 60);
            double value;
            return double.TryParse((res.StdOut ?? "").Trim(), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        /// <summary>Sambung berkas dengan concat demuxer mentah; kembalikan durasinya.</summary>
        public static double ConcatTo(IList<string> files, string outPath, string workDir)
        {
            string listing = Merger.WriteConcatList(files,
                Path.Combine(workDir, "l.txt"));
            var res = Shell.RunCapture(new[]
            {
                Tools.FFmpeg, "-hide_banner", "-loglevel", "error", "-y",
                "-f", "concat", "-safe", "0", "-i", listing, "-c", "copy", outPath,
            }, 120);
            if (res.ExitCode != 0)
                throw new Exception("concat gagal: " + res.StdErr);
            return RealDuration(outPath);
        }

        /// <summary>Baris yang dikeluarkan ffmpeg saat membaca ulang berkas. Idealnya nihil.</summary>
        public static List<string> DecodeWarnings(string path)
        {
            var res = Shell.RunCapture(new[]
            {
                Tools.FFmpeg, "-v", "error", "-i", path, "-f", "null", "-",
            }, 120);
            var lines = new List<string>();
            foreach (string line in (res.StdErr ?? "").Split('\n'))
                if (line.Trim().Length > 0) lines.Add(line.Trim());
            return lines;
        }

        public static int SubtitleStreamCount(string path)
        {
            var res = Shell.RunCapture(new[]
            {
                Tools.FFprobe, "-v", "error", "-select_streams", "s",
                "-show_entries", "stream=index", "-of", "csv=p=0", path,
            }, 60);
            int count = 0;
            foreach (string line in (res.StdOut ?? "").Split('\n'))
                if (line.Trim().Length > 0) count++;
            return count;
        }

        /// <summary>
        /// Luminansi puncak pada satu bagian gambar. Klip hitam murni ~16.
        ///
        /// Inilah cara pembakaran subtitle DIBUKTIKAN: sumbernya hitam polos,
        /// jadi piksel terang mana pun di seperempat bawah hanya bisa berasal
        /// dari teks yang benar-benar digambar ffmpeg ke dalam gambar.
        /// Memeriksa kode keluar akan lulus sama gembiranya pada berkas yang
        /// tidak digambari apa pun.
        ///
        /// file=- bukan hiasan: metadata=print menulis di level INFO, jadi
        /// dengan -v error yang dibutuhkan agar senyap, ia tidak mencetak apa
        /// pun dan setiap pengukuran diam-diam kembali 0 - yang terbaca persis
        /// seperti "tidak ada teks yang terbakar", apa pun isi berkasnya.
        /// </summary>
        public static double Brightest(string path,
                                       string crop = "iw:ih/4:0:ih*3/4")
        {
            var res = Shell.RunCapture(new[]
            {
                Tools.FFmpeg, "-v", "error", "-i", path,
                "-vf", "crop=" + crop + ",signalstats,"
                       + "metadata=print:key=lavfi.signalstats.YMAX:file=-",
                "-f", "null", "-",
            }, 120);
            double peak = 0;
            foreach (string line in ((res.StdOut ?? "") + (res.StdErr ?? "")).Split('\n'))
            {
                int cut = line.IndexOf("YMAX=", StringComparison.Ordinal);
                if (cut < 0) continue;
                double value;
                if (double.TryParse(line.Substring(cut + 5).Trim(),
                                    NumberStyles.Float, CultureInfo.InvariantCulture,
                                    out value))
                    peak = Math.Max(peak, value);
            }
            return peak;
        }

        /// <summary>Seberapa banyak piksel yang menyala, dijumlah antar frame.</summary>
        public static long LitArea(string path)
        {
            var res = Shell.RunCapture(new[]
            {
                Tools.FFmpeg, "-v", "error", "-i", path,
                "-vf", "crop=iw:ih/3:0:ih*2/3,signalstats,"
                       + "metadata=print:key=lavfi.signalstats.YAVG:file=-",
                "-f", "null", "-",
            }, 120);
            double total = 0;
            foreach (string line in ((res.StdOut ?? "") + (res.StdErr ?? "")).Split('\n'))
            {
                int cut = line.IndexOf("YAVG=", StringComparison.Ordinal);
                if (cut < 0) continue;
                double value;
                if (double.TryParse(line.Substring(cut + 5).Trim(),
                                    NumberStyles.Float, CultureInfo.InvariantCulture,
                                    out value))
                    total += value;
            }
            return (long)(total * 100);
        }

        public static void Silent(Progress p) { }
        public static void Quiet(string line) { }
    }
}
