using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VideoMerger.Core;

namespace VideoMerger.App
{
    /// <summary>
    /// Dasar bagi baris tabel.
    ///
    /// Ada lapisan pembungkus di sini karena Core memakai field publik dan
    /// binding WPF hanya bekerja pada properti - dan karena hanya sebagian
    /// kecil isinya yang benar-benar berubah setelah pemindaian (centang,
    /// nomor, keterangan). Membuat seluruh model bisa memberi notifikasi akan
    /// menyeret urusan tampilan ke dalam mesin yang juga dipakai CLI dan tes.
    /// </summary>
    public abstract class RowBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }

        private int _index;
        public int Index
        {
            get { return _index; }
            set { if (_index != value) { _index = value; Raise(); } }
        }
    }

    /// <summary>Satu baris pada tab "Gabungkan Video".</summary>
    public class MergeRow : RowBase
    {
        public VideoFile File { get; private set; }

        public MergeRow(VideoFile file)
        {
            File = file;
        }

        public bool Selected
        {
            get { return File.Selected; }
            set
            {
                // Berkas rusak tidak boleh dicentang: mencentangnya hanya akan
                // ditolak lagi saat digabung, jadi lebih jujur menolaknya di sini.
                if (!File.Valid) value = false;
                if (File.Selected == value) return;
                File.Selected = value;
                Raise();
                Raise("IsDimmed");
                var changed = SelectionChanged;
                if (changed != null) changed(this, EventArgs.Empty);
            }
        }

        public event EventHandler SelectionChanged;

        public bool CanSelect => File.Valid;
        public bool IsBad => !File.Valid;
        public bool IsDimmed => File.Valid && !File.Selected;

        public string Name => File.Name;
        public string Duration => File.Valid ? Humanize.Duration(File.Duration) : "-";
        public string Resolution => File.Valid ? File.Resolution : "-";
        public string Codec => string.IsNullOrEmpty(File.VCodec) ? "-" : File.VCodec;

        public string Audio =>
            !File.Valid ? "-"
            : File.HasAudio ? File.ACodec + " " + File.Channels + "ch"
            : "(tanpa audio)";

        public string Size => Humanize.Size(File.Size);

        public string Date
        {
            get
            {
                DateTime? stamp = File.MediaCreated ?? File.NameTimestamp;
                DateTime value = stamp ?? File.Modified;
                return value == default(DateTime)
                    ? "-" : value.ToString("yyyy-MM-dd HH:mm");
            }
        }

        public string Status => File.Valid ? "" : File.Error;
    }

    /// <summary>Satu baris pada tab "Subtitle Permanen".</summary>
    public class SubRow : RowBase
    {
        public HardsubItem Item { get; private set; }

        public SubRow(HardsubItem item)
        {
            Item = item;
        }

        public bool Selected
        {
            get { return Item.Selected; }
            set
            {
                if (!Item.HasSource) value = false;
                if (Item.Selected == value) return;
                Item.Selected = value;
                Raise();
                Raise("IsDimmed");
                var changed = SelectionChanged;
                if (changed != null) changed(this, EventArgs.Empty);
            }
        }

        public event EventHandler SelectionChanged;

        public bool CanSelect => Item.HasSource;
        public bool IsBad => !Item.HasSource && !string.IsNullOrEmpty(Item.Error);
        public bool IsDimmed => Item.HasSource && !Item.Selected;

        public string Name => Item.Video.Name;
        public string Duration => Humanize.Duration(Item.Video.Duration);
        public string Resolution => Item.Video.Resolution;
        public string Source => Item.SourceLabel;

        public string Status =>
            !string.IsNullOrEmpty(Item.Error) ? Item.Error
            : !string.IsNullOrEmpty(Item.ResultPath) ? "selesai"
            : "";

        /// <summary>Dipanggil setelah sumber subtitle atau hasilnya berubah.</summary>
        public void Refresh()
        {
            Raise("Source");
            Raise("Status");
            Raise("Selected");
            Raise("CanSelect");
            Raise("IsBad");
            Raise("IsDimmed");
        }
    }
}
