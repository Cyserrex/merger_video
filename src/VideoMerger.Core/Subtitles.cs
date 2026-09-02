using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace VideoMerger.Core
{
    /// <summary>Subtitle tidak bisa disiapkan; pesannya untuk pengguna.</summary>
    public class SubtitleException : Exception
    {
        public SubtitleException(string message) : base(message) { }
    }

    /// <summary>
    /// Menemukan subtitle dan mengubahnya jadi sesuatu yang bisa dibakar ffmpeg.
    ///
    /// "Hardsub" berarti melukis subtitle ke dalam gambarnya sendiri, supaya
    /// tetap ada saat diputar di TV, pemutar DVD/VCD, atau aplikasi apa pun
    /// yang mengabaikan trek subtitle terpisah di sebelahnya. Ini satu-satunya
    /// operasi di sini yang SELALU menuntut encode ulang penuh: pikselnya
    /// berubah, jadi tidak ada yang bisa disalin.
    ///
    /// Ada dua bentuk subtitle dan keduanya butuh perlakuan yang sama sekali
    /// berbeda:
    ///
    ///   TEKS    subrip/ass/ssa/mov_text/webvtt - digambar libass. Dicapai
    ///           dengan filter <c>subtitles</c>, yang menerima JALUR BERKAS.
    ///
    ///   GAMBAR  hdmv_pgs_subtitle (Blu-ray), dvd_subtitle (DVD),
    ///           dvb_subtitle - ini sudah berupa gambar. libass tidak bisa
    ///           menyentuhnya; keduanya ditumpuk dengan <c>overlay</c>
    ///           langsung dari stream masukan, tanpa jalur sama sekali.
    ///
    /// Penanganan jalur di bawah tampak berlebihan karena filter
    /// <c>subtitles</c> mengurai argumennya sampai tiga kali (pemisah graf
    /// filter, pemisah opsi, lalu libass), sehingga jalur Windows seperti
    /// <c>D:\Video\Anime [2024]\ep01.srt</c> membawa titik dua, dua backslash,
    /// dan sepasang kurung siku yang semuanya bermakna di sana. Alih-alih
    /// meloloskan semua itu, setiap subtitle teks disalin ke folder kerja
    /// dengan nama ASCII polos dan dirujuk dengan nama berkas telanjang
    /// sementara direktori kerja ffmpeg diarahkan ke sana. Cara itu kebal
    /// terhadap setiap karakter yang bisa ada di nama berkas.
    /// </summary>
    public static class Subtitles
    {
        /// <summary>Codec subtitle yang bisa digambar libass. mov_text isi MP4.</summary>
        public static readonly HashSet<string> TextCodecs =
            new HashSet<string>(new[]
            {
                "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text",
                "subviewer", "microdvd", "sami", "realtext", "stl",
                "subviewer1", "vplayer", "pjs", "mpl2", "jacosub",
            }, StringComparer.OrdinalIgnoreCase);

        /// <summary>Subtitle berbasis gambar: ditumpuk, tidak pernah digambar ulang.</summary>
        public static readonly HashSet<string> ImageCodecs =
            new HashSet<string>(new[]
            {
                "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub",
                "hdmv_text_subtitle",
            }, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Berkas pendamping di sebelah video. Urutannya berarti: .ass membawa
        /// gaya yang tidak dimiliki .srt, jadi ia menang bila rilisnya
        /// menyertakan keduanya.
        /// </summary>
        public static readonly string[] SidecarExtensions =
            { ".ass", ".ssa", ".srt", ".vtt", ".sub", ".smi", ".ttml" };

        public const double ProbeTimeout = 60.0;

        // Kode dua dan tiga huruf yang lazim ditemui, dipetakan ke sesuatu
        // yang dikenali orang saat membaca daftar pilihan.
        private static readonly Dictionary<string, string> LanguageNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ind", "Indonesia" }, { "id", "Indonesia" }, { "in", "Indonesia" },
                { "eng", "Inggris" }, { "en", "Inggris" },
                { "jpn", "Jepang" }, { "ja", "Jepang" },
                { "kor", "Korea" }, { "ko", "Korea" },
                { "zho", "Mandarin" }, { "chi", "Mandarin" }, { "zh", "Mandarin" },
                { "ara", "Arab" }, { "ar", "Arab" },
                { "may", "Melayu" }, { "msa", "Melayu" }, { "ms", "Melayu" },
                { "tha", "Thai" }, { "th", "Thai" },
                { "vie", "Vietnam" }, { "vi", "Vietnam" },
                { "spa", "Spanyol" }, { "es", "Spanyol" },
                { "fra", "Prancis" }, { "fre", "Prancis" }, { "fr", "Prancis" },
                { "deu", "Jerman" }, { "ger", "Jerman" }, { "de", "Jerman" },
                { "nld", "Belanda" }, { "dut", "Belanda" }, { "nl", "Belanda" },
                { "por", "Portugis" }, { "pt", "Portugis" },
                { "rus", "Rusia" }, { "ru", "Rusia" },
                { "hin", "Hindi" }, { "hi", "Hindi" },
                { "und", "tidak diketahui" },
            };

        /// <summary>"ind" -> "Indonesia". Kode tak dikenal dikembalikan apa adanya.</summary>
        public static string LanguageName(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            string name;
            return LanguageNames.TryGetValue(tag.Trim(), out name) ? name : tag;
        }

        // ------------------------------------------------------- penemuan --
        /// <summary>Setiap stream subtitle di `path`, urut stream. Kosong bila tak ada.</summary>
        public static List<SubtitleTrack> ListTracks(FFmpegTools tools, string path)
        {
            var tracks = new List<SubtitleTrack>();
            var cmd = new[]
            {
                tools.FFprobe, "-v", "error", "-select_streams", "s",
                "-show_entries",
                "stream=index,codec_name:stream_tags=language,title:"
                + "stream_disposition=default,forced",
                "-of", "json", path,
            };

            Dictionary<string, object> data;
            try
            {
                var res = Shell.RunCapture(cmd, ProbeTimeout);
                if (res.TimedOut || res.ExitCode != 0) return tracks;
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                data = serializer.DeserializeObject(
                    string.IsNullOrWhiteSpace(res.StdOut) ? "{}" : res.StdOut)
                    as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return tracks;
            }
            if (data == null) return tracks;

            object streamsNode;
            if (!data.TryGetValue("streams", out streamsNode)) return tracks;
            var array = streamsNode as object[];
            if (array == null) return tracks;

            int order = 0;
            foreach (object item in array)
            {
                var stream = item as Dictionary<string, object>;
                if (stream == null) continue;
                var tags = Sub(stream, "tags");
                var disp = Sub(stream, "disposition");
                tracks.Add(new SubtitleTrack
                {
                    StreamIndex = order++,
                    Codec = (Text(stream, "codec_name") ?? "").ToLowerInvariant(),
                    Language = Text(tags, "language"),
                    Title = Text(tags, "title"),
                    Forced = Flag(disp, "forced"),
                    Default = Flag(disp, "default"),
                });
            }
            return tracks;
        }

        private static Dictionary<string, object> Sub(Dictionary<string, object> node,
                                                      string key)
        {
            object value;
            if (node == null || !node.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        private static string Text(Dictionary<string, object> node, string key)
        {
            object value;
            if (node == null || !node.TryGetValue(key, out value) || value == null)
                return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool Flag(Dictionary<string, object> node, string key)
        {
            string text = Text(node, key);
            int value;
            return int.TryParse(text, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out value) && value != 0;
        }

        /// <summary>
        /// Berkas subtitle di sebelah video, kandidat terbaik lebih dulu.
        ///
        /// Mencocokkan baik <c>ep01.srt</c> maupun penamaan yang sangat lazim
        /// <c>ep01.id.srt</c> / <c>ep01.indonesian.srt</c>, karena apa pun yang
        /// diawali nama video itu sendiri hampir pasti subtitle-nya.
        /// </summary>
        public static List<string> SidecarSubs(string videoPath)
        {
            var result = new List<string>();
            string folder, stem;
            try
            {
                folder = Path.GetDirectoryName(Path.GetFullPath(videoPath));
                stem = Path.GetFileNameWithoutExtension(videoPath);
            }
            catch (Exception) { return result; }
            if (string.IsNullOrEmpty(stem) || !Directory.Exists(folder)) return result;

            var found = new List<Tuple<int, int, string>>();
            string[] entries;
            try { entries = Directory.GetFiles(folder); }
            catch (Exception) { return result; }

            foreach (string entry in entries)
            {
                string ext = (Path.GetExtension(entry) ?? "").ToLowerInvariant();
                int extRank = Array.IndexOf(SidecarExtensions, ext);
                if (extRank < 0) continue;

                string baseName = Path.GetFileNameWithoutExtension(entry);
                int rank;
                if (string.Equals(baseName, stem, StringComparison.OrdinalIgnoreCase))
                    rank = 0;                            // cocok persis
                else if (baseName.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    rank = 1;                            // ep01.id.srt
                else
                    continue;
                found.Add(Tuple.Create(rank, extRank, entry));
            }

            found.Sort((a, b) =>
            {
                int cmp = a.Item1.CompareTo(b.Item1);
                if (cmp != 0) return cmp;
                cmp = a.Item2.CompareTo(b.Item2);
                if (cmp != 0) return cmp;
                return string.Compare(a.Item3, b.Item3, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var item in found) result.Add(item.Item3);
            return result;
        }

        /// <summary>
        /// Trek yang paling mungkin dimaksud pengguna: Indonesia, lalu bawaan,
        /// lalu yang pertama.
        ///
        /// Trek "forced" dilewati selama masih ada pilihan lain - trek itu
        /// hanya memuat baris berbahasa asing, sehingga membakarnya alih-alih
        /// subtitle penuh diam-diam menghasilkan video yang hampir tanpa teks.
        /// </summary>
        public static SubtitleTrack PickDefaultTrack(IList<SubtitleTrack> tracks)
        {
            var usable = new List<SubtitleTrack>();
            foreach (var t in tracks) if (t.Burnable) usable.Add(t);
            if (usable.Count == 0) return null;

            var full = new List<SubtitleTrack>();
            foreach (var t in usable) if (!t.Forced) full.Add(t);
            if (full.Count == 0) full = usable;

            foreach (string tag in new[] { "ind", "id", "in" })
                foreach (var t in full)
                    if (string.Equals(t.Language, tag, StringComparison.OrdinalIgnoreCase))
                        return t;
            foreach (var t in full) if (t.Default) return t;
            return full[0];
        }

        // ------------------------------------------------------ penyiapan --
        /// <summary>
        /// Wujudkan subtitle terpilih lalu kembalikan cara membakarnya.
        /// Tepat satu sumber dipakai: `externalPath` bila diisi, kalau tidak `track`.
        /// </summary>
        public static BurnPlan PrepareBurn(FFmpegTools tools, string videoPath,
                                           string workDir, SubtitleTrack track = null,
                                           string externalPath = "",
                                           SubtitleStyle style = null, int slot = 0)
        {
            Directory.CreateDirectory(workDir);

            if (!string.IsNullOrEmpty(externalPath))
                return PlanFromFile(externalPath, workDir, style, slot);
            if (track == null)
                throw new SubtitleException("Tidak ada subtitle yang dipilih.");
            if (track.IsImage)
                // Tidak ada yang perlu diekstrak: overlay membaca stream-nya
                // langsung dari masukan.
                return new BurnPlan
                {
                    Kind = BurnKind.Image,
                    StreamIndex = track.StreamIndex,
                    WorkDir = workDir,
                    SourceLabel = "trek " + track.Label,
                };
            if (!track.IsText)
                throw new SubtitleException(
                    "Jenis subtitle '" + track.Codec + "' tidak bisa dibakar.");
            return PlanFromEmbedded(tools, videoPath, track, workDir, style, slot);
        }

        /// <summary>Nama berkas tanpa satu pun karakter yang bisa disalahbaca pengurai.</summary>
        private static string SafeName(int slot, string ext)
            => "sub" + slot.ToString("0000", CultureInfo.InvariantCulture) + ext;

        private static BurnPlan PlanFromFile(string path, string workDir,
                                             SubtitleStyle style, int slot)
        {
            if (!File.Exists(path))
                throw new SubtitleException(
                    "Berkas subtitle tidak ditemukan:" + Environment.NewLine + path);
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (Array.IndexOf(SidecarExtensions, ext) < 0)
                throw new SubtitleException(
                    "Format subtitle '" + ext + "' tidak dikenali.");

            string local = Path.Combine(workDir, SafeName(slot, ext));
            try
            {
                File.Copy(path, local, true);
            }
            catch (Exception ex)
            {
                throw new SubtitleException(
                    "Gagal menyalin berkas subtitle:" + Environment.NewLine + ex.Message);
            }
            if (new FileInfo(local).Length == 0)
                throw new SubtitleException(
                    "Berkas subtitle kosong:" + Environment.NewLine + path);

            return new BurnPlan
            {
                Kind = BurnKind.Text,
                FilterArg = TextFilter(Path.GetFileName(local), style),
                WorkDir = workDir,
                SourceLabel = Path.GetFileName(path),
            };
        }

        /// <summary>
        /// Tarik satu trek teks tertanam keluar jadi .ass di folder kerja.
        ///
        /// Diekstrak, bukan mengarahkan libass ke videonya sendiri, dengan
        /// sengaja: <c>subtitles=movie.mkv:si=2</c> membuat libass membuka dan
        /// mengindeks SELURUH video untuk kedua kalinya, yang pada berkas
        /// multi-GB memakan detik per proses dan membacanya ulang dari disk.
        /// Ekstraksinya satu lintasan murah dan hasilnya mungil.
        ///
        /// .ass jadi sasaran ekstraksi bahkan untuk masukan SRT karena itu
        /// format asli libass, sehingga tidak ada yang hilang di jalan masuk.
        /// </summary>
        private static BurnPlan PlanFromEmbedded(FFmpegTools tools, string videoPath,
                                                 SubtitleTrack track, string workDir,
                                                 SubtitleStyle style, int slot)
        {
            string local = Path.Combine(workDir, SafeName(slot, ".ass"));
            var cmd = new[]
            {
                tools.FFmpeg, "-hide_banner", "-y", "-i", videoPath,
                "-map", "0:s:" + track.StreamIndex.ToString(CultureInfo.InvariantCulture),
                "-c:s", "ass", local,
            };
            var res = Shell.RunCapture(cmd, ProbeTimeout * 5);

            if (res.ExitCode != 0 || !File.Exists(local)
                || new FileInfo(local).Length == 0)
            {
                string[] lines = (res.StdErr ?? "").Trim()
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string detail = "(tidak ada pesan)";
                if (lines.Length > 0)
                {
                    int start = Math.Max(0, lines.Length - 3);
                    detail = string.Join(Environment.NewLine,
                        new List<string>(lines).GetRange(start, lines.Length - start));
                }
                throw new SubtitleException(
                    "Gagal mengeluarkan subtitle dari video."
                    + Environment.NewLine + Environment.NewLine + detail);
            }

            return new BurnPlan
            {
                Kind = BurnKind.Text,
                FilterArg = TextFilter(Path.GetFileName(local), style),
                WorkDir = workDir,
                SourceLabel = "trek " + track.Label,
            };
        }

        /// <summary>
        /// <c>subtitles=sub0001.ass</c> berikut gayanya, kalau pengguna memintanya.
        ///
        /// `localName` sudah dipastikan nama ASCII telanjang saat dibuat, jadi
        /// tidak butuh escape sama sekali - dan justru itulah seluruh alasan
        /// berkasnya disalin ke sini.
        /// </summary>
        private static string TextFilter(string localName, SubtitleStyle style)
        {
            string arg = "subtitles=" + localName;
            if (style != null && style.Enabled)
                // Tanda kutip tunggal menjaga koma di dalam force_style supaya
                // tidak dibaca sebagai pemisah filter.
                arg += ":force_style='" + style.ForceStyle() + "'";
            return arg;
        }
    }

    /// <summary>Satu stream subtitle di dalam berkas video.</summary>
    public class SubtitleTrack
    {
        /// <summary>
        /// Indeks DI ANTARA stream subtitle (N pada <c>0:s:N</c>), bukan indeks
        /// stream absolut - karena itulah yang diminta -map maupun opsi
        /// <c>si</c> pada filter <c>subtitles</c>.
        /// </summary>
        public int StreamIndex;
        public string Codec = "";
        public string Language = "";
        public string Title = "";
        public bool Forced;
        public bool Default;

        public bool IsText => Subtitles.TextCodecs.Contains(Codec ?? "");
        public bool IsImage => Subtitles.ImageCodecs.Contains(Codec ?? "");
        public bool Burnable => IsText || IsImage;

        /// <summary>Yang ditampilkan di daftar pilihan.</summary>
        public string Label
        {
            get
            {
                var bits = new List<string> { "#" + (StreamIndex + 1) };
                string name = Subtitles.LanguageName(Language);
                if (!string.IsNullOrEmpty(name)) bits.Add(name);

                string title = (Title ?? "").Trim();
                // Kode bahasa dan judul trek sangat sering menyatakan hal yang
                // sama, dan mengulanginya hanya membuat daftarnya melebar.
                if (title.Length > 0
                    && !title.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && !title.Equals(Language, StringComparison.OrdinalIgnoreCase))
                    bits.Add(title);

                var marks = new List<string>();
                if (Default) marks.Add("bawaan");
                if (Forced) marks.Add("paksa");
                if (IsImage) marks.Add("gambar");

                string text = string.Join(" - ", bits);
                if (marks.Count > 0) text += " (" + string.Join(", ", marks) + ")";
                return text;
            }
        }
    }

    /// <summary>
    /// Penyesuaian tampilan yang diterapkan ke subtitle teks. Hanya dipakai
    /// saat `Enabled`; kalau tidak, berkas .ass mempertahankan gayanya sendiri,
    /// yang hampir selalu memang yang dimaksud pembuat rilisnya.
    /// </summary>
    public class SubtitleStyle
    {
        public bool Enabled;
        public string Font = "Arial";
        public int Size = 24;
        public string Primary = "#FFFFFF";       // isian
        public string OutlineColor = "#000000";
        public double Outline = 2.0;
        public double Shadow = 0.0;
        public bool Bold;
        public int MarginV = 20;                 // jarak dari tepi bawah, dalam poin

        /// <summary>
        /// Susun string force_style milik libass.
        ///
        /// Warna di .ass berformat &amp;HAABBGGRR - alfa dulu, lalu RGB
        /// TERBALIK. Menulisnya sebagai RRGGBB adalah cara klasik berakhir
        /// dengan teks biru yang seharusnya merah.
        /// </summary>
        public string ForceStyle()
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Join(",", new[]
            {
                "FontName=" + Font,
                "FontSize=" + Size.ToString(inv),
                "PrimaryColour=" + AssColour(Primary),
                "OutlineColour=" + AssColour(OutlineColor),
                "BorderStyle=1",
                "Outline=" + Outline.ToString("0.##", inv),
                "Shadow=" + Shadow.ToString("0.##", inv),
                "Bold=" + (Bold ? "1" : "0"),
                "Alignment=2",
                "MarginV=" + MarginV.ToString(inv),
            });
        }

        /// <summary>"#FF8800" -> "&amp;H000088FF" (opak, urutan BGR).</summary>
        public static string AssColour(string hexRgb)
        {
            string text = (hexRgb ?? "").Trim().TrimStart('#');
            if (text.Length != 6) return "&H00FFFFFF";
            int r, g, b;
            if (!int.TryParse(text.Substring(0, 2), NumberStyles.HexNumber,
                              CultureInfo.InvariantCulture, out r)
                || !int.TryParse(text.Substring(2, 2), NumberStyles.HexNumber,
                                 CultureInfo.InvariantCulture, out g)
                || !int.TryParse(text.Substring(4, 2), NumberStyles.HexNumber,
                                 CultureInfo.InvariantCulture, out b))
                return "&H00FFFFFF";
            return "&H00" + b.ToString("X2") + g.ToString("X2") + r.ToString("X2");
        }

        public SubtitleStyle Clone() => (SubtitleStyle)MemberwiseClone();
    }

    public enum BurnKind { Text, Image }

    /// <summary>
    /// Semua yang dibutuhkan untuk membakar satu subtitle ke satu video.
    ///
    /// `FilterArg` adalah string filter ffmpeg yang lengkap. Untuk subtitle
    /// teks ia menyebut berkas yang tinggal di `WorkDir`, dan ffmpeg HARUS
    /// dijalankan dengan folder itu sebagai direktori kerjanya - lihat
    /// keterangan kelas Subtitles.
    /// </summary>
    public class BurnPlan
    {
        public BurnKind Kind = BurnKind.Text;
        public string FilterArg = "";
        public int StreamIndex;
        public string WorkDir = "";
        public string SourceLabel = "";
    }
}
