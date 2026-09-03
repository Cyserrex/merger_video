using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;

namespace VideoMerger.Core
{
    public class FFmpegTools
    {
        public string FFmpeg = "";
        public string FFprobe = "";
        public string Version = "";
        public string Source = "";

        public bool Ok => !string.IsNullOrEmpty(FFmpeg) && !string.IsNullOrEmpty(FFprobe);

        /// <summary>"8.1.1-full_build-www.gyan.dev" -> "8.1.1".</summary>
        public string ShortVersion
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Version)) return "?";
                int dash = Version.IndexOf('-');
                return dash > 0 ? Version.Substring(0, dash) : Version;
            }
        }
    }

    /// <summary>
    /// Menemukan ffmpeg.exe / ffprobe.exe di komputer pengguna.
    ///
    /// Build lengkap ffmpeg.exe saja berukuran ~217 MB, sehingga membundelnya
    /// akan mengubah aplikasi 2 MB menjadi unduhan ratusan MB. Karena itu
    /// aplikasi MENCARI ffmpeg, berurutan:
    ///
    ///   1. folder yang dipilih pengguna di Pengaturan
    ///   2. di sebelah exe, atau subfolder ffmpeg\bin (mode portabel)
    ///   3. PATH
    ///   4. lokasi pemasangan umum (winget, chocolatey, scoop, C:\ffmpeg)
    ///
    /// Kalau tidak ada yang cocok, tampilan menawarkan mengunduhnya sekali.
    /// </summary>
    public static class FFmpegLocator
    {
        public const string FFmpegName = "ffmpeg.exe";
        public const string FFprobeName = "ffprobe.exe";

        public const string DownloadUrl =
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        /// <summary>
        /// Urutan folder yang dicari, dari yang paling diutamakan.
        ///
        /// Publik karena urutannya adalah PERILAKU, bukan detail: seluruh cara
        /// aplikasi memperbarui FFmpeg bergantung pada salinannya sendiri
        /// dicari lebih dulu daripada winget/chocolatey/scoop. Kalau urutan itu
        /// bergeser, pembaruan akan tampak berhasil lalu diam-diam tidak
        /// terpakai - jadi ada tesnya.
        /// </summary>
        public static IList<KeyValuePair<string, string>> SearchOrder(
            string manualDir = "")
        {
            return new List<KeyValuePair<string, string>>(CandidateDirs(manualDir));
        }

        private static IEnumerable<KeyValuePair<string, string>> CandidateDirs(
            string manualDir)
        {
            var list = new List<KeyValuePair<string, string>>();
            void Add(string label, string dir)
            {
                if (!string.IsNullOrEmpty(dir))
                    list.Add(new KeyValuePair<string, string>(label, dir));
            }

            if (!string.IsNullOrEmpty(manualDir))
            {
                Add("pengaturan", manualDir);
                Add("pengaturan", Path.Combine(manualDir, "bin"));
            }

            string here = Paths.AppDir();
            Add("folder aplikasi", here);
            Add("folder aplikasi", Path.Combine(here, "ffmpeg"));
            Add("folder aplikasi", Path.Combine(here, "ffmpeg", "bin"));
            Add("folder aplikasi", Path.Combine(here, "bin"));

            string appdata = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
            string profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

            // Salinan milik aplikasi sendiri, dan sengaja diletakkan SEBELUM
            // winget/chocolatey/scoop: begitu aplikasi mengunduh versi yang
            // lebih baru, versi itulah yang dipakai - tanpa pernah menyentuh
            // pemasangan milik alat lain.
            Add("unduhan aplikasi", Path.Combine(appdata, AppInfo.Id, "ffmpeg", "bin"));
            Add("terpasang di sistem", @"C:\ffmpeg\bin");
            Add("terpasang di sistem", Path.Combine(programFiles, "ffmpeg", "bin"));
            Add("terpasang di sistem", Path.Combine(profile, "scoop", "shims"));
            Add("terpasang di sistem", @"C:\ProgramData\chocolatey\bin");

            // winget menyimpannya di folder bernomor versi; ditelusuri
            // daripada dipatok, supaya pembaruan ffmpeg tidak memutusnya.
            try
            {
                string wingetRoot = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(wingetRoot))
                {
                    var pkgs = new List<string>(
                        Directory.GetDirectories(wingetRoot, "Gyan.FFmpeg*"));
                    pkgs.Sort(StringComparer.OrdinalIgnoreCase);
                    pkgs.Reverse();
                    foreach (var pkg in pkgs)
                        foreach (var build in Directory.GetDirectories(pkg, "ffmpeg-*"))
                            Add("winget", Path.Combine(build, "bin"));
                }
            }
            catch (Exception) { }

            return list;
        }

        private static bool PairIn(string folder, out string ffmpeg, out string ffprobe)
        {
            ffmpeg = ffprobe = null;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
            string a = Path.Combine(folder, FFmpegName);
            string b = Path.Combine(folder, FFprobeName);
            if (!File.Exists(a) || !File.Exists(b)) return false;
            ffmpeg = a; ffprobe = b;
            return true;
        }

        /// <summary>Jalankan alatnya dan kembalikan versinya, atau "" kalau tidak jalan.</summary>
        private static string ProbeVersion(string toolPath)
        {
            try
            {
                var res = Shell.RunCapture(
                    new[] { toolPath, "-hide_banner", "-version" }, 15);
                if (res.ExitCode != 0) return "";
                string text = !string.IsNullOrWhiteSpace(res.StdOut)
                    ? res.StdOut : res.StdErr;
                string first = (text ?? "").Split('\n')[0].Trim();
                if (first.Length == 0) return "";
                var m = Regex.Match(first, @"ff(?:mpeg|probe) version (\S+)");
                return m.Success ? m.Groups[1].Value : first;
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static FFmpegTools Locate(string manualDir = "")
        {
            foreach (var pair in CandidateDirs(manualDir))
            {
                string ffmpeg, ffprobe;
                if (!PairIn(pair.Value, out ffmpeg, out ffprobe)) continue;

                var tools = new FFmpegTools
                {
                    FFmpeg = ffmpeg,
                    FFprobe = ffprobe,
                    Source = pair.Key,
                };
                tools.Version = ProbeVersion(ffmpeg);
                // ffprobe harus ikut jalan. Folder berisi ffmpeg.exe yang
                // sehat di samping ffprobe.exe yang terpotong atau diblokir
                // dulu diterima begitu saja, dan akibatnya SETIAP berkas
                // dilaporkan rusak.
                if (!string.IsNullOrEmpty(tools.Version)
                    && !string.IsNullOrEmpty(ProbeVersion(ffprobe)))
                    return tools;
            }

            string onPathFFmpeg = Which(FFmpegName);
            string onPathFFprobe = Which(FFprobeName);
            if (onPathFFmpeg != null && onPathFFprobe != null)
            {
                var tools = new FFmpegTools
                {
                    FFmpeg = onPathFFmpeg,
                    FFprobe = onPathFFprobe,
                    Source = "PATH",
                };
                tools.Version = ProbeVersion(onPathFFmpeg);
                if (!string.IsNullOrEmpty(tools.Version)
                    && !string.IsNullOrEmpty(ProbeVersion(onPathFFprobe)))
                    return tools;
            }
            return null;
        }

        private static string Which(string exeName)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in path.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string candidate;
                    try { candidate = Path.Combine(dir.Trim(), exeName); }
                    catch (ArgumentException) { continue; }   // PATH berisi karakter ilegal
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (Exception) { }
            return null;
        }

        public static string InstallDir()
        {
            return Path.Combine(Paths.ConfigDir(), "ffmpeg");
        }

        /// <summary>
        /// Unduh build essentials gyan.dev lalu keluarkan ffmpeg/ffprobe saja.
        /// `progress(sudah, total, pesan)` dipanggil selama berjalan;
        /// `cancel()` yang bernilai true membatalkan.
        /// </summary>
        public static FFmpegTools DownloadAndInstall(
            Action<long, long, string> progress = null,
            Func<bool> cancel = null)
        {
            string target = InstallDir();
            string binDir = Path.Combine(target, "bin");
            string tmpZip = Path.Combine(Path.GetTempPath(), "vmerge_ffmpeg.zip");

            void Report(long done, long total, string msg)
            {
                if (progress != null) progress(done, total, msg);
            }

            try
            {
                Directory.CreateDirectory(binDir);
                Report(0, 0, "Menghubungi server gyan.dev...");

                // TLS 1.2 harus diminta eksplisit: pada .NET Framework 4.8 di
                // Windows lama nilai bawaannya masih bisa SSL3/TLS1, dan
                // gyan.dev menolaknya - unduhan gagal tanpa penjelasan.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                var request = (HttpWebRequest)WebRequest.Create(DownloadUrl);
                request.UserAgent = "vmerge/" + AppInfo.Version;
                request.Timeout = 60000;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(tmpZip, FileMode.Create,
                                                   FileAccess.Write, FileShare.None))
                {
                    long total = response.ContentLength;
                    long done = 0;
                    var buffer = new byte[256 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (cancel != null && cancel()) return null;
                        output.Write(buffer, 0, read);
                        done += read;
                        Report(done, total, "Mengunduh FFmpeg...");
                    }
                }

                Report(0, 0, "Mengekstrak...");
                using (var zip = ZipFile.OpenRead(tmpZip))
                {
                    bool any = false;
                    foreach (var entry in zip.Entries)
                    {
                        string name = Path.GetFileName(entry.FullName);
                        if (!string.Equals(name, FFmpegName, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(name, FFprobeName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        entry.ExtractToFile(Path.Combine(binDir, name), true);
                        any = true;
                    }
                    if (!any) return null;
                }
                return Locate(target);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); }
                catch (IOException) { }
            }
        }
    }
}
