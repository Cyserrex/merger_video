using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Web.Script.Serialization;

namespace VideoMerger.Core
{
    /// <summary>
    /// Membaca parameter media dengan ffprobe.
    ///
    /// Pemeriksaan inilah yang memungkinkan aplikasi memilih antara jalur
    /// salin-langsung yang hitungan detik dan jalur encode ulang yang
    /// hitungan jam, jadi setiap berkas diperiksa sebelum digabung. Satu
    /// panggilan ffprobe per berkas dengan ~30 ms berarti 3 detik untuk 100
    /// berkas kalau berurutan; kumpulan thread kecil memangkasnya.
    ///
    /// JSON-nya diurai dengan JavaScriptSerializer dari System.Web.Extensions
    /// - bagian dari .NET Framework, jadi tidak ada DLL yang perlu ikut
    /// didistribusikan bersama exe.
    /// </summary>
    public static class Prober
    {
        public const double ProbeTimeout = 60.0;

        // -- pembantu pengurai ------------------------------------------------
        /// <summary>"30000/1001" -> 29,97. Mengembalikan 0 untuk "0/0" dan sampah.</summary>
        public static double ParseRate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0.0;
            string[] bits = value.Split('/');
            double num, den;
            if (bits.Length == 1)
                return double.TryParse(bits[0], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out num) ? num : 0.0;
            if (bits.Length != 2) return 0.0;
            if (!double.TryParse(bits[0], NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out num)) return 0.0;
            if (!double.TryParse(bits[1], NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out den)) return 0.0;
            return den == 0 ? 0.0 : num / den;
        }

        /// <summary>
        /// "90000/3003" -> "30000/1001", supaya laju yang sama dibandingkan sama.
        ///
        /// Membandingkan nilai ini sebagai string mentah persis yang membuat
        /// sepasang klip yang sebenarnya identik tampak tidak kompatibel (atau
        /// lebih buruk, sepasang yang benar-benar berbeda tampak kompatibel),
        /// jadi setiap laju dan time base melewati fungsi ini.
        /// </summary>
        public static string NormaliseFraction(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string[] bits = value.Split('/');
            if (bits.Length != 2) return value;
            long num, den;
            if (!long.TryParse(bits[0], NumberStyles.Integer,
                               CultureInfo.InvariantCulture, out num)
                || !long.TryParse(bits[1], NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out den))
                return value;
            if (den == 0) return value;

            long g = Gcd(Math.Abs(num), Math.Abs(den));
            if (g == 0) return value;
            num /= g; den /= g;
            if (den < 0) { num = -num; den = -den; }
            return num.ToString(CultureInfo.InvariantCulture) + "/"
                   + den.ToString(CultureInfo.InvariantCulture);
        }

        private static long Gcd(long a, long b)
        {
            while (b != 0) { long t = b; b = a % b; a = t; }
            return a;
        }

