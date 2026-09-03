using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VideoMerger.Core
{
    /// <summary>
    /// Apa yang ada di komputer ini. Hanya untuk ditampilkan dan untuk
    /// menyusun sidik jari cache benchmark - keputusan encoder tidak pernah
    /// diambil dari sini, melainkan dari hasil pengukuran nyata.
    ///
    /// Nama GPU dibaca lewat WMI, yang bisa memakan ratusan milidetik pada
    /// mesin lama, jadi pemanggilnya wajib melakukannya di luar thread UI.
    /// </summary>
    public static class Hardware
    {
        private static string _cpu;
        private static List<string> _gpus;

        public static int CoreCount => Environment.ProcessorCount;

        public static string CpuName
        {
            get
            {
                if (_cpu != null) return _cpu;
                // Variabel lingkungan ini selalu ada dan gratis; WMI untuk CPU
                // tidak menambah apa pun yang berguna di sini.
                string name = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
                _cpu = string.IsNullOrWhiteSpace(name) ? "CPU" : name.Trim();
                return _cpu;
            }
        }

        public static List<string> GpuNames()
        {
            if (_gpus != null) return _gpus;
            var found = new List<string>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                           "SELECT Name FROM Win32_VideoController"))
                foreach (var item in searcher.Get())
                {
                    object name = item["Name"];
                    if (name != null && !string.IsNullOrWhiteSpace(name.ToString()))
                        found.Add(Tidy(name.ToString()));
                }
            }
            catch (Exception)
            {
                // WMI dimatikan, rusak, atau diblokir kebijakan grup. Bukan
                // alasan untuk menggagalkan apa pun - ini cuma teks.
            }
            _gpus = found;
            return _gpus;
        }

        /// <summary>
        /// Buang basa-basi merek yang memakan lebar tanpa memberi informasi:
        /// "Intel(R) UHD Graphics" -> "Intel UHD Graphics".
        /// </summary>
        private static string Tidy(string name)
        {
            return (name ?? "").Replace("(R)", "").Replace("(TM)", "")
                               .Replace("(C)", "").Replace("  ", " ").Trim();
        }

        /// <summary>Ringkasan sebaris untuk ditampilkan, mis. "8 core - NVIDIA GeForce GTX 1650".</summary>
        public static string Summary()
        {
            var sb = new StringBuilder();
            // "core" tidak dijamakkan dalam bahasa Indonesia, jadi tidak ada
            // percabangan tunggal/jamak di sini.
            sb.Append(CoreCount).Append(" core");
            var gpus = GpuNames();
            if (gpus.Count > 0) sb.Append("  -  ").Append(string.Join(", ", gpus));
            return sb.ToString();
        }

        /// <summary>
        /// Sidik jari perangkat keras + FFmpeg. Hasil benchmark hanya sah
        /// selama nilainya tidak berubah: ganti kartu grafis, perbarui driver
        /// lewat FFmpeg baru, atau pindah ke komputer lain berarti angka
        /// lamanya tidak lagi berlaku.
        /// </summary>
        public static string Fingerprint(FFmpegTools tools)
        {
            var sb = new StringBuilder();
            sb.Append(CoreCount).Append('|');
            sb.Append(CpuName).Append('|');
            sb.Append(string.Join(",", GpuNames())).Append('|');
            sb.Append(tools != null ? tools.Version : "");
            // Dipendekkan jadi angka: berkas pengaturannya key=value satu baris,
            // dan nama GPU bisa memuat karakter apa saja.
            return Hash(sb.ToString());
        }

        private static string Hash(string text)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;      // FNV-1a 64-bit
                foreach (char c in text)
                {
                    h ^= c;
                    h *= 1099511628211UL;
                }
                return h.ToString("x16", CultureInfo.InvariantCulture);
            }
        }
    }
}
