using System;
using System.Globalization;
using System.IO;
using System.Net;

namespace VideoMerger.Core
{
    public class UpdateCheck
    {
        public bool Checked;             // pemeriksaan benar-benar terjadi
        public string Latest = "";
        public string Installed = "";
        public bool UpdateAvailable;
        public bool Managed;             // true = pemasangan ini milik aplikasi
        public string Error = "";
    }

    /// <summary>
    /// Memeriksa dan memasang FFmpeg versi baru.
    ///
    /// Satu aturan yang menentukan seluruh perilaku di sini: aplikasi hanya
    /// memperbarui FFmpeg yang DIPASANGNYA SENDIRI, di
    /// %APPDATA%\vmerge\ffmpeg. FFmpeg yang datang dari winget, chocolatey,
    /// scoop, atau yang ditaruh sendiri oleh pengguna di PATH adalah milik
    /// alat lain - menimpanya berarti diam-diam mengambil alih pemasangan yang
    /// tidak kita buat, dan pembaruan berikutnya dari alat aslinya akan
    /// bertabrakan. Untuk kasus itu aplikasi hanya memberitahu, tidak bertindak.
    /// </summary>
    public static class FFmpegUpdater
    {
        /// <summary>Berkas teks satu baris berisi nomor versi rilis terakhir.</summary>
        public const string VersionUrl =
            "https://www.gyan.dev/ffmpeg/builds/release-version";

        /// <summary>Apakah pemasangan ini yang dibuat aplikasi (dan karenanya boleh ditimpa)?</summary>
        public static bool IsManagedByApp(FFmpegTools tools)
        {
            if (tools == null || string.IsNullOrEmpty(tools.FFmpeg)) return false;
            try
            {
                // Pemisah folder di ujung itu wajib. Tanpa itu perbandingan
                // awalan ikut mencocoki folder TETANGGA yang namanya kebetulan
                // berawalan sama: "...\vmerge\ffmpeg-manual\bin\ffmpeg.exe"
                // akan dianggap milik aplikasi, lalu ditawari pembaruan
                // otomatis yang sebenarnya memasang ke folder lain.
                string own = Path.GetFullPath(FFmpegLocator.InstallDir());
                if (!own.EndsWith(Path.DirectorySeparatorChar.ToString(),
                                  StringComparison.Ordinal))
                    own += Path.DirectorySeparatorChar;
                string here = Path.GetFullPath(tools.FFmpeg);
                return here.StartsWith(own, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Bandingkan dua versi bergaya "8.1.1" secara numerik per ruas.
        /// Ruas non-angka diabaikan, sehingga "8.1.1-full_build-www.gyan.dev"
        /// dibandingkan sebagai 8.1.1 - dan itu penting, karena versi yang
        /// terpasang SELALU membawa akhiran build sedangkan yang di server
        /// tidak. Membandingkannya sebagai teks membuat setiap pemeriksaan
        /// melaporkan "ada versi baru" selamanya.
        /// </summary>
        public static int CompareVersions(string a, string b)
        {
            int[] left = Parse(a), right = Parse(b);
            int len = Math.Max(left.Length, right.Length);
            for (int i = 0; i < len; i++)
            {
                int x = i < left.Length ? left[i] : 0;
                int y = i < right.Length ? right[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        private static int[] Parse(string version)
        {
            string head = (version ?? "").Trim();
            int cut = head.IndexOf('-');
            if (cut > 0) head = head.Substring(0, cut);

            string[] bits = head.Split('.');
            var numbers = new int[bits.Length];
            for (int i = 0; i < bits.Length; i++)
            {
                int value;
                // Ruas seperti "1n" (build git) dibaca sebagai angka di depannya.
                var digits = new System.Text.StringBuilder();
                foreach (char c in bits[i])
                {
                    if (!char.IsDigit(c)) break;
                    digits.Append(c);
                }
                int.TryParse(digits.ToString(), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out value);
                numbers[i] = value;
            }
            return numbers;
        }

        /// <summary>Tanya server versi terbaru. Tidak pernah melempar.</summary>
        public static UpdateCheck Check(FFmpegTools tools, int timeoutSeconds = 15)
        {
            var result = new UpdateCheck
            {
                Installed = tools != null ? tools.ShortVersion : "",
                Managed = IsManagedByApp(tools),
            };
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(VersionUrl);
                request.UserAgent = "vmerge/" + AppInfo.Version;
                request.Timeout = timeoutSeconds * 1000;
                request.ReadWriteTimeout = timeoutSeconds * 1000;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    // Berkasnya satu baris pendek; batasi bacaannya supaya
                    // halaman galat HTML tidak terbaca sebagai "versi".
                    var buffer = new char[64];
                    int read = reader.Read(buffer, 0, buffer.Length);
                    result.Latest = new string(buffer, 0, Math.Max(0, read)).Trim();
                }

                if (string.IsNullOrEmpty(result.Latest)
                    || result.Latest.IndexOf('<') >= 0)
                {
                    result.Error = "Jawaban server tidak dikenali.";
                    return result;
                }

                result.Checked = true;
                result.UpdateAvailable =
                    !string.IsNullOrEmpty(result.Installed)
                    && CompareVersions(result.Installed, result.Latest) < 0;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Unduh dan pasang versi terbaru ke folder milik aplikasi.
        /// Mengembalikan alat yang baru, atau null kalau gagal.
        /// </summary>
        public static FFmpegTools Update(Action<long, long, string> progress = null,
                                         Func<bool> cancel = null)
        {
            // Sengaja memakai jalur pemasangan yang sama: DownloadAndInstall
            // menimpa kedua exe di tempatnya. Berkas yang sedang dipakai tidak
            // bisa ditimpa di Windows, jadi pemanggilnya wajib memastikan tidak
            // ada pekerjaan yang sedang berjalan.
            return FFmpegLocator.DownloadAndInstall(progress, cancel);
        }

        // ------------------------------------------------ penjadwalan cek --
        private const string KeyLastCheck = "ffmpeg_last_check";

        /// <summary>
        /// Sudah waktunya memeriksa lagi? Dibatasi sekali seminggu supaya
        /// membuka aplikasi tidak berarti satu permintaan jaringan setiap kali -
        /// di komputer kantor yang lambat itu terasa, dan FFmpeg tidak rilis
        /// setiap hari.
        /// </summary>
        public static bool DueForCheck(AppSettings settings, int days = 7)
        {
            if (settings == null || !settings.GetBool("ffmpeg_auto_check")) return false;
            string raw = settings[KeyLastCheck];
            if (string.IsNullOrWhiteSpace(raw)) return true;

            DateTime last;
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out last))
                return true;
            // Jam sistem yang mundur (baterai CMOS mati di PC lama) membuat
            // selisihnya negatif; itu tetap berarti "periksa lagi".
            return Math.Abs((DateTime.Now - last).TotalDays) >= days;
        }

        public static void MarkChecked(AppSettings settings)
        {
            if (settings == null) return;
            settings.Set(KeyLastCheck,
                         DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            settings.Save();
        }
    }
}
