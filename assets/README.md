# Aset ikon

| Berkas | Isi |
|---|---|
| `vmerge-source.png` | Gambar asli, 1254x1254, **tanpa alpha dan berlatar hitam**. Disimpan supaya ikonnya bisa dibuat ulang tanpa mencari berkas aslinya lagi. |
| `vmerge.ico` | Ikon aplikasi. 9 ukuran: 16, 20, 24, 32, 40, 48, 64, 128, 256. |
| `vmerge.png` | Pratinjau 256x256 berlatar transparan. |

## Kalau perlu dibuat ulang

Sumbernya berlatar **hitam pekat tanpa alpha**, jadi tidak bisa langsung
dijadikan `.ico` — hasilnya kotak hitam di taskbar. Latarnya harus dibuang
dulu, dan tidak boleh dengan ambang warna polos: strip film di dalam logo
berwarna `#2B2F3A` yang nyaris sama gelapnya dengan latar, sehingga "hapus
semua yang gelap" akan melubangi gambarnya sendiri.

Cara yang dipakai: **penelusuran tersambung dari tepi kanvas** — hanya piksel
gelap yang menyambung ke pinggir yang dibuang, jadi bagian gelap di dalam logo
aman. Piksel di cincin anti-aliasing bernilai warna logo yang sudah tercampur
hitam, jadi alfanya dihitung dari kecerahannya lalu warnanya dikembalikan
(dibagi alfa). Tanpa langkah terakhir itu, ikonnya bergaris gelap di sekeliling
saat ditaruh di atas latar terang.

Ukuran 128 dan 256 disimpan sebagai PNG di dalam `.ico`; sisanya sebagai DIB
32-bit. DIB tidak terkompresi sama sekali - 128x128 saja memakan 65 KB melawan
~30 KB sebagai PNG, dan ukuran itu ikut masuk ke dalam exe.
