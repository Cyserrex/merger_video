using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using VideoMerger.Core;

namespace VideoMerger.App
{
    /// <summary>
    /// Antarmuka baris perintah.
    ///
    /// Ada supaya mesinnya bisa dijalankan tanpa layar - dari .bat, dari Task
    /// Scheduler - dan supaya exe yang sama melayani keduanya.
    /// </summary>
    public static class Cli
    {
        private class Options
        {
            public string Input = "";
            public string Output = "";
            public bool Recursive;
            public string Sort = "name";
            public bool Desc;
            public string Mode = "auto";
            public int Crf = 23;
            public string Preset = "veryfast";
            public string Encoder = "";
            public bool Strict;
            public bool List;
            public bool NoFaststart;

            public bool Hardsub;
            public string SubFile = "";
            public int SubTrack = -1;
            public string SubLang = "";
            public bool SubStyle;
            public string SubFont = "Arial";
            public int SubSize = 24;
            public string Suffix = " - hardsub";
            public string OutDir = "";
            public string Container = ".mp4";
        }

        public static int Run(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> next = () => (i + 1 < args.Length) ? args[++i] : "";
                switch (a)
                {
                    case "-h": case "--help": PrintHelp(); return 0;
                    case "--version":
                        Console.WriteLine(AppInfo.Name + " " + AppInfo.Version);
                        return 0;
                    case "-i": case "--input": o.Input = next(); break;
                    case "-o": case "--output": o.Output = next(); break;
                    case "-r": case "--recursive": o.Recursive = true; break;
                    case "-s": case "--sort": o.Sort = next(); break;
                    case "--desc": o.Desc = true; break;
                    case "-m": case "--mode": o.Mode = next(); break;
                    case "--crf": int.TryParse(next(), out o.Crf); break;
                    case "--preset": o.Preset = next(); break;
                    case "--encoder": o.Encoder = next(); break;
                    case "--strict": o.Strict = true; break;
                    case "--list": o.List = true; break;
                    case "--no-faststart": o.NoFaststart = true; break;
                    case "--hardsub": o.Hardsub = true; break;
                    case "--sub-file": o.SubFile = next(); break;
                    case "--sub-track": int.TryParse(next(), out o.SubTrack); break;
                    case "--sub-lang": o.SubLang = next(); break;
                    case "--sub-style": o.SubStyle = true; break;
                    case "--sub-font": o.SubFont = next(); break;
                    case "--sub-size": int.TryParse(next(), out o.SubSize); break;
                    case "--suffix": o.Suffix = next(); break;
                    case "--out-dir": o.OutDir = next(); break;
                    case "--container": o.Container = next(); break;
                    default:
                        Console.Error.WriteLine("Argumen tidak dikenal: " + a);
                        return 2;
                }
            }

            if (string.IsNullOrEmpty(o.Input)) { PrintHelp(); return 2; }

            string source = Path.GetFullPath(o.Input);
            // Hardsub adalah satu-satunya mode yang wajar menunjuk satu berkas
            // ("episode ini softsub-nya tidak tampil"), jadi ia menerima keduanya.
            if (!Directory.Exists(source)
                && !(o.Hardsub && File.Exists(source)))
            {
                Console.Error.WriteLine("Folder tidak ditemukan: " + source);
                return 2;
            }

            var tools = FFmpegLocator.Locate();
            if (tools == null)
            {
                Console.Error.WriteLine(
                    "FFmpeg tidak ditemukan. Pasang FFmpeg atau letakkan "
                    + "ffmpeg.exe di folder aplikasi.");
                return 3;
            }
            Console.WriteLine("FFmpeg " + tools.Version + " (" + tools.Source + ")");

            return o.Hardsub ? RunHardsub(o, tools, source) : RunMerge(o, tools, source);
        }

