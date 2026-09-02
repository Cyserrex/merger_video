using System;
using System.Collections.Generic;
using System.IO;

namespace VideoMerger.Core
{
    /// <summary>Menelusuri folder dan mengumpulkan berkas video kandidat.</summary>
    public static class Scanner
    {
        private static readonly HashSet<string> Extensions =
            new HashSet<string>(AppInfo.VideoExtensions, StringComparer.OrdinalIgnoreCase);

        public static bool IsVideoName(string name)
        {
            try { return Extensions.Contains(Path.GetExtension(name) ?? ""); }
            catch (ArgumentException) { return false; }
        }

        /// <summary>
        /// Kumpulkan setiap berkas yang tampak video di bawah `folder`.
        ///
        /// Hasilnya belum diurutkan dan belum diperiksa ffprobe. Atribut berkas
        /// dibaca di sini karena murah dan kolom tanggal membutuhkannya sebelum
        /// ffprobe berjalan. Entri yang tidak terbaca dilewati, bukan
        /// dilemparkan sebagai galat: satu berkas terkunci di antara 100 tidak
        /// boleh membatalkan seluruh pemindaian.
        /// </summary>
        public static List<VideoFile> ScanFolder(string folder, bool recursive = false,
                                                 Func<bool> cancel = null,
                                                 Action<int> onFound = null)
        {
            var found = new List<VideoFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string root;
            try { root = Path.GetFullPath(folder); }
            catch (Exception) { return found; }

            Walk(root, 0, recursive, found, seen, cancel, onFound);
            return found;
        }

        private static void Walk(string path, int depth, bool recursive,
                                 List<VideoFile> found, HashSet<string> seen,
                                 Func<bool> cancel, Action<int> onFound)
        {
            if (cancel != null && cancel()) return;

            string[] entries;
            try { entries = Directory.GetFileSystemEntries(path); }
            catch (Exception) { return; }   // tidak ada izin, folder hilang, dsb.

            foreach (string entry in entries)
            {
                if (cancel != null && cancel()) return;
                try
                {
                    FileAttributes attrs;
                    try { attrs = File.GetAttributes(entry); }
                    catch (Exception) { continue; }

                    // Junction Windows tampak sebagai folder biasa. Satu yang
                    // menunjuk balik ke folder induknya mengubah pemindaian
                    // rekursif jadi penelusuran tanpa akhir (terukur: 1536
                    // berkas ditemukan di folder yang isinya 24).
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;

                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        if (recursive && depth < 24)
                            Walk(entry, depth + 1, true, found, seen, cancel, onFound);
                        continue;
                    }

                    if (!Extensions.Contains(Path.GetExtension(entry) ?? "")) continue;

                    string full = Path.GetFullPath(entry);
                    if (!seen.Add(full)) continue;

                    var info = new FileInfo(full);
                    found.Add(new VideoFile
                    {
                        Path = full,
                        Size = info.Length,
                        Modified = info.LastWriteTime,
                        // Di Windows CreationTime memang waktu pembuatan,
                        // yang itulah isi kolom "Tanggal dibuat".
                        CreatedOn = info.CreationTime,
                        NameTimestamp = TimestampParser.Parse(info.Name),
                    });
                    if (onFound != null) onFound(found.Count);
                }
                catch (Exception)
                {
                    continue;
                }
            }
        }
    }
}