        /// <summary>creation_time container berformat ISO-8601 UTC.</summary>
        public static DateTime? ParseCreationTime(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            DateTime parsed;
            if (DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal
                                  | DateTimeStyles.AssumeUniversal, out parsed))
                return parsed.ToLocalTime();
            return null;
        }

        // -- akses dictionary yang aman --------------------------------------
        private static Dictionary<string, object> Dict(object node)
            => node as Dictionary<string, object>;

        private static object Get(Dictionary<string, object> node, string key)
        {
            object value;
            return node != null && node.TryGetValue(key, out value) ? value : null;
        }

        private static string Str(Dictionary<string, object> node, string key)
        {
            object value = Get(node, key);
            return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int Int(Dictionary<string, object> node, string key)
        {
            int result;
            return int.TryParse(Str(node, key), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static double Dbl(Dictionary<string, object> node, string key)
        {
            double result;
            return double.TryParse(Str(node, key), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out result) ? result : 0.0;
        }

        private static List<Dictionary<string, object>> Nodes(object list)
        {
            var result = new List<Dictionary<string, object>>();
            var array = list as object[];
            if (array == null) return result;
            foreach (object item in array)
            {
                var node = item as Dictionary<string, object>;
                if (node != null) result.Add(node);
            }
            return result;
        }

        // -- rotasi -----------------------------------------------------------
        private static int RotationOf(Dictionary<string, object> stream)
        {
            foreach (var side in Nodes(Get(stream, "side_data_list")))
            {
                if (!side.ContainsKey("rotation")) continue;
                double value;
                if (double.TryParse(Convert.ToString(side["rotation"],
                                                     CultureInfo.InvariantCulture),
                                    NumberStyles.Float, CultureInfo.InvariantCulture,
                                    out value))
                {
                    int deg = (int)Math.Round(value) % 360;
                    return deg < 0 ? deg + 360 : deg;
                }
            }
            var tags = Dict(Get(stream, "tags"));
            string tag = Str(tags, "rotate");
            if (!string.IsNullOrEmpty(tag))
            {
                double value;
                if (double.TryParse(tag, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out value))
                {
                    int deg = (int)value % 360;
                    return deg < 0 ? deg + 360 : deg;
                }
            }
            return 0;
        }

        /// <summary>Thumbnail tertanam muncul sebagai stream video 1 frame; abaikan.</summary>
        private static bool IsCoverArt(Dictionary<string, object> stream)
        {
            var disposition = Dict(Get(stream, "disposition"));
            if (Int(disposition, "attached_pic") != 0) return true;
            string codec = Str(stream, "codec_name");
            string rate = Str(stream, "avg_frame_rate");
            return (codec == "mjpeg" || codec == "png" || codec == "bmp")
                   && (rate == "0/0" || rate == "");
        }

        /// <summary>
        /// Durasi container, mundur ke stream terpanjang bila tidak ada.
        /// Stream mentah (.h264, sebagian ekspor .dav) tidak membawa durasi
        /// container sama sekali, dan nilai stream jadi satu-satunya sumber.
        /// </summary>
        private static double ParseDuration(Dictionary<string, object> format,
                                            List<Dictionary<string, object>> streams)
        {
            double value = Dbl(format, "duration");
            if (value > 0) return value;
            double best = 0.0;
            foreach (var s in streams) best = Math.Max(best, Dbl(s, "duration"));
            return best;
        }

        // -- pemeriksaan satu berkas ------------------------------------------
        /// <summary>Isi `video` dengan laporan ffprobe. Tidak pernah melempar.</summary>
        public static VideoFile ProbeFile(FFmpegTools tools, VideoFile video)
        {
            video.Probed = true;
            video.Valid = false;

            if (video.Size == 0)
            {
                video.Error = "File kosong (0 byte)";
                return video;
            }

            var cmd = new[]
            {
                tools.FFprobe, "-v", "error", "-hide_banner",
                "-print_format", "json", "-show_format", "-show_streams",
                "-i", video.Path,
            };

            CapturedResult res;
            try
            {
                res = Shell.RunCapture(cmd, ProbeTimeout);
            }
            catch (Exception ex)
            {
                video.Error = "Gagal menjalankan ffprobe: " + ex.Message;
                return video;
            }

            if (res.TimedOut)
            {
                video.Error = "ffprobe timeout - file mungkin rusak";
                return video;
            }
            if (res.ExitCode != 0)
            {
                string[] lines = (res.StdErr ?? "").Trim()
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                video.Error = lines.Length > 0
                    ? lines[lines.Length - 1].Trim()
                    : "Bukan file media yang valid";
                return video;
            }

            Dictionary<string, object> data;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                data = serializer.DeserializeObject(
                    string.IsNullOrWhiteSpace(res.StdOut) ? "{}" : res.StdOut)
                    as Dictionary<string, object>;
            }
            catch (Exception)
            {
                video.Error = "Output ffprobe tidak bisa dibaca";
                return video;
            }
            if (data == null)
            {
                video.Error = "Output ffprobe tidak bisa dibaca";
                return video;
            }

            var format = Dict(Get(data, "format")) ?? new Dictionary<string, object>();
            var streams = Nodes(Get(data, "streams"));

            video.FormatName = Str(format, "format_name");
            video.Duration = ParseDuration(format, streams);
            video.MediaCreated = ParseCreationTime(
                Str(Dict(Get(format, "tags")), "creation_time"));

            var videoStreams = new List<Dictionary<string, object>>();
            var audioStreams = new List<Dictionary<string, object>>();
            foreach (var s in streams)
            {
                string type = Str(s, "codec_type");
                if (type == "video" && !IsCoverArt(s)) videoStreams.Add(s);
                else if (type == "audio") audioStreams.Add(s);
            }

            video.VideoStreamCount = videoStreams.Count;
            video.AudioStreamCount = audioStreams.Count;
            video.HasVideo = videoStreams.Count > 0;
            video.HasAudio = audioStreams.Count > 0;

            if (video.HasVideo)
            {
                var s = videoStreams[0];
                video.VCodec = Str(s, "codec_name");
                video.VCodecTag = Str(s, "codec_tag_string");
                video.Width = Int(s, "width");
                video.Height = Int(s, "height");
                video.PixFmt = Str(s, "pix_fmt");
                string sar = Str(s, "sample_aspect_ratio");
                video.Sar = string.IsNullOrEmpty(sar) ? "1:1" : sar;
                double fps = ParseRate(Str(s, "avg_frame_rate"));
                video.Fps = fps > 0 ? fps : ParseRate(Str(s, "r_frame_rate"));
                video.VRate = NormaliseFraction(Str(s, "r_frame_rate"));
                video.VTimeBase = NormaliseFraction(Str(s, "time_base"));
                video.FieldOrder = Str(s, "field_order");
                video.ColorRange = Str(s, "color_range");
                video.ColorSpace = Str(s, "color_space");
                video.VProfile = Str(s, "profile");
                video.VLevel = Int(s, "level");
                video.Rotation = RotationOf(s);
                video.VideoDuration = Dbl(s, "duration");
                if (video.MediaCreated == null)
                    video.MediaCreated = ParseCreationTime(
                        Str(Dict(Get(s, "tags")), "creation_time"));
            }

            if (video.HasAudio)
            {
                var s = audioStreams[0];
                video.ACodec = Str(s, "codec_name");
                video.ACodecTag = Str(s, "codec_tag_string");
                video.SampleRate = Int(s, "sample_rate");
                video.Channels = Int(s, "channels");
                string layout = Str(s, "channel_layout");
                if (string.IsNullOrEmpty(layout))
                    layout = video.Channels == 1 ? "mono"
                           : video.Channels == 2 ? "stereo" : "";
                video.ChannelLayout = layout;
                video.AProfile = Str(s, "profile");
                video.ATimeBase = NormaliseFraction(Str(s, "time_base"));
            }

            if (!video.HasVideo)
            {
                video.Error = "Tidak ada stream video di dalam file";
                return video;
            }
            if (video.Duration <= 0)
            {
                video.Error = "Durasi tidak terbaca - file mungkin rusak/terpotong";
                return video;
            }

            video.Valid = true;
            video.Error = "";
            return video;
        }

        /// <summary>Periksa sekumpulan berkas paralel, melaporkan tiap yang selesai.</summary>
        public static IList<VideoFile> ProbeMany(
            FFmpegTools tools, IList<VideoFile> files, int workers = 8,
            Action<int, int, VideoFile> onProgress = null,
            Func<bool> cancel = null)
        {
            int total = files.Count;
            if (total == 0) return files;

            workers = Math.Max(1, Math.Min(workers,
                Math.Min(total, Environment.ProcessorCount * 2)));

            int next = -1;
            int done = 0;
            var gate = new object();
            var threads = new List<Thread>();

            for (int w = 0; w < workers; w++)
            {
                var thread = new Thread(() =>
                {
                    while (true)
                    {
                        if (cancel != null && cancel()) return;
                        int index = Interlocked.Increment(ref next);
                        if (index >= total) return;
                        var video = files[index];
                        try
                        {
                            ProbeFile(tools, video);
                        }
                        catch (Exception ex)
                        {
                            // Defensif: satu berkas aneh tidak boleh
                            // menjatuhkan seluruh pemeriksaan.
                            video.Probed = true;
                            video.Valid = false;
                            video.Error = "Gagal memeriksa: " + ex.Message;
                        }
                        lock (gate)
                        {
                            done++;
                            if (onProgress != null) onProgress(done, total, video);
                        }
                    }
                });
                // Latar belakang: thread yang belum selesai tidak boleh
                // menahan proses tetap hidup setelah jendelanya ditutup -
                // aplikasi akan tampak sudah tertutup padahal belum.
                thread.IsBackground = true;
                thread.Start();
                threads.Add(thread);
            }

            foreach (var thread in threads) thread.Join();
            return files;
        }

        // ------------------------------------------------------ kompatibilitas --
        public static Dictionary<string, List<VideoFile>> GroupBySignature(
            IEnumerable<VideoFile> files)
        {
            var groups = new Dictionary<string, List<VideoFile>>(StringComparer.Ordinal);
            foreach (var f in files)
            {
                string key = f.CopySignature();
                List<VideoFile> bucket;
                if (!groups.TryGetValue(key, out bucket))
                    groups[key] = bucket = new List<VideoFile>();
                bucket.Add(f);
            }
            return groups;
        }

        /// <summary>Bisakah klip-klip ini disambung dengan `-c copy`?</summary>
        public static bool CanStreamCopy(IEnumerable<VideoFile> files,
                                         out List<string> reasons)
        {
            reasons = new List<string>();
            var valid = new List<VideoFile>();
            foreach (var f in files) if (f.Valid) valid.Add(f);
            if (valid.Count < 2) return true;

            var first = valid[0];
            for (int i = 1; i < valid.Count; i++)
            {
                var diff = first.SignatureDiff(valid[i]);
                if (diff.Count > 0)
                    reasons.Add(valid[i].Name + ": " + string.Join("; ", diff));
            }
            if (reasons.Count > 10) reasons = reasons.GetRange(0, 10);
            return reasons.Count == 0;
        }
    }
}