        // ---------------------------------------------------- penggabungan --
        private static int RunMerge(Options o, FFmpegTools tools, string folder)
        {
            var found = Scanner.ScanFolder(folder, o.Recursive);
            if (!string.IsNullOrEmpty(o.Output))
            {
                // Menyimpan ke dalam folder yang digabung itu wajar, jadi hasil
                // proses sebelumnya tidak boleh ikut tersapu jadi masukan.
                string target = Path.GetFullPath(o.Output);
                found.RemoveAll(f => string.Equals(Path.GetFullPath(f.Path), target,
                                                   StringComparison.OrdinalIgnoreCase));
            }
            if (found.Count == 0)
            {
                Console.Error.WriteLine("Tidak ada file video di folder tersebut.");
                return 4;
            }
            Console.WriteLine("Memeriksa " + found.Count + " file...");
            Prober.ProbeMany(tools, found);

            var valid = new List<VideoFile>();
            var bad = new List<VideoFile>();
            foreach (var f in found) (f.Valid ? valid : bad).Add(f);
            valid = FileSorter.Sort(valid, ParseSort(o.Sort), o.Desc);

            double total = 0; long bytes = 0;
            foreach (var f in valid) { total += f.Duration; bytes += f.Size; }
            Console.WriteLine();
            Console.WriteLine(valid.Count + " video valid, total durasi "
                              + Humanize.Duration(total) + ", ukuran "
                              + Humanize.Size(bytes));
            for (int i = 0; i < valid.Count; i++)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,3}. {1,-44} {2} {3,10} {4}", i + 1,
                    Trim(valid[i].Name, 44), Humanize.Duration(valid[i].Duration),
                    valid[i].Resolution, valid[i].VCodec));
            foreach (var f in bad)
                Console.WriteLine("  SKIP " + f.Name + ": " + f.Error);

            if (valid.Count >= 2)
            {
                List<string> reasons;
                bool ok = Prober.CanStreamCopy(valid, out reasons);
                Console.WriteLine();
                Console.WriteLine("Bisa digabung tanpa encode ulang: "
                                  + (ok ? "YA" : "TIDAK"));
                for (int i = 0; i < Math.Min(5, reasons.Count); i++)
                    Console.WriteLine("  - " + reasons[i]);
            }

            if (bad.Count > 0 && o.Strict)
            {
                // Proses tak berpenjaga (.bat, Task Scheduler) tidak boleh
                // diam-diam menggabung 97 dari 100 rekaman karena tiga di
                // antaranya masih dikunci DVR.
                Console.Error.WriteLine();
                Console.Error.WriteLine("GAGAL (--strict): " + bad.Count
                                        + " file dilewati.");
                return 5;
            }
            if (o.List) return 0;
            if (string.IsNullOrEmpty(o.Output))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("--output wajib diisi (atau pakai --list).");
                return 2;
            }
            if (valid.Count < 2)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Butuh minimal 2 video valid.");
                return 4;
            }

            var job = new MergeJob
            {
                Files = valid,
                OutputPath = Path.GetFullPath(o.Output),
                Mode = ParseMode(o.Mode),
                Target = new TargetSpec { Crf = o.Crf, Preset = o.Preset },
                HwaccelEncoder = o.Encoder,
                Faststart = !o.NoFaststart,
            };

            var reporter = new Reporter();
            var merger = new Merger(tools, job, reporter.Report, line => { });
            var clock = Stopwatch.StartNew();
            string outPath;
            try
            {
                outPath = merger.Run();
            }
            catch (CancelledException)
            {
                Console.Error.WriteLine("\nDibatalkan.");
                return 130;
            }
            catch (MergeException ex)
            {
                Console.Error.WriteLine("\n\nGAGAL: " + ex.Message);
                return 1;
            }
            catch (IOException ex)
            {
                // Tujuan tidak bisa ditulisi, share terputus, disk penuh:
                // laporkan apa adanya daripada menumpahkan stack trace.
                Console.Error.WriteLine("\n\nGAGAL: " + ex.Message);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Selesai dalam "
                              + Humanize.Duration(clock.Elapsed.TotalSeconds));
            Console.WriteLine("Hasil: " + outPath + " ("
                              + Humanize.Size(new FileInfo(outPath).Length) + ")");
            return 0;
        }

        // --------------------------------------------------------- hardsub --
        private static int RunHardsub(Options o, FFmpegTools tools, string source)
        {
            List<VideoFile> videos;
            if (File.Exists(source))
            {
                videos = new List<VideoFile>
                {
                    Prober.ProbeFile(tools, new VideoFile
                    {
                        Path = source,
                        Size = new FileInfo(source).Length,
                    }),
                };
            }
            else
            {
                var found = Scanner.ScanFolder(source, o.Recursive);
                if (found.Count == 0)
                {
                    Console.Error.WriteLine("Tidak ada file video di folder tersebut.");
                    return 4;
                }
                Console.WriteLine("Memeriksa " + found.Count + " file...");
                Prober.ProbeMany(tools, found);
                var ok = new List<VideoFile>();
                foreach (var f in found) if (f.Valid) ok.Add(f);
                videos = FileSorter.Sort(ok, ParseSort(o.Sort), o.Desc);
            }

            var usable = new List<VideoFile>();
            foreach (var v in videos) if (v.Valid) usable.Add(v);
            if (usable.Count == 0)
            {
                Console.Error.WriteLine("Tidak ada video yang bisa dibaca.");
                return 4;
            }

            var items = Hardsubber.CollectSources(tools, usable);
            var problems = new List<string>();
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(o.SubFile))
                {
                    item.ExternalPath = Path.GetFullPath(o.SubFile);
                    item.Track = null;
                    item.Error = "";
                    item.Selected = true;
                }
                else
                {
                    string why = PickTrack(item, o);
                    if (!string.IsNullOrEmpty(why))
                    {
                        item.Selected = false;
                        item.Error = why;
                    }
                }
                if (!item.HasSource)
                {
                    // CollectSources sudah melepas centang video yang memang
                    // tidak punya subtitle, jadi memeriksa `Selected` di sini
                    // justru menyembunyikan berkas yang paling perlu
                    // diberitahukan - mereka hilang begitu saja dari laporan.
                    item.Selected = false;
                    problems.Add(item.Video.Name + ": "
                                 + (string.IsNullOrEmpty(item.Error)
                                    ? "tanpa subtitle" : item.Error));
                }
            }

            var chosen = new List<HardsubItem>();
            foreach (var item in items) if (item.Selected) chosen.Add(item);

            Console.WriteLine();
            Console.WriteLine(chosen.Count + " video akan dibakar subtitle:");
            for (int i = 0; i < chosen.Count; i++)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,3}. {1,-40} <- {2}", i + 1,
                    Trim(chosen[i].Video.Name, 40), chosen[i].SourceLabel));
            foreach (string line in problems) Console.WriteLine("  SKIP " + line);

            if (problems.Count > 0 && o.Strict)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("GAGAL (--strict): " + problems.Count
                                        + " file dilewati.");
                return 5;
            }
            if (o.List) return 0;
            if (chosen.Count == 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Tidak ada video yang punya subtitle.");
                return 4;
            }

            var job = new HardsubJob
            {
                Items = chosen,
                OutputDir = string.IsNullOrEmpty(o.OutDir)
                    ? "" : Path.GetFullPath(o.OutDir),
                Suffix = o.Suffix,
                Container = o.Container.StartsWith(".", StringComparison.Ordinal)
                    ? o.Container : "." + o.Container,
                Style = new SubtitleStyle
                {
                    Enabled = o.SubStyle,
                    Font = o.SubFont,
                    Size = o.SubSize,
                },
                Target = new TargetSpec { Crf = o.Crf, Preset = o.Preset },
                HwaccelEncoder = o.Encoder,
                Faststart = !o.NoFaststart,
            };

            var reporter = new Reporter();
            var task = new Hardsubber(tools, job, reporter.Report, line => { });
            var clock = Stopwatch.StartNew();
            HardsubResult result;
            try
            {
                result = task.Run();
            }
            catch (CancelledException)
            {
                Console.Error.WriteLine("\nDibatalkan.");
                return 130;
            }
            catch (MergeException ex)
            {
                Console.Error.WriteLine("\n\nGAGAL: " + ex.Message);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Selesai dalam "
                              + Humanize.Duration(clock.Elapsed.TotalSeconds));
            foreach (string path in result.Done)
                Console.WriteLine("  " + path + " ("
                                  + Humanize.Size(new FileInfo(path).Length) + ")");
            foreach (var fail in result.Failed)
                Console.Error.WriteLine("  GAGAL " + fail.Key + ": "
                                        + FirstLine(fail.Value));
            // Satu angkatan yang sebagian gagal bukan sukses, walaupun sisanya
            // tertulis - tugas terjadwal harus bisa membedakannya.
            return result.Failed.Count > 0 ? 1 : 0;
        }

        /// <summary>Terapkan --sub-track / --sub-lang, atau pertahankan pilihan otomatis.</summary>
        private static string PickTrack(HardsubItem item, Options o)
        {
            if (o.SubTrack > 0)
            {
                int wanted = o.SubTrack - 1;
                foreach (var t in item.Tracks)
                    if (t.StreamIndex == wanted && t.Burnable)
                    {
                        item.Track = t; item.ExternalPath = "";
                        return "";
                    }
                return "tidak ada trek subtitle #" + o.SubTrack;
            }
            if (!string.IsNullOrEmpty(o.SubLang))
            {
                foreach (var t in item.Tracks)
                    if (t.Burnable && string.Equals(t.Language, o.SubLang.Trim(),
                                                    StringComparison.OrdinalIgnoreCase))
                    {
                        item.Track = t; item.ExternalPath = "";
                        return "";
                    }
                return "tidak ada trek subtitle berbahasa '" + o.SubLang + "'";
            }
            return "";
        }

        // ------------------------------------------------------- pembantu --
        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int cut = text.IndexOfAny(new[] { '\r', '\n' });
            return cut < 0 ? text : text.Substring(0, cut);
        }

        private static string Trim(string text, int width)
            => text.Length <= width ? text : text.Substring(0, width);

        private static SortBy ParseSort(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "name_plain": return SortBy.NamePlain;
                case "modified": return SortBy.Modified;
                case "created": return SortBy.Created;
                case "recorded": return SortBy.Recorded;
                case "media_created": return SortBy.MediaCreated;
                case "name_ts": return SortBy.NameTimestamp;
                case "duration": return SortBy.Duration;
                case "size": return SortBy.Size;
                default: return SortBy.Name;
            }
        }

        private static MergeMode ParseMode(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "copy": return MergeMode.Copy;
                case "reencode": return MergeMode.Reencode;
                case "smart": return MergeMode.Smart;
                default: return MergeMode.Auto;
            }
        }

        /// <summary>Progres satu baris yang tidak membanjiri log yang dialihkan.</summary>
        private class Reporter
        {
            private double _last = -1;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly bool _tty = !Console.IsOutputRedirected;

            public void Report(Progress p)
            {
                double now = _clock.Elapsed.TotalSeconds;
                if (p.Stage != Stage.Done && p.Stage != Stage.Failed
                    && now - _last < 0.5) return;
                _last = now;

                const int barLen = 28;
                int filled = (int)(barLen * Math.Max(0, Math.Min(1, p.Fraction)));
                string bar = new string('#', filled) + new string('-', barLen - filled);
                string text = "[" + bar + "] "
                    + p.Percent.ToString("00.0", CultureInfo.InvariantCulture)
                    + "%  " + p.Message;
                if (_tty)
                {
                    if (text.Length > 110) text = text.Substring(0, 110);
                    Console.Write("\r" + text.PadRight(110));
                }
                else
                {
                    Console.WriteLine(text);
                }
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(AppInfo.Name + " " + AppInfo.Version
                + " - gabungkan video, atau bakar subtitle permanen.");
            Console.WriteLine();
            Console.WriteLine("Pemakaian:");
            Console.WriteLine("  VideoMerger.exe -i FOLDER -o HASIL.mp4");
            Console.WriteLine("  VideoMerger.exe --hardsub -i FOLDER [--out-dir FOLDER]");
            Console.WriteLine();
            Console.WriteLine("Penggabungan:");
            Console.WriteLine("  -i, --input FOLDER   folder sumber");
            Console.WriteLine("  -o, --output FILE    berkas hasil");
            Console.WriteLine("  -r, --recursive      ikut memindai subfolder");
            Console.WriteLine("  -s, --sort KUNCI     name, recorded, modified, created,");
            Console.WriteLine("                       media_created, name_ts, duration, size");
            Console.WriteLine("      --desc           urutkan menurun");
            Console.WriteLine("  -m, --mode MODE      auto, copy, smart, reencode");
            Console.WriteLine("      --crf N          kualitas encode ulang (14-32)");
            Console.WriteLine("      --preset NAMA    preset x264, mis. veryfast");
            Console.WriteLine("      --encoder NAMA   mis. h264_nvenc, h264_qsv, h264_amf");
            Console.WriteLine("      --strict         gagal kalau ada berkas dilewati");
            Console.WriteLine("      --list           tampilkan daftar saja");
            Console.WriteLine("      --no-faststart   jangan pindahkan indeks MP4 ke depan");
            Console.WriteLine();
            Console.WriteLine("Subtitle permanen (hardsub):");
            Console.WriteLine("      --hardsub        bakar subtitle, bukan menggabungkan");
            Console.WriteLine("      --sub-file FILE  pakai berkas .srt/.ass ini");
            Console.WriteLine("      --sub-track N    pakai trek ke-N di dalam video");
            Console.WriteLine("      --sub-lang KODE  pilih trek per bahasa, mis. ind");
            Console.WriteLine("      --sub-style      terapkan --sub-font dan --sub-size");
            Console.WriteLine("      --sub-font NAMA  nama font");
            Console.WriteLine("      --sub-size N     ukuran font");
            Console.WriteLine("      --suffix TEKS    akhiran nama hasil");
            Console.WriteLine("      --out-dir FOLDER folder hasil");
            Console.WriteLine("      --container EXT  format hasil, mis. .mp4 atau .mkv");
            Console.WriteLine();
            Console.WriteLine("Jalankan tanpa argumen untuk membuka tampilan grafis.");
        }
    }
}
