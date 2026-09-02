using System;
using System.IO;
using System.Reflection;

namespace VideoMerger.App
{
    /// <summary>
    /// Muat DLL pendamping dari dalam exe-nya sendiri.
    ///
    /// Tanpa ini distribusinya jadi dua berkas, dan menyalin "VideoMerger.exe"
    /// saja ke flashdisk menghasilkan aplikasi yang gagal jalan dengan pesan
    /// yang tidak menjelaskan apa-apa. Versi sebelumnya berupa satu berkas
    /// tunggal, dan sifat itu layak dipertahankan.
    ///
    /// Pencariannya dipasang sebelum tipe apa pun dari Core disentuh - itulah
    /// sebabnya ia berada di kelas terpisah dan dipanggil dari baris pertama
    /// Main: CLR memuat assembly saat metode yang MEMAKAINYA di-JIT, bukan
    /// saat barisnya dijalankan, jadi memasangnya di tengah metode yang juga
    /// menyebut tipe Core sudah terlambat.
    /// </summary>
    internal static class EmbeddedAssemblies
    {
        public static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string wanted = new AssemblyName(args.Name).Name + ".dll";
                var host = Assembly.GetExecutingAssembly();

                // Namanya dicocokkan lewat akhiran, bukan disusun dari nama
                // assembly: nama assembly di sini "VideoMerger" sedangkan
                // resource-nya berawalan namespace "VideoMerger.App", jadi
                // menyusunnya menghasilkan nama yang tidak pernah cocok - dan
                // gagalnya baru terlihat saat exe dijalankan sendirian.
                string resource = null;
                foreach (string name in host.GetManifestResourceNames())
                {
                    if (name.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                    { resource = name; break; }
                }
                if (resource == null) return null;

                using (Stream stream = host.GetManifestResourceStream(resource))
                {
                    if (stream == null) return null;
                    var buffer = new byte[stream.Length];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return Assembly.Load(buffer);
                }
            };
        }
    }
}
