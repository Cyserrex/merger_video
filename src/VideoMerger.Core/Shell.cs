using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VideoMerger.Core
{
    public class CapturedResult
    {
        public int ExitCode = -1;
        public string StdOut = "";
        public string StdErr = "";
        public bool TimedOut;
    }

    /// <summary>
    /// Segala urusan proses anak. Semua ffmpeg/ffprobe dijalankan lewat sini
    /// supaya dua jebakan klasik hanya ditangani di satu tempat:
    ///
    ///   1. Jendela konsol hitam yang berkedip. Aplikasi ini memanggil ffprobe
    ///      sekali per berkas, jadi pada folder berisi 100 video itu berarti
    ///      100 kedipan. CreateNoWindow + UseShellExecute=false membuat
    ///      konsolnya tidak pernah dibuat sama sekali.
    ///   2. stdin yang diwariskan. Pada aplikasi WinExe, stdin yang diwariskan
    ///      bukan handle yang sah, dan ffmpeg yang membacanya bisa menggantung
    ///      selamanya. Karena itu stdin selalu diarahkan ulang.
    /// </summary>
    public static class Shell
    {
        /// <summary>
        /// ffmpeg menulis UTF-8 tanpa peduli code page konsol, jadi outputnya
        /// dibaca sebagai UTF-8 dan bukan sebagai ANSI mesin setempat.
        /// </summary>
        public static readonly Encoding PipeEncoding = new UTF8Encoding(false);

        public static ProcessStartInfo NewStartInfo(IList<string> cmd)
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd[0],
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = PipeEncoding,
                StandardErrorEncoding = PipeEncoding,
            };
            var args = new List<string>();
            for (int i = 1; i < cmd.Count; i++) args.Add(cmd[i]);
            psi.Arguments = BuildArguments(args);
            return psi;
        }

        /// <summary>
        /// Rangkai argumen jadi satu baris perintah dengan aturan
        /// CommandLineToArgvW.
        ///
        /// .NET Framework tidak punya ProcessStartInfo.ArgumentList (itu hanya
        /// ada di .NET Core), jadi pengutipannya harus dilakukan sendiri -
        /// dan ini bukan sekadar "bungkus dengan tanda kutip". Jalur folder
        /// Windows berakhiran backslash, sehingga <c>"D:\Video\"</c> justru
        /// membuat backslash-nya meng-escape tanda kutip penutup dan argumen
        /// berikutnya ikut tertelan. Setiap backslash tepat sebelum tanda
        /// kutip harus digandakan.
        /// </summary>
        public static string BuildArguments(IList<string> args)
        {
            var sb = new StringBuilder();
            foreach (string arg in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                AppendArgument(sb, arg ?? "");
            }
            return sb.ToString();
        }

        private static void AppendArgument(StringBuilder sb, string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                sb.Append(arg);
                return;
            }

            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }

                if (i == arg.Length)
                {
                    // Backslash di ujung: digandakan supaya tidak meng-escape
                    // tanda kutip penutup yang menyusul.
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }
            sb.Append('"');
        }

        /// <summary>Jalankan perintah pendek dan tangkap keluarannya sebagai teks.</summary>
        public static CapturedResult RunCapture(IList<string> cmd,
                                                double timeoutSeconds = 0,
                                                string workingDirectory = null)
        {
            var result = new CapturedResult();
            var psi = NewStartInfo(cmd);
            if (!string.IsNullOrEmpty(workingDirectory))
                psi.WorkingDirectory = workingDirectory;

            using (var proc = new Process { StartInfo = psi })
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                proc.OutputDataReceived += (s, e) =>
                { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) =>
                { if (e.Data != null) stderr.AppendLine(e.Data); };

                try
                {
                    proc.Start();
                }
                catch (Exception ex)
                {
                    result.StdErr = ex.Message;
                    return result;
                }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                // Ditutup, bukan dibiarkan menganggur: ffprobe yang menunggu
                // masukan akan menggantung sampai batas waktu habis.
                try { proc.StandardInput.Close(); } catch (IOException) { }

                int millis = timeoutSeconds > 0
                    ? (int)(timeoutSeconds * 1000) : int.MaxValue;
                if (!proc.WaitForExit(millis))
                {
                    result.TimedOut = true;
                    TerminateTree(proc);
                    return result;
                }
                // WaitForExit(int) tidak menjamin pembaca asinkron sudah
                // selesai; overload tanpa argumen inilah yang menunggunya,
                // dan tanpa ini baris terakhir kadang hilang.
                proc.WaitForExit();

                result.ExitCode = proc.ExitCode;
                result.StdOut = stdout.ToString();
                result.StdErr = stderr.ToString();
            }
            return result;
        }

        /// <summary>
        /// Kalau true, ffmpeg dijalankan di bawah prioritas normal.
        ///
        /// Prosesnya sendiri nyaris tidak melambat - ia tetap mendapat seluruh
        /// CPU yang menganggur - tetapi penggunanya bisa terus memakai
        /// komputernya selama render. Tanpa ini, delapan jam encode ulang
        /// membuat laptop lawas praktis tidak bisa dipakai apa-apa.
        /// </summary>
        public static bool BackgroundPriority = true;

        /// <summary>
        /// Luncurkan proses panjang yang keluarannya dibaca baris per baris.
        /// stdin dibiarkan terbuka supaya kita bisa mengirim "q" untuk
        /// berhenti dengan rapi (yang membuat indeks container tertulis)
        /// alih-alih membunuhnya.
        /// </summary>
        public static Process StartStreaming(IList<string> cmd,
                                             string workingDirectory = null)
        {
            var psi = NewStartInfo(cmd);
            if (!string.IsNullOrEmpty(workingDirectory))
                psi.WorkingDirectory = workingDirectory;
            var proc = new Process { StartInfo = psi };
            proc.Start();
            if (BackgroundPriority) TrySetBelowNormal(proc);
            return proc;
        }

        private static void TrySetBelowNormal(Process proc)
        {
            try
            {
                // Bukan Idle: Idle berarti ffmpeg hanya jalan saat tidak ada
                // apa pun yang berjalan, dan pekerjaan delapan jam bisa jadi
                // tidak selesai-selesai. BelowNormal cukup untuk menjaga
                // antarmuka tetap responsif.
                proc.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
                // Prosesnya sudah selesai, atau kebijakan sistem melarang.
                // Bukan alasan untuk menggagalkan pekerjaannya.
            }
        }

        /// <summary>Bunuh proses beserta anak-anaknya, tanpa pernah melempar.</summary>
        public static void TerminateTree(Process proc, double timeoutSeconds = 5.0)
        {
            if (proc == null) return;
            try { if (proc.HasExited) return; } catch (InvalidOperationException) { return; }

            try
            {
                // /T penting: ffmpeg sendiri jarang punya anak, tetapi
                // membunuh hanya induknya meninggalkan proses yang masih
                // menulis ke folder sementara yang sebentar lagi dihapus.
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.Arguments = "/F /T /PID " + proc.Id.ToString();
                using (var kill = Process.Start(psi))
                    kill.WaitForExit((int)(timeoutSeconds * 1000));
            }
            catch (Exception) { /* taskkill hilang atau prosesnya sudah mati */ }

            try { if (!proc.HasExited) proc.Kill(); } catch (Exception) { }
            try { proc.WaitForExit((int)(timeoutSeconds * 1000)); } catch (Exception) { }
        }
    }

    public static class Paths
    {
        /// <summary>Folder tempat exe berada - dipakai mencari ffmpeg di sebelahnya.</summary>
        public static string AppDir()
        {
            try
            {
                return Path.GetDirectoryName(
                    System.Reflection.Assembly.GetEntryAssembly()?.Location
                    ?? AppDomain.CurrentDomain.BaseDirectory)
                    ?? AppDomain.CurrentDomain.BaseDirectory;
            }
            catch (Exception)
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        public static string ConfigDir()
        {
            string root = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root))
                root = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
            return Path.Combine(root, AppInfo.Id);
        }

        public static string LogPath()
        {
            string root = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root)) root = ConfigDir();
            return Path.Combine(root, AppInfo.Id, "error.log");
        }

        /// <summary>Kembalikan `path`, atau "nama (2).ext" kalau sudah ada.</summary>
        public static string Unique(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int n = 2; n < 10000; n++)
            {
                string candidate = Path.Combine(dir, stem + " (" + n + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return path;
        }

        /// <summary>Byte kosong pada volume yang memuat `path`.</summary>
        public static long DiskFree(string path)
        {
            try
            {
                string probe = Path.GetFullPath(path);
                while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                {
                    string parent = Path.GetDirectoryName(probe);
                    if (string.IsNullOrEmpty(parent) || parent == probe) break;
                    probe = parent;
                }
                string root = Path.GetPathRoot(probe);
                if (string.IsNullOrEmpty(root)) return 0;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception)
            {
                // Drive jaringan atau UNC yang tidak melapor: jangan menolak
                // pekerjaan hanya karena ruang kosongnya tidak terbaca.
                return 0;
            }
        }

        /// <summary>
        /// Nama sistem berkas volume yang memuat `path` (mis. "NTFS", "FAT32",
        /// "exFAT"), atau string kosong kalau tidak terbaca. Dipakai untuk
        /// memperingatkan batas 4 GB per berkas pada FAT32 sebelum proses
        /// panjang dimulai.
        /// </summary>
        public static string DriveFormat(string path)
        {
            try
            {
                string probe = Path.GetFullPath(path);
                while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                {
                    string parent = Path.GetDirectoryName(probe);
                    if (string.IsNullOrEmpty(parent) || parent == probe) break;
                    probe = parent;
                }
                string root = Path.GetPathRoot(probe);
                if (string.IsNullOrEmpty(root)) return "";
                return new DriveInfo(root).DriveFormat;
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>Buka Explorer dengan berkasnya tersorot. Tidak pernah melempar.</summary>
        public static void RevealInExplorer(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = false,
                    // Explorer menuntut bentuk persis "/select," lalu jalurnya
                    // sebagai argumen terpisah.
                    Arguments = "/select,\"" + Path.GetFullPath(path) + "\"",
                };
                Process.Start(psi);
            }
            catch (Exception) { }
        }
    }
}
