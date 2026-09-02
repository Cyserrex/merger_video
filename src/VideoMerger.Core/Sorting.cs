using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VideoMerger.Core
{
    /// <summary>
    /// Mengurutkan daftar klip.
    ///
    /// Urutan yang dipilih di sini ADALAH urutan video jadinya, jadi "terlihat
    /// benar di Explorer" lebih penting daripada "murni secara leksikografis".
    /// Karena itu bawaannya menyerahkan perbandingan ke comparator Win32 yang
    /// dipakai Explorer sendiri, dengan urutan alami buatan sendiri sebagai
    /// cadangan bila DLL-nya tidak tersedia.
    /// </summary>
    public static class NaturalOrder
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode,
                   ExactSpelling = true, SetLastError = false)]
        private static extern int StrCmpLogicalW(string a, string b);

        private static readonly bool Win32Available = TestWin32();

        private static bool TestWin32()
        {
            try
            {
                // Diuji dulu sebelum dipercaya: kalau shlwapi tidak ada atau
                // berperilaku lain, kita mau tahu sekarang, bukan saat
                // pengguna melihat urutan yang kacau.
                return StrCmpLogicalW("a2", "a10") < 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Bandingkan dua nama seperti Windows Explorer.
        ///
        /// Nama mentah dipakai sebagai pemutus seri, dan itu bukan hiasan:
        /// StrCmpLogicalW melaporkan "a.mp4" dan "A.mp4" SAMA, sehingga urutan
        /// pasangan seperti itu akan bergantung pada urutan pemindaian. Di
        /// dalam satu folder NTFS itu tidak mungkin terjadi, tetapi pemindaian
        /// rekursif dengan mudah memungut keduanya dari subfolder berbeda.
        /// </summary>
        public static int Compare(string a, string b)
        {
            int primary = Win32Available
                ? StrCmpLogicalW(a ?? "", b ?? "")
                : CompareNatural(a ?? "", b ?? "");
            if (primary != 0) return primary;
            return string.CompareOrdinal(a ?? "", b ?? "");
        }

        public static IComparer<string> Comparer { get; } = new NameComparer();

        private class NameComparer : IComparer<string>
        {
            public int Compare(string x, string y) => NaturalOrder.Compare(x, y);
        }

        /// <summary>
        /// Cadangan: pisahkan angka dari teks supaya 'v2' &lt; 'v10'.
        /// Deretan angka dibandingkan secara numerik, sisanya tanpa peduli
        /// besar-kecil huruf.
        /// </summary>
        public static int CompareNatural(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si);
                    string nb = b.Substring(sj, j - sj);
                    string ta = na.TrimStart('0');
                    string tb = nb.TrimStart('0');
                    if (ta.Length != tb.Length)
                        return ta.Length < tb.Length ? -1 : 1;
                    int cmp = string.CompareOrdinal(ta, tb);
                    if (cmp != 0) return cmp;
                    // Nilainya sama: ejaan dengan nol di depan lebih dulu,
                    // seperti Explorer yang menaruh video05 sebelum video5.
                    if (na.Length != nb.Length)
                        return na.Length > nb.Length ? -1 : 1;
                }
                else
                {
                    int cmp = char.ToLowerInvariant(a[i]).CompareTo(
                        char.ToLowerInvariant(b[j]));
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }

    /// <summary>Waktu rekaman yang dipulihkan dari nama berkas.</summary>
    public static class TimestampParser
    {
        private class Pattern
        {
            public Regex Regex;
            public string Kind;
            public Pattern(string expr, string kind)
            {
                Regex = new Regex(expr, RegexOptions.Compiled);
                Kind = kind;
            }
        }

        // Diurutkan dari yang paling spesifik; pola pertama yang menghasilkan
        // tanggal sah menang. Sengaja tidak diikat ketat ke awal/akhir karena
        // pengekspor CCTV gemar menambahkan awalan dan akhiran.
        private static readonly Pattern[] Patterns =
        {
            // 2024-01-05 08.00.00 / 2024-01-05_08-00-00 / 2024_01_05 08:00:00
            new Pattern(@"(20\d{2})[-_.]?(\d{2})[-_.]?(\d{2})"
                        + @"[ _T-]+(\d{2})[-_.:]?(\d{2})[-_.:]?(\d{2})", "ymdhms"),
            // 20240105080000 (14 digit tanpa pemisah) - bentuk CCTV paling umum.
            // (?:\d{3})? menampung varian Hikvision yang menempelkan milidetik.
            new Pattern(@"(?<!\d)(20\d{2})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})"
                        + @"(?:\d{3})?(?!\d)", "ymdhms"),
            // 05-01-2024 08.00.00 (hari di depan)
            new Pattern(@"(?<!\d)(\d{2})[-_.](\d{2})[-_.](20\d{2})"
                        + @"[ _T-]+(\d{2})[-_.:]?(\d{2})[-_.:]?(\d{2})", "dmyhms"),
            // hanya tanggal: 2024-01-05 / 20240105
            new Pattern(@"(?<!\d)(20\d{2})[-_.]?(\d{2})[-_.]?(\d{2})(?!\d)", "ymd"),
        };

        /// <summary>
        /// Mengembalikan null - bukan tebakan - kalau tidak ada yang masuk
        /// akal, supaya pemanggilnya bisa mundur ke waktu berkas.
        /// </summary>
        public static DateTime? Parse(string name)
        {
            string stem;
            try { stem = Path.GetFileNameWithoutExtension(name) ?? ""; }
            catch (ArgumentException) { stem = name ?? ""; }

            foreach (var pattern in Patterns)
            {
                foreach (Match match in pattern.Regex.Matches(stem))
                {
                    var g = new int[match.Groups.Count - 1];
                    bool ok = true;
                    for (int i = 1; i < match.Groups.Count; i++)
                    {
                        if (!int.TryParse(match.Groups[i].Value, NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out g[i - 1]))
                        { ok = false; break; }
                    }
                    if (!ok) continue;

                    DateTime dt;
                    try
                    {
                        if (pattern.Kind == "ymdhms")
                            dt = new DateTime(g[0], g[1], g[2], g[3], g[4], g[5]);
                        else if (pattern.Kind == "dmyhms")
                            dt = new DateTime(g[2], g[1], g[0], g[3], g[4], g[5]);
                        else
                            dt = new DateTime(g[0], g[1], g[2]);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        continue;      // mis. bulan 13 - bukan tanggal sungguhan
                    }
                    if (dt.Year >= 2000 && dt.Year <= 2099) return dt;
                }
            }
            return null;
        }
    }

    public static class FileSorter
    {
        private static readonly DateTime FarFuture = new DateTime(9999, 1, 1);

        /// <summary>
        /// Kembalikan daftar baru yang diurutkan menurut `key`.
        ///
        /// Berkas yang tidak punya nilai untuk kunci terpilih (tanpa metadata
        /// tanggal, nama tak terurai) tenggelam ke bawah secara stabil alih-alih
        /// berserak: urutan relatifnya dipertahankan, dengan nama sebagai
        /// pemutus seri.
        /// </summary>
        public static List<VideoFile> Sort(IEnumerable<VideoFile> files, SortBy key,
                                           bool descending = false)
        {
            var items = new List<VideoFile>(files);
            if (key == SortBy.Manual) return items;

            // Nama adalah pemutus seri universal, jadi mulai dari urutan nama.
            StableSort(items, (a, b) => NaturalOrder.Compare(a.Name, b.Name));

            switch (key)
            {
                case SortBy.Name:
                    break;
                case SortBy.NamePlain:
                    StableSort(items, (a, b) => string.Compare(
                        a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    break;
                case SortBy.Modified:
                    StableSort(items, (a, b) => a.Modified.CompareTo(b.Modified));
                    break;
                case SortBy.Created:
                    StableSort(items, (a, b) => a.CreatedOn.CompareTo(b.CreatedOn));
                    break;
                case SortBy.Size:
                    StableSort(items, (a, b) => a.Size.CompareTo(b.Size));
                    break;
                case SortBy.Duration:
                    StableSort(items, (a, b) => a.Duration.CompareTo(b.Duration));
                    break;
                case SortBy.MediaCreated:
                    StableSort(items, (a, b) => Nullable(a.MediaCreated)
                        .CompareTo(Nullable(b.MediaCreated)));
                    break;
                case SortBy.NameTimestamp:
                    StableSort(items, (a, b) => Nullable(a.NameTimestamp)
                        .CompareTo(Nullable(b.NameTimestamp)));
                    break;
                case SortBy.Recorded:
                    StableSort(items, (a, b) => RecordedAt(a).CompareTo(RecordedAt(b)));
                    break;
            }

            if (descending) items.Reverse();
            return items;
        }

        private static DateTime Nullable(DateTime? value) => value ?? FarFuture;

        /// <summary>
        /// Waktu rekam: metadata container, lalu nama berkas, lalu mtime.
        ///
        /// Waktu pembuatan berkas tidak pernah dipakai: nilainya berubah jadi
        /// waktu penyalinan begitu rekaman ditarik dari kartu SD, sehingga
        /// semua klip akan tertumpuk pada saat yang sama.
        /// </summary>
        private static DateTime RecordedAt(VideoFile f)
        {
            if (f.MediaCreated.HasValue) return f.MediaCreated.Value;
            if (f.NameTimestamp.HasValue) return f.NameTimestamp.Value;
            return f.Modified == default(DateTime) ? FarFuture : f.Modified;
        }

        /// <summary>
        /// List.Sort di .NET TIDAK stabil (introsort). Kestabilan itu justru
        /// inti dari pengurutan bertingkat di sini: berkas tanpa tanggal harus
        /// mempertahankan urutan namanya, bukan diacak.
        /// </summary>
        public static void StableSort<T>(List<T> items, Comparison<T> comparison)
        {
            var indexed = new List<KeyValuePair<int, T>>(items.Count);
            for (int i = 0; i < items.Count; i++)
                indexed.Add(new KeyValuePair<int, T>(i, items[i]));

            indexed.Sort((a, b) =>
            {
                int cmp = comparison(a.Value, b.Value);
                return cmp != 0 ? cmp : a.Key.CompareTo(b.Key);
            });

            for (int i = 0; i < items.Count; i++) items[i] = indexed[i].Value;
        }

        /// <summary>
        /// Geser baris pada `indices` ke atas (delta&lt;0) atau bawah (delta&gt;0).
        /// Mengembalikan indeks barunya. Blok yang sudah mentok berhenti saja,
        /// sesuai harapan pengguna terhadap tombol Naik/Turun.
        /// </summary>
        public static List<int> MoveItems<T>(IList<T> order, IEnumerable<int> indices,
                                             int delta)
        {
            var idx = new List<int>(indices);
            idx.Sort();
            if (delta > 0) idx.Reverse();
            var moved = new List<int>();
            foreach (int i in idx)
            {
                int j = i + delta;
                if (j < 0 || j >= order.Count || moved.Contains(j))
                {
                    // Mentok, atau terhalang saudara yang sudah parkir.
                    moved.Add(i);
                    continue;
                }
                T tmp = order[i];
                order[i] = order[j];
                order[j] = tmp;
                moved.Add(j);
            }
            moved.Sort();
            return moved;
        }
    }
}
