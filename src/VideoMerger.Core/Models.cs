using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VideoMerger.Core
{
    /// <summary>
    /// Kontrak data bersama. Tidak menyentuh ffmpeg maupun UI, supaya pemindai,
    /// penggabung, dan tampilan sepakat pada bentuk yang sama.
    /// </summary>
    public static class AppInfo
    {
        public const string Name = "Video Merger";
        public const string Id = "vmerge";
        public const string Version = "1.2.0";

        /// <summary>
        /// Ekstensi yang dianggap video saat memindai folder. Sengaja luas:
        /// ekspor CCTV (.dav, .264) dan format camcorder (.mts, .m2ts) ikut.
        /// </summary>
        public static readonly string[] VideoExtensions =
        {
            ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
            ".mpg", ".mpeg", ".m2v", ".ts", ".m2ts", ".mts", ".vob", ".3gp",
            ".3g2", ".asf", ".rm", ".rmvb", ".ogv", ".mxf", ".dav", ".264",
            ".h264", ".hevc", ".divx", ".f4v", ".m4s", ".dv",
        };
    }

    public enum SortBy
    {
        Name,          // urutan alami, seperti Windows Explorer
        NamePlain,     // abjad murni, tanpa peduli besar-kecil huruf
        Modified,
        Created,
        Recorded,      // metadata -> nama berkas -> mtime, bertingkat
        MediaCreated,  // tag creation_time di dalam container
        NameTimestamp, // tanggal yang diurai dari nama berkas
        Duration,
        Size,
        Manual,        // pengguna menggeser baris sendiri; biarkan apa adanya
    }

    public enum MergeMode
    {
        Auto,      // pilih Copy kalau aman, kalau tidak Reencode
        Copy,      // concat demuxer + -c copy (hitungan detik, tanpa rugi)
        Reencode,  // seragamkan semua klip, lalu sambung
        Smart,     // encode ulang hanya klip yang berbeda, sisanya disalin
    }

    public enum Stage
    {
        Scanning, Probing, Normalizing, Merging, Finalizing,
        Done, Failed, Cancelled,
    }

    public static class Labels
    {
        public static string Of(SortBy key)
        {
            switch (key)
            {
                case SortBy.Name: return "Nama file (urutan alami)";
                case SortBy.NamePlain: return "Nama file (A-Z biasa)";
                case SortBy.Recorded: return "Tanggal rekam (otomatis)";
                case SortBy.Modified: return "Tanggal diubah";
                case SortBy.Created: return "Tanggal dibuat";
                case SortBy.MediaCreated: return "Tanggal rekam (metadata)";
                case SortBy.NameTimestamp: return "Tanggal dari nama file";
                case SortBy.Duration: return "Durasi";
                case SortBy.Size: return "Ukuran file";
                default: return "Urutan manual";
            }
        }

        public static string Of(MergeMode mode)
        {
            switch (mode)
            {
                case MergeMode.Copy: return "Cepat - tanpa encode ulang";
                case MergeMode.Reencode: return "Encode ulang semua";
                case MergeMode.Smart: return "Hemat - encode ulang yang beda saja";
                default: return "Otomatis (disarankan)";
            }
        }
    }

    /// <summary>Satu berkas kandidat berikut semua yang dilaporkan ffprobe.</summary>
    public class VideoFile
    {
        public string Path = "";
        public long Size;
        public DateTime Modified;
        public DateTime CreatedOn;

        // --- diisi oleh Prober ------------------------------------------
        public bool Probed;
        public bool Valid;
        public string Error = "";
        public double Duration;
        public double VideoDuration;      // durasi stream video itu sendiri
        public string FormatName = "";
        public DateTime? MediaCreated;

        public bool HasVideo;
        public string VCodec = "";
        public string VCodecTag = "";
        public int Width;
        public int Height;
        public string PixFmt = "";
        public string Sar = "";           // sample aspect ratio, mis. "1:1"
        public double Fps;                // avg_frame_rate, untuk tampilan saja
        public string VRate = "";         // r_frame_rate ternormalisasi
        public string VTimeBase = "";     // ternormalisasi, mis. "1/15360"
        public string FieldOrder = "";
        public string ColorRange = "";
        public string ColorSpace = "";
        public string VProfile = "";
        public int VLevel;
        public int Rotation;              // 0/90/180/270, dari display matrix
        public int VideoStreamCount;

        public bool HasAudio;
        public string ACodec = "";
        public string ACodecTag = "";
        public int SampleRate;
        public int Channels;
        public string ChannelLayout = "";
        public string AProfile = "";
        public string ATimeBase = "";
        public int AudioStreamCount;

        // --- keadaan tampilan -------------------------------------------
        public bool Selected = true;
        public DateTime? NameTimestamp;

        public string Name => System.IO.Path.GetFileName(Path);

        public string Resolution =>
            Width > 0 ? Width + "x" + Height : "-";

        /// <summary>
        /// Parameter yang harus sama antar klip supaya <c>-c copy</c> aman.
        ///
        /// Setiap field di sini dipilih karena perbedaannya TERUKUR merusak
        /// hasil sementara ffmpeg tetap keluar dengan kode 0 tanpa peringatan.
        /// Dua yang tidak kelihatan jelas:
        ///
        ///   VTimeBase  Dua klip yang identik dalam segala hal tetapi dimuxing
        ///              dengan timescale MP4 berbeda (1/15360 vs 1/30000)
        ///              menghasilkan video hampir dua kali lebih panjang,
        ///              separuhnya diputar setengah kecepatan.
        ///   VRate      30 fps disambung ke 29,97 fps berakibat sama. Nilainya
        ///              dibandingkan sebagai pecahan ternormalisasi supaya
        ///              30000/1001 dan 90000/3003 dihitung sama.
        ///   Rotation   Display matrix ponsel ada di container, bukan di
        ///              frame-nya. Menyambung klip 90 derajat ke klip tegak
        ///              hanya menyimpan matrix klip pertama, sehingga separuh
        ///              videonya jadi miring.
        ///
        /// VLevel sengaja TIDAK ikut: level 3.0 dan 4.1 menyambung dengan
        /// bersih, jadi menyertakannya hanya memaksa encode ulang percuma.
        /// </summary>
        public string CopySignature()
        {
            return string.Join("", new[]
            {
                VCodec, VCodecTag, VProfile,
                Width.ToString(CultureInfo.InvariantCulture),
                Height.ToString(CultureInfo.InvariantCulture),
                PixFmt, Sar, FieldOrder, ColorRange, ColorSpace,
                VTimeBase, VRate,
                Rotation.ToString(CultureInfo.InvariantCulture),
                VideoStreamCount.ToString(CultureInfo.InvariantCulture),
                ACodec, ACodecTag, AProfile,
                SampleRate.ToString(CultureInfo.InvariantCulture),
                Channels.ToString(CultureInfo.InvariantCulture),
                ChannelLayout, ATimeBase,
                AudioStreamCount.ToString(CultureInfo.InvariantCulture),
            });
        }

        /// <summary>Alasan yang bisa dibaca manusia kenapa dua klip tak bisa disalin.</summary>
        public List<string> SignatureDiff(VideoFile other)
        {
            var pairs = new List<Tuple<string, string, string>>
            {
                T("codec video", VCodec, other.VCodec),
                T("tag codec video", VCodecTag, other.VCodecTag),
                T("resolusi", Resolution, other.Resolution),
                T("pixel format", PixFmt, other.PixFmt),
                T("aspect ratio", Sar, other.Sar),
                T("profil video", VProfile, other.VProfile),
                T("frame rate", VRate, other.VRate),
                T("time base video", VTimeBase, other.VTimeBase),
                T("rotasi", Rotation.ToString(), other.Rotation.ToString()),
                T("urutan field", FieldOrder, other.FieldOrder),
                T("color range", ColorRange, other.ColorRange),
                T("color space", ColorSpace, other.ColorSpace),
                T("jumlah stream video", VideoStreamCount.ToString(),
                  other.VideoStreamCount.ToString()),
                T("codec audio", ACodec, other.ACodec),
                T("tag codec audio", ACodecTag, other.ACodecTag),
                T("profil audio", AProfile, other.AProfile),
                T("sample rate", SampleRate.ToString(), other.SampleRate.ToString()),
                T("jumlah channel", Channels.ToString(), other.Channels.ToString()),
                T("layout channel", ChannelLayout, other.ChannelLayout),
                T("time base audio", ATimeBase, other.ATimeBase),
                T("jumlah stream audio", AudioStreamCount.ToString(),
                  other.AudioStreamCount.ToString()),
            };

            var result = new List<string>();
            foreach (var p in pairs)
            {
                if (!string.Equals(p.Item2, p.Item3, StringComparison.Ordinal))
                    result.Add(p.Item1 + ": " + Show(p.Item2) + " vs " + Show(p.Item3));
            }
            return result;
        }

        private static Tuple<string, string, string> T(string a, string b, string c)
            => Tuple.Create(a, b ?? "", c ?? "");

        // "0" bermakna di sini ("0 stream audio"), jadi hanya string kosong
        // yang berubah jadi tanda hubung.
        private static string Show(string value)
            => string.IsNullOrEmpty(value) ? "-" : value;
    }

    /// <summary>Parameter seragam yang dituju setiap klip saat encode ulang.</summary>
    public class TargetSpec
    {
        public int Width = 1920;
        public int Height = 1080;
        public double Fps = 30.0;
        public string PixFmt = "yuv420p";
        public string VEncoder = "libx264";

        /// <summary>"main"/"high"/... supaya hasil encode cocok dengan sumbernya.</summary>
        public string VProfile = "";

        // Terukur pada sumber 1080p->720p: veryfast/CRF23 berjalan 8,4x
        // realtime (pekerjaan 8 jam selesai ~57 menit) untuk ~2,25 GB,
        // sedangkan medium 3,9x (~124 menit) dengan berkas sedikit lebih
        // besar. ultrafast justru jebakan: bitrate-nya membengkak jadi
        // 5798 kbps, hampir 9x veryfast.
        public int Crf = 23;
        public string Preset = "veryfast";
        public string AEncoder = "aac";
        public string ABitrate = "192k";
        public int SampleRate = 48000;
        public int Channels = 2;

        public string Describe() =>
            string.Format(CultureInfo.InvariantCulture,
                "{0}x{1} @ {2:0.##}fps, {3} crf{4}, {5} {6} {7}Hz",
                Width, Height, Fps, VEncoder, Crf, AEncoder, ABitrate, SampleRate);

        public TargetSpec Clone() => (TargetSpec)MemberwiseClone();
    }

    /// <summary>Semua yang dibutuhkan pekerja untuk menghasilkan berkas keluaran.</summary>
    public class MergeJob
    {
        public List<VideoFile> Files = new List<VideoFile>();
        public string OutputPath = "";
        public MergeMode Mode = MergeMode.Auto;
        public TargetSpec Target = new TargetSpec();
        public string HwaccelEncoder = "";   // "" = perangkat lunak
        public bool Overwrite = true;
        public bool Faststart = true;

        public double TotalDuration
        {
            get
            {
                double total = 0;
                foreach (var f in Files)
                    if (f.Duration > 0) total += f.Duration;
                return total;
            }
        }
    }

    /// <summary>Satu denyut kemajuan dari thread pekerja ke tampilan.</summary>
    public class Progress
    {
        public Stage Stage = Stage.Merging;
        public double Fraction;          // 0..1 keseluruhan
        public string Message = "";
        public int CurrentIndex;         // mulai 1, untuk tahap per berkas
        public int TotalItems;
        public double SecondsDone;
        public double SecondsTotal;
        public double Speed;             // pengali "speed=" dari ffmpeg
        public double EtaSeconds;
        public long OutputSize;

        public double Percent => Math.Max(0.0, Math.Min(100.0, Fraction * 100.0));
    }

    // ---------------------------------------------------------------- format --
    public static class Humanize
    {
        public static string Size(double bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = Math.Min((int)Math.Log(bytes, 1024), units.Length - 1);
            double value = bytes / Math.Pow(1024, i);
            return i == 0
                ? value.ToString("0", CultureInfo.InvariantCulture) + " B"
                : value.ToString("0.00", CultureInfo.InvariantCulture) + " " + units[i];
        }

        /// <summary>0 -> "00:00:00". Selalu HH:MM:SS supaya lebar kolom stabil.</summary>
        public static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || seconds <= 0) return "00:00:00";
            long total = (long)Math.Round(seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                total / 3600, (total % 3600) / 60, total % 60);
        }

        public static string Eta(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                return "-";
            long total = (long)Math.Round(seconds);
            if (total < 60) return total + " detik";
            if (total < 3600) return (total / 60) + " menit " + (total % 60) + " detik";
            return (total / 3600) + " jam " + ((total % 3600) / 60) + " menit";
        }
    }
}
