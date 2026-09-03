using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VideoMerger.Core
{
    /// <summary>
    /// Preferensi pengguna, disimpan sebagai key=value di
    /// %APPDATA%\vmerge\settings.ini.
    ///
    /// Bukan JSON supaya tidak ada pustaka yang perlu ikut, dan karena bentuk
    /// key=value tidak punya mode gagal "satu koma salah, seluruh berkas
    /// ditolak" - baris yang rusak dibuang satu per satu.
    ///
    /// Setiap pembacaan bersikap defensif: berkas yang rusak atau disunting
    /// tangan tidak boleh membuat aplikasi gagal dibuka. Memeriksa bahwa
    /// bentuknya benar saja tidak cukup - nilai seperti crf=tinggi dulu
    /// sampai ke pembangun jendela dan menggagalkannya sebelum apa pun
    /// tampil, tanpa cara bagi penggunanya untuk tahu kenapa. Apa pun yang
    /// tidak bisa dikonversi dibuang dan nilai bawaannya tetap berlaku.
    /// </summary>
    public class AppSettings
    {
        private static readonly Dictionary<string, string> Defaults =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "last_input_dir", "" },
                { "last_output_dir", "" },
                { "recent_input_dirs", "" },
                { "recursive", "false" },
                { "sort_key", "Name" },
                { "sort_desc", "false" },
                { "merge_mode", "Auto" },
                { "output_container", ".mp4" },
                { "crf", "23" },
                { "preset", "veryfast" },
                { "hwaccel_encoder", "" },
                { "faststart", "true" },
                { "window_width", "1120" },
                { "window_height", "760" },
                { "window_maximized", "false" },
                { "ffmpeg_dir", "" },
                { "keep_log_lines", "500" },
                { "active_tab", "0" },

                // -- pembaruan FFmpeg ------------------------------------------
                { "ffmpeg_auto_check", "true" },
                { "ffmpeg_last_check", "" },

                // -- pemilihan encoder otomatis --------------------------------
                { "encoder_auto", "true" },
                { "encoder_bench_fingerprint", "" },
                { "encoder_bench_best", "" },
                { "encoder_bench_detail", "" },

                // -- kinerja ---------------------------------------------------
                // Menjalankan ffmpeg di bawah prioritas normal nyaris tidak
                // memperlambat proses yang berjalan sendirian, tetapi membuat
                // perbedaan besar saat penggunanya tetap memakai laptopnya:
                // tanpa ini seluruh mesin tersendat selama render.
                { "background_priority", "true" },
                // Animasi WPF dimatikan pada mesin tanpa akselerasi grafis.
                { "reduce_effects", "auto" },

                // -- hardsub -------------------------------------------------
                { "hardsub_input_dir", "" },
                { "hardsub_output_dir", "" },
                { "recent_hardsub_dirs", "" },
                { "hardsub_suffix", " - hardsub" },
                { "hardsub_container", ".mp4" },
                // Lebih rendah dari bawaan penggabungan dengan sengaja: tepi
                // huruf yang tajam justru yang paling dulu dikaburkan
                // kompresi, dan subtitle yang tidak terbaca meniadakan seluruh
                // alasan membakarnya.
                { "hardsub_crf", "20" },
                { "hardsub_copy_audio", "true" },
                { "sub_style_enabled", "false" },
                { "sub_font", "Arial" },
                { "sub_size", "24" },
                { "sub_primary", "#FFFFFF" },
                { "sub_outline_color", "#000000" },
                { "sub_outline", "2" },
                { "sub_bold", "false" },
                { "sub_margin_v", "20" },
            };

        private readonly Dictionary<string, string> _data =
            new Dictionary<string, string>(Defaults, StringComparer.OrdinalIgnoreCase);

        public static string ConfigPath()
            => Path.Combine(Paths.ConfigDir(), "settings.ini");

        // -- akses -----------------------------------------------------------
        public string this[string key]
        {
            get
            {
                string value;
                if (_data.TryGetValue(key, out value)) return value;
                return Defaults.TryGetValue(key, out value) ? value : "";
            }
            set { _data[key] = value ?? ""; }
        }

        public string GetString(string key) => this[key];

        public int GetInt(string key, int low = int.MinValue, int high = int.MaxValue)
        {
            int value;
            if (!int.TryParse(this[key], NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out value))
                int.TryParse(Defaults[key], NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out value);
            return Math.Max(low, Math.Min(high, value));
        }

        public double GetDouble(string key)
        {
            double value;
            if (double.TryParse(this[key], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out value))
                return value;
            double.TryParse(Defaults[key], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out value);
            return value;
        }

        public bool GetBool(string key)
        {
            string text = (this[key] ?? "").Trim().ToLowerInvariant();
            return text == "1" || text == "true" || text == "yes" || text == "ya";
        }

        public void Set(string key, object value)
        {
            if (value is bool) _data[key] = ((bool)value) ? "true" : "false";
            else if (value is double)
                _data[key] = ((double)value).ToString("0.####",
                                                      CultureInfo.InvariantCulture);
            else if (value is IFormattable)
                _data[key] = ((IFormattable)value).ToString(null,
                                                            CultureInfo.InvariantCulture);
            else _data[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        // -- pembantu bertipe -------------------------------------------------
        public SortBy SortBy
        {
            get
            {
                SortBy key;
                return Enum.TryParse(this["sort_key"], true, out key)
                    ? key : Core.SortBy.Name;
            }
            set { Set("sort_key", value.ToString()); }
        }

        public MergeMode MergeMode
        {
            get
            {
                MergeMode mode;
                return Enum.TryParse(this["merge_mode"], true, out mode)
                    ? mode : Core.MergeMode.Auto;
            }
            set { Set("merge_mode", value.ToString()); }
        }

        public SubtitleStyle SubtitleStyle
        {
            get
            {
                return new SubtitleStyle
                {
                    Enabled = GetBool("sub_style_enabled"),
                    Font = string.IsNullOrWhiteSpace(this["sub_font"])
                        ? "Arial" : this["sub_font"],
                    Size = GetInt("sub_size", 8, 200),
                    Primary = this["sub_primary"],
                    OutlineColor = this["sub_outline_color"],
                    Outline = GetDouble("sub_outline"),
                    Bold = GetBool("sub_bold"),
                    MarginV = GetInt("sub_margin_v", 0, 400),
                };
            }
        }

        // -- penyimpanan ------------------------------------------------------
        public static AppSettings Load()
        {
            var settings = new AppSettings();
            string path = ConfigPath();
            string[] lines;
            try
            {
                if (!File.Exists(path)) return settings;
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (Exception)
            {
                return settings;      // tidak terbaca: bawaannya tetap berlaku
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                // Kunci yang tidak dikenal diabaikan, bukan disimpan: berkas
                // dari versi yang lebih baru tidak boleh menyeret setelan yang
                // tidak dimengerti versi ini.
                if (Defaults.ContainsKey(key)) settings._data[key] = Unescape(value);
            }

            // Nilai yang tipenya benar tetapi di luar jangkauan sama fatalnya
            // di hilir, jadi yang langsung disuapkan ke widget dijepit di sini.
            settings.Set("crf", settings.GetInt("crf", 0, 51));
            settings.Set("hardsub_crf", settings.GetInt("hardsub_crf", 0, 51));
            settings.Set("keep_log_lines",
                         settings.GetInt("keep_log_lines", 50, 100000));
            return settings;
        }

        /// <summary>Penulisan atomik, supaya crash saat menyimpan tidak merusak setelan.</summary>
        public bool Save()
        {
            try
            {
                string dir = Paths.ConfigDir();
                Directory.CreateDirectory(dir);
                string target = ConfigPath();
                string tmp = Path.Combine(dir, "settings.tmp");

                var sb = new StringBuilder();
                sb.Append("# ").Append(AppInfo.Name).Append(' ')
                  .Append(AppInfo.Version).Append(Environment.NewLine);
                foreach (var pair in _data)
                    sb.Append(pair.Key).Append('=').Append(Escape(pair.Value))
                      .Append(Environment.NewLine);

                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Nilai bisa memuat baris baru hanya lewat suntingan tangan, tetapi
        // satu saja akan memotong berkas jadi dua di pembacaan berikutnya.
        private static string Escape(string value)
            => (value ?? "").Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

        private static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0) return value;
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length) { sb.Append(value[i]); continue; }
                char next = value[++i];
                if (next == 'n') sb.Append('\n');
                else if (next == 'r') sb.Append('\r');
                else if (next == '\\') sb.Append('\\');
                else { sb.Append('\\'); sb.Append(next); }
            }
            return sb.ToString();
        }
    }
}
