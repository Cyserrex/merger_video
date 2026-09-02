using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VideoMerger.Core
{
    /// <summary>Satu video berikut subtitle yang dipilih untuknya.</summary>
    public class HardsubItem
    {
        public VideoFile Video;
        public SubtitleTrack Track;
        public string ExternalPath = "";
        public List<SubtitleTrack> Tracks = new List<SubtitleTrack>();
        public List<string> Sidecars = new List<string>();
        public bool Selected = true;
        public string ResultPath = "";
        public string Error = "";

        public bool HasSource => !string.IsNullOrEmpty(ExternalPath) || Track != null;

        public string SourceLabel
        {
            get
            {
                if (!string.IsNullOrEmpty(ExternalPath))
                    return Path.GetFileName(ExternalPath);
                if (Track != null) return Track.Label;
                return "(belum dipilih)";
            }
        }
    }

    public class HardsubJob
    {
        public List<HardsubItem> Items = new List<HardsubItem>();
        public string OutputDir = "";
        public string Suffix = " - hardsub";
        public string Container = ".mp4";
        public SubtitleStyle Style = new SubtitleStyle();
        public TargetSpec Target = new TargetSpec();
        public string HwaccelEncoder = "";
        public bool CopyAudio = true;
        public bool Faststart = true;
        public bool Overwrite;

        public string OutputFor(HardsubItem item)
        {
            string stem = Path.GetFileNameWithoutExtension(item.Video.Path);
            string folder = !string.IsNullOrEmpty(OutputDir)
                ? OutputDir
                : Path.GetDirectoryName(item.Video.Path);
            return Path.Combine(folder ?? ".", stem + Suffix + Container);
        }
    }

    public class HardsubResult
    {
        public List<string> Done = new List<string>();
        public List<KeyValuePair<string, string>> Failed =
            new List<KeyValuePair<string, string>>();

        public bool Ok => Done.Count > 0 && Failed.Count == 0;
    }

    /// <summary>
    /// Membakar subtitle secara permanen ke dalam video.
    ///
    /// Subtitle lunak adalah trek terpisah yang harus ditemukan, didekode, dan
    /// digambar oleh pemutarnya. Banyak perangkat keras tidak melakukannya:
    /// pemutar DVD/VCD, TV pintar lama, head unit mobil, dan sebagian besar
    /// pemutaran dari flashdisk di TV mengabaikan treknya sama sekali,
    /// sehingga videonya jalan tanpa teks. Membakarnya ("hardsub") melukis
    /// teks ke dalam gambar, yang bisa ditampilkan setiap pemutar di dunia
    /// karena pada titik itu ia sekadar video.
    ///
    /// Harganya pasti dan tak terhindarkan: gambarnya berubah, jadi stream
    /// video harus di-encode ulang. Audio disalin tanpa disentuh - ia tidak
    /// terpengaruh subtitle, dan menyalinnya menghemat waktu sekaligus satu
    /// generasi penurunan kualitas.
    ///
    /// Satu proses ffmpeg per video, masing-masing diverifikasi sesudahnya,
    /// sehingga satu angkatan berisi 50 episode melaporkan persis mana yang
    /// berhasil.
    /// </summary>
    public class Hardsubber : FFmpegTask
    {
        /// <summary>
        /// Container yang bisa menampung hasilnya. MKV dan MP4 mencakup semua
        /// yang bisa dibaca TV atau pemutar DVD dari flashdisk.
        /// </summary>
        public static readonly string[] OutputExtensions =
            { ".mp4", ".mkv", ".mov", ".avi", ".ts" };

        public HardsubJob Job { get; private set; }
        private string _tempDir = "";

        private static readonly string NL = Environment.NewLine;
        private static readonly string NL2 = Environment.NewLine + Environment.NewLine;

        public Hardsubber(FFmpegTools tools, HardsubJob job,
                          Action<Progress> onProgress = null,
                          Action<string> onLog = null)
            : base(tools, onProgress, onLog)
        {
            Job = job;
        }

        // ---------------------------------------------------------- utama --
        public HardsubResult Run()
        {
            var items = new List<HardsubItem>();
            foreach (var i in Job.Items) if (i.Selected) items.Add(i);
            if (items.Count == 0)
                throw new MergeException("Tidak ada video yang dipilih.");

            var missing = new List<HardsubItem>();
            foreach (var i in items) if (!i.HasSource) missing.Add(i);
            if (missing.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append(missing.Count)
                  .Append(" video belum punya subtitle yang dipilih:").Append(NL);
                for (int n = 0; n < Math.Min(8, missing.Count); n++)
                    sb.Append("  - ").Append(missing[n].Video.Name).Append(NL);
                throw new MergeException(sb.ToString());
            }

            CheckInputsReadable(items);
            CheckDisk(items);

            _tempDir = Path.Combine(Path.GetTempPath(),
                "vmerge_sub_" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(_tempDir);

            var result = new HardsubResult();
            double totalSeconds = 0;
            foreach (var i in items) totalSeconds += i.Video.Duration;
            if (totalSeconds <= 0) totalSeconds = 1.0;
            double doneSeconds = 0;

            try
            {
                for (int index = 1; index <= items.Count; index++)
                {
                    CheckCancel();
                    var item = items[index - 1];
                    double baseFraction = doneSeconds / totalSeconds;
                    double span = item.Video.Duration / totalSeconds;
                    try
                    {
                        string outPath = BurnOne(item, index, items.Count,
                                                 baseFraction, span);
                        item.ResultPath = outPath;
                        result.Done.Add(outPath);
                    }
                    catch (CancelledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Satu episode tidak terbaca di antara 50 tidak boleh
                        // membuang 49 yang berhasil. Catat lalu lanjut;
                        // pemanggilnya melaporkan daftarnya di akhir.
                        item.Error = ex.Message;
                        result.Failed.Add(new KeyValuePair<string, string>(
                            item.Video.Name, ex.Message));
                        Log("GAGAL " + item.Video.Name + ": " + ex.Message);
                    }
                    doneSeconds += item.Video.Duration;
                }
            }
            finally
            {
                CleanupTemp();
            }

            if (result.Done.Count == 0)
            {
                var sb = new StringBuilder();
                sb.Append("Tidak ada video yang berhasil diproses.").Append(NL2);
                for (int n = 0; n < Math.Min(5, result.Failed.Count); n++)
                    sb.Append("  - ").Append(result.Failed[n].Key).Append(": ")
                      .Append(FirstLine(result.Failed[n].Value)).Append(NL);
                throw new MergeException(sb.ToString());
            }
            return result;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int cut = text.IndexOfAny(new[] { '\r', '\n' });
            return cut < 0 ? text : text.Substring(0, cut);
        }

        // ------------------------------------------------------ satu berkas --
        private string BurnOne(HardsubItem item, int index, int count,
                               double baseFraction, double span)
        {
            string outPath = Job.OutputFor(item);
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (string.Equals(Path.GetFullPath(outPath),
                              Path.GetFullPath(item.Video.Path),
                              StringComparison.OrdinalIgnoreCase))
                // Menulis di atas masukan sambil membacanya menghasilkan
                // berkas terpotong sekaligus menghilangkan aslinya. Tidak
                // pernah pantas "diperbaiki" diam-diam.
                throw new MergeException(
                    "Nama hasil sama dengan video aslinya:" + NL + outPath + NL2
                    + "Ubah akhiran nama atau folder tujuan.");

            if (File.Exists(outPath) && !Job.Overwrite)
                outPath = Paths.Unique(outPath);

            Emit(new Progress
            {
                Stage = Stage.Normalizing,
                Fraction = baseFraction,
                CurrentIndex = index,
                TotalItems = count,
                Message = "Menyiapkan subtitle " + index + "/" + count + ": "
                          + item.Video.Name,
            });

            string work = Path.Combine(_tempDir,
                "job" + index.ToString("0000", CultureInfo.InvariantCulture));
            BurnPlan plan = Subtitles.PrepareBurn(
                Tools, item.Video.Path, work, item.Track, item.ExternalPath,
                Job.Style, index);
            Log("Subtitle " + item.Video.Name + ": " + plan.SourceLabel
                + " (" + plan.Kind.ToString().ToLowerInvariant() + ")");

            string tempOut = Path.Combine(work, "out" + Job.Container);
            var cmd = BurnCmd(item, plan, tempOut);

            RunFFmpeg(cmd, item.Video.Duration, baseFraction, span * 0.97,
                      Stage.Normalizing,
                      "Membakar subtitle " + index + "/" + count,
                      index, count,
                      // Nama berkas telanjang di dalam filter; lihat keterangan
                      // kelas Subtitles.
                      plan.Kind == BurnKind.Text ? plan.WorkDir : null);

            Verify(tempOut, item.Video.Duration, item.Video.Name);

            Emit(new Progress
            {
                Stage = Stage.Finalizing,
                Fraction = baseFraction + span * 0.98,
                CurrentIndex = index,
                TotalItems = count,
                Message = "Memindahkan hasil " + index + "/" + count + "...",
            });
            // Di-encode ke folder sementara lalu dipindahkan, supaya proses
            // yang terputus tidak pernah meninggalkan berkas setengah jadi di
            // sebelah aslinya yang tampak seperti sudah selesai.
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tempOut, outPath);
            Log("Selesai: " + outPath);
            return outPath;
        }

        private List<string> BurnCmd(HardsubItem item, BurnPlan plan, string outPath)
        {
            var cmd = new List<string>
            {
                Tools.FFmpeg, "-hide_banner", "-y", "-i", item.Video.Path,
            };

            if (plan.Kind == BurnKind.Image)
            {
                // Subtitle bitmap ditumpuk, bukan digambar ulang. overlay
                // butuh kedua masukan dalam satu graf, jadi ini tidak bisa
                // memakai -vf.
                cmd.AddRange(new[]
                {
                    "-filter_complex",
                    "[0:v][0:s:" + plan.StreamIndex.ToString(CultureInfo.InvariantCulture)
                    + "]overlay[v]",
                    "-map", "[v]",
                });
            }
            else
            {
                cmd.AddRange(new[] { "-vf", plan.FilterArg, "-map", "0:v:0" });
            }

            if (item.Video.HasAudio)
            {
                cmd.AddRange(new[] { "-map", "0:a" });
                if (Job.CopyAudio)
                    // Subtitle tidak menyentuh audio, jadi meng-encode ulangnya
                    // hanya memakan waktu dan satu generasi kualitas.
                    cmd.AddRange(new[] { "-c:a", "copy" });
                else
                    cmd.AddRange(new[]
                    {
                        "-c:a", Job.Target.AEncoder, "-b:a", Job.Target.ABitrate,
                    });
            }

            cmd.AddRange(VideoFlags());
            // Trek subtitle lunaknya sengaja dibuang: sekarang ia sudah
            // terlukis di dalam gambar, dan mempertahankannya membuat pemutar
            // menggambar teksnya dua kali.
            cmd.AddRange(new[] { "-sn", "-dn" });
            cmd.AddRange(ContainerFlags(outPath));
            cmd.AddRange(new[] { "-progress", "pipe:1", "-nostats", outPath });
            return cmd;
        }

        private List<string> VideoFlags()
        {
            var inv = CultureInfo.InvariantCulture;
            var target = Job.Target;
            string encoder = !string.IsNullOrEmpty(Job.HwaccelEncoder)
                ? Job.HwaccelEncoder : "libx264";

            if (encoder == "h264_nvenc" || encoder == "hevc_nvenc")
                // Offset yang sama dengan penggabung: -cq NVENC bukan -crf x264.
                return new List<string>
                {
                    "-c:v", encoder, "-preset", "p5", "-rc", "vbr",
                    "-cq", Math.Min(51, target.Crf + 5).ToString(inv),
                };
            if (encoder == "h264_qsv" || encoder == "hevc_qsv")
                return new List<string>
                {
                    "-c:v", encoder, "-global_quality", target.Crf.ToString(inv),
                };
            if (encoder == "h264_amf" || encoder == "hevc_amf")
                return new List<string>
                {
                    "-c:v", encoder, "-quality", "balanced", "-rc", "cqp",
                    "-qp_i", target.Crf.ToString(inv),
                    "-qp_p", target.Crf.ToString(inv),
                };

            // yuv420p bukan bawaan encoder untuk sumber 10-bit atau 4:2:2, dan
            // apa pun selain itu persis yang tidak bisa didekode pemutar lama
            // yang justru jadi alasan fitur ini ada.
            return new List<string>
            {
                "-c:v", encoder, "-preset", target.Preset,
                "-crf", target.Crf.ToString(inv), "-pix_fmt", "yuv420p",
            };
        }

        private List<string> ContainerFlags(string outPath)
        {
            string ext = (Path.GetExtension(outPath) ?? "").ToLowerInvariant();
            if ((ext == ".mp4" || ext == ".m4v" || ext == ".mov") && Job.Faststart)
                return new List<string> { "-movflags", "+faststart" };
            return new List<string>();
        }

        // ----------------------------------------------------- pemeriksaan --
        /// <summary>Buka semua masukan sekarang, bukan gagal 40 menit kemudian.</summary>
        private void CheckInputsReadable(List<HardsubItem> items)
        {
            var bad = new List<string>();
            foreach (var item in items)
            {
                CheckCancel();
                try
                {
                    using (var stream = File.Open(item.Video.Path, FileMode.Open,
                                                  FileAccess.Read, FileShare.ReadWrite))
                        stream.ReadByte();
                }
                catch (Exception ex)
                {
                    bad.Add("  - " + item.Video.Name + ": " + ex.Message);
                }
            }
            if (bad.Count == 0) return;
            var sb = new StringBuilder("Video berikut tidak bisa dibaca:").Append(NL);
            for (int n = 0; n < Math.Min(10, bad.Count); n++)
                sb.Append(bad[n]).Append(NL);
            throw new MergeException(sb.ToString());
        }

        /// <summary>Menolak sebelum mulai kalau tujuannya jelas tidak muat.</summary>
        private void CheckDisk(List<HardsubItem> items)
        {
            long needed = 0;
            foreach (var item in items)
                // Hasil encode ulang biasanya lebih kecil dari sumbernya,
                // tetapi CRF rendah pada sumber yang sudah padat bisa
                // melampauinya. 1,2x adalah margin murah yang tetap menangkap
                // disk yang benar-benar penuh.
                needed += (long)(item.Video.Size * 1.2);

            string folder = !string.IsNullOrEmpty(Job.OutputDir)
                ? Job.OutputDir
                : Path.GetDirectoryName(items[0].Video.Path);
            if (string.IsNullOrEmpty(folder)) folder = ".";

            long free = Paths.DiskFree(folder);
            if (free > 0 && needed > free)
                throw new MergeException(
                    "Ruang disk tidak cukup di " + folder + "." + NL
                    + "Perkiraan dibutuhkan "
                    + (needed / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture)
                    + " GB, tersedia "
                    + (free / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture)
                    + " GB.");
        }

        /// <summary>
        /// Berkas hasil bakar yang jauh terlalu pendek berarti ffmpeg menyerah
        /// di tengah jalan.
        ///
        /// Pelajaran yang sama dengan penggabung: ffmpeg keluar dengan 0
        /// setelah meninggalkan sebuah masukan, jadi durasi yang benar-benar
        /// ditulisnya adalah satu-satunya pemeriksaan yang jujur.
        /// </summary>
        private void Verify(string outPath, double expected, string name)
        {
            if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                throw new MergeException("Hasil kosong untuk " + name + ".");
            if (expected <= 0) return;

            double actual = DurationOf(Tools, outPath);
            if (actual <= 0)
                throw new MergeException("Durasi hasil untuk " + name + " tidak terbaca.");

            // Satu detik, atau 1% untuk berkas yang sangat panjang - membakar
            // subtitle tidak mengubah lini masanya, jadi selisih di luar
            // pembulatan adalah kehilangan yang nyata.
            double tolerance = Math.Max(1.0, expected * 0.01);
            if (Math.Abs(actual - expected) > tolerance)
                throw new MergeException(
                    "Durasi hasil " + name + " tidak cocok: "
                    + Humanize.Duration(actual) + " dari "
                    + Humanize.Duration(expected)
                    + ". Video sumber kemungkinan rusak di tengah.");
        }

        private void CleanupTemp()
        {
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch (Exception) { }
            }
            _tempDir = "";
        }

        public static double DurationOf(FFmpegTools tools, string path)
        {
            var cmd = new[]
            {
                tools.FFprobe, "-v", "error", "-show_entries", "format=duration",
                "-of", "default=nw=1:nk=1", path,
            };
            try
            {
                var res = Shell.RunCapture(cmd, 60);
                double value;
                return double.TryParse((res.StdOut ?? "").Trim(),
                                       NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out value)
                    ? value : 0.0;
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Bangun satu HardsubItem per video, lengkap dengan pilihan subtitle-nya.
        ///
        /// Trek tertanam maupun berkas pendamping sama-sama ditawarkan. Trek
        /// tertanam memenangkan pilihan bawaan bila ada, karena berkas
        /// pendamping yang tergeletak di folder bisa saja milik rilis lain.
        /// </summary>
        public static List<HardsubItem> CollectSources(
            FFmpegTools tools, IList<VideoFile> videos, Func<bool> cancel = null)
        {
            var items = new List<HardsubItem>();
            foreach (var video in videos)
            {
                if (cancel != null && cancel()) break;
                var tracks = Subtitles.ListTracks(tools, video.Path);
                var sidecars = Subtitles.SidecarSubs(video.Path);
                var item = new HardsubItem
                {
                    Video = video,
                    Tracks = tracks,
                    Sidecars = sidecars,
                };
                var chosen = Subtitles.PickDefaultTrack(tracks);
                if (chosen != null)
                {
                    item.Track = chosen;
                }
                else if (sidecars.Count > 0)
                {
                    item.ExternalPath = sidecars[0];
                }
                else
                {
                    item.Selected = false;
                    item.Error = "Tidak ada subtitle di dalam video atau di folder ini";
                }
                items.Add(item);
            }
            return items;
        }
    }
}
