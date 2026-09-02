using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using VideoMerger.Core;

namespace VideoMerger.App
{
    /// <summary>
    /// Titik masuk. Ada argumen -> mode baris perintah; tanpa argumen -> GUI.
    /// </summary>
    public static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        private const int AttachParentProcess = -1;

        /// <summary>
        /// Sengaja hanya dua baris. CLR memuat assembly saat metode yang
        /// MEMAKAI tipenya di-JIT, bukan saat barisnya dijalankan - jadi
        /// begitu Main menyentuh apa pun dari Core, pemuatan terjadi sebelum
        /// baris pertama sempat berjalan dan pencari yang dipasang di situ
        /// tidak pernah terpakai. Isinya harus tinggal di metode terpisah
        /// yang tidak boleh di-inline kembali ke sini.
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            EmbeddedAssemblies.Install();
            return Start(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Start(string[] args)
        {
            InstallCrashHandler();

            if (args != null && args.Length > 0)
            {
                bool ownConsole = EnsureConsole();
                int code;
                try
                {
                    code = Cli.Run(args);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("GAGAL: " + ex.Message);
                    code = 1;
                }
                if (ownConsole) PauseBriefly();
                return code;
            }

            var app = new App();
            app.InitializeComponent();
            app.Run(new MainWindow());
            return 0;
        }

        /// <summary>
        /// Beri build berjendela tempat untuk menulis. Mengembalikan true kalau
        /// jendela konsolnya milik kita sendiri (dan karenanya akan ikut hilang
        /// saat proses berakhir).
        ///
        /// Aliran yang SUDAH diarahkan tidak pernah disentuh: membuka CONOUT$
        /// tanpa syarat akan mengosongkan berkas milik pengguna pada
        /// <c>VideoMerger.exe --list &gt; daftar.txt</c>. stdout dan stderr
        /// diperiksa terpisah karena pengalihan bisa mengenai salah satunya saja.
        /// </summary>
        private static bool EnsureConsole()
        {
            bool opened = false;
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                // Menempel ke terminal pemanggil lebih dulu; membuka jendela
                // sendiri adalah pilihan terakhir, bukan yang kedua.
                if (!AttachConsole(AttachParentProcess))
                    opened = AllocConsole();
            }

            try
            {
                if (!Console.IsOutputRedirected)
                {
                    var stdout = new StreamWriter(Console.OpenStandardOutput(),
                                                  new UTF8Encoding(false))
                    { AutoFlush = true };
                    Console.SetOut(stdout);
                }
                if (!Console.IsErrorRedirected)
                {
                    var stderr = new StreamWriter(Console.OpenStandardError(),
                                                  new UTF8Encoding(false))
                    { AutoFlush = true };
                    Console.SetError(stderr);
                }
            }
            catch (IOException)
            {
                // Tidak ada konsol yang bisa dipakai sama sekali; biarkan
                // penulisan berikutnya berakhir di mana pun ia berakhir
                // daripada menjatuhkan proses.
            }
            return opened;
        }

        /// <summary>
        /// Jendela konsol milik sendiri ikut tertutup bersama prosesnya, jadi
        /// tahan sebentar supaya pesannya sempat terbaca. Tidak pernah memakai
        /// pembacaan yang memblokir: proses terjadwal tidak boleh menggantung
        /// selamanya menunggu tombol yang tidak akan pernah ditekan.
        /// </summary>
        private static void PauseBriefly(int seconds = 30)
        {
            Console.WriteLine();
            Console.WriteLine("Tekan tombol apa saja untuk menutup "
                              + "(otomatis tertutup dalam " + seconds + " detik)...");
            for (int i = 0; i < seconds * 10; i++)
            {
                try { if (Console.KeyAvailable) { Console.ReadKey(true); return; } }
                catch (InvalidOperationException) { return; }   // stdin dialihkan
                Thread.Sleep(100);
            }
        }

        /// <summary>
        /// Catat kesalahan yang tak tertangani lalu keluar, alih-alih berhenti
        /// di depan dialog.
        ///
        /// WPF menampilkan kotak modal lalu menunggu seseorang mengkliknya.
        /// Untuk penggabungan yang ditinggal semalaman, itu berarti pekerjaannya
        /// mati begitu saja dan baru ketahuan pagi hari di balik dialog yang
        /// tidak dilihat siapa pun. Menulis jejaknya ke berkas log lalu keluar
        /// lebih berguna sekaligus tidak memblokir.
        /// </summary>
        private static void InstallCrashHandler()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                string path = WriteCrashLog(ex, "utama");
                ShowCrash(ex, path);
                Environment.Exit(1);
            };
        }

        internal static string WriteCrashLog(Exception ex, string where)
        {
            string path = Paths.LogPath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path,
                    "=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + " (" + where + ") ===" + Environment.NewLine
                    + (ex != null ? ex.ToString() : "(tidak diketahui)")
                    + Environment.NewLine + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception) { }
            return path;
        }

        internal static void ShowCrash(Exception ex, string logPath)
        {
            try
            {
                MessageBox.Show(
                    "Terjadi kesalahan tak terduga dan aplikasi harus ditutup."
                    + Environment.NewLine + Environment.NewLine
                    + (ex != null ? ex.Message : "")
                    + Environment.NewLine + Environment.NewLine
                    + "Rincian teknis disimpan di:" + Environment.NewLine + logPath,
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception) { }
        }
    }
}
