; ============================================================================
;  Video Merger - skrip installer Inno Setup
;
;  Bangun dengan:  build_installer.bat        (dari folder induk)
;  atau manual  :  ISCC.exe installer\setup.iss
;
;  Syarat: dist\VideoMerger.exe sudah ada (jalankan build_exe.bat dulu).
;
;  FFmpeg ikut dibundel HANYA kalau folder ffmpeg\ di akar proyek berisi
;  ffmpeg.exe dan ffprobe.exe. Tanpa itu installer tetap jadi, dan aplikasi
;  akan mencari atau mengunduh FFmpeg sendiri saat pertama dipakai.
; ============================================================================

#define MyAppName "Video Merger"
#define MyAppVersion "1.0.0"
#define MyAppExeName "VideoMerger.exe"
#define MyAppPublisher "Video Merger"

; Deteksi FFmpeg opsional di <akar proyek>\ffmpeg\
#define FFmpegSrc AddBackslash(SourcePath) + "..\ffmpeg"
#if FileExists(FFmpegSrc + "\ffmpeg.exe") && FileExists(FFmpegSrc + "\ffprobe.exe")
  #define BundleFFmpeg
#endif

[Setup]
; AppId menentukan identitas aplikasi bagi Windows. JANGAN diubah antar versi,
; kalau tidak setiap rilis akan terpasang sebagai aplikasi terpisah dan versi
; lama tidak pernah tergantikan.
AppId={{7B3C1E42-9D5A-4F18-A6C7-2E4B8D0F5A31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}

; "lowest" + OverridesAllowed=dialog: installer menawarkan pasang untuk semua
; pengguna (butuh admin) atau hanya untuk saya (tanpa admin). Penting karena
; aplikasi ini sering dipakai di PC kantor yang penggunanya bukan admin.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Build PyInstaller-nya 64-bit, jadi tolak Windows 32-bit sejak awal daripada
; gagal misterius saat exe dijalankan.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1

OutputDir=Output
OutputBaseFilename=VideoMerger-{#MyAppVersion}-Setup
SetupIconFile=..\assets\vmerge.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes

; Tutup aplikasi yang sedang jalan sebelum menimpa exe-nya, daripada gagal
; dengan "file sedang digunakan" di tengah pemasangan ulang.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "id"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Buat ikon di Desktop"; GroupDescription: "Pintasan tambahan:"

[Files]
Source: "..\dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; DestName: "Panduan.md"; Flags: ignoreversion isreadme
#ifdef BundleFFmpeg
; Ditempatkan di {app}\ffmpeg - salah satu folder pertama yang dicari
; ffmpeg_locator, jadi aplikasi langsung siap pakai tanpa unduhan apa pun.
Source: "{#FFmpegSrc}\ffmpeg.exe"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion
Source: "{#FFmpegSrc}\ffprobe.exe"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Hapus {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Jalankan {#MyAppName} sekarang"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Log galat yang dibuat aplikasi kalau pernah crash.
Type: filesandordirs; Name: "{localappdata}\vmerge"

[Messages]
; Inno Setup 6 belum menyertakan terjemahan Indonesia resmi, jadi teks yang
; benar-benar dibaca pengguna ditimpa di sini di atas Default.isl.
SetupAppTitle=Pemasang
SetupWindowTitle=Pemasang - %1
ExitSetupTitle=Keluar dari Pemasang
ExitSetupMessage=Pemasangan belum selesai. Kalau keluar sekarang, aplikasi tidak akan terpasang.%n%nYakin mau keluar?
ButtonBack=< &Kembali
ButtonNext=&Lanjut >
ButtonInstall=&Pasang
ButtonCancel=Batal
ButtonYes=&Ya
ButtonNo=&Tidak
ButtonFinish=&Selesai
ButtonBrowse=&Telusuri...
ClickNext=Klik Lanjut untuk melanjutkan, atau Batal untuk keluar.
BeveledLabel=

WelcomeLabel1=Selamat datang di pemasang [name]
WelcomeLabel2=Aplikasi ini akan memasang [name/ver] di komputer Anda.%n%nSebaiknya tutup aplikasi lain sebelum melanjutkan.

PrivilegesRequiredOverrideTitle=Pilih Cara Pemasangan
PrivilegesRequiredOverrideInstruction=Pilih untuk siapa aplikasi ini dipasang
PrivilegesRequiredOverrideText1=[name] bisa dipasang untuk semua pengguna (butuh hak administrator), atau hanya untuk Anda.
PrivilegesRequiredOverrideText2=[name] bisa dipasang hanya untuk Anda, atau untuk semua pengguna (butuh hak administrator).
PrivilegesRequiredOverrideAllUsers=Pasang untuk &semua pengguna
PrivilegesRequiredOverrideCurrentUser=Pasang hanya untuk &saya
PrivilegesRequiredOverrideCurrentUserRecommended=Pasang hanya untuk &saya (disarankan)

WizardSelectDir=Pilih Lokasi Pemasangan
SelectDirDesc=Di mana [name] akan dipasang?
SelectDirLabel3=Pemasang akan menaruh [name] di folder berikut.
SelectDirBrowseLabel=Klik Lanjut untuk memakai folder ini. Untuk folder lain, klik Telusuri.
DiskSpaceGBLabel=Butuh ruang kosong minimal [gb] GB.
DiskSpaceMBLabel=Butuh ruang kosong minimal [mb] MB.
CannotInstallToNetworkDrive=Tidak bisa memasang ke drive jaringan.
InvalidPath=Masukkan jalur lengkap beserta huruf drive, contoh:%n%nC:\APP

WizardSelectTasks=Pilih Tugas Tambahan
SelectTasksDesc=Tugas tambahan apa yang perlu dijalankan?
SelectTasksLabel2=Pilih tugas tambahan, lalu klik Lanjut.

WizardReady=Siap Memasang
ReadyLabel1=Pemasang siap memasang [name] di komputer Anda.
ReadyLabel2a=Klik Pasang untuk mulai, atau Kembali untuk mengubah pilihan.
ReadyLabel2b=Klik Pasang untuk mulai memasang.
ReadyMemoDir=Lokasi pemasangan:
ReadyMemoTasks=Tugas tambahan:
ReadyMemoGroup=Folder Start Menu:

WizardPreparing=Menyiapkan
PreparingDesc=Menyiapkan pemasangan [name].
WizardInstalling=Memasang
InstallingLabel=Mohon tunggu, [name] sedang dipasang...

FinishedHeadingLabel=Pemasangan [name] selesai
FinishedLabelNoIcons=[name] sudah terpasang di komputer Anda.
FinishedLabel=[name] sudah terpasang. Jalankan lewat ikon yang dibuat.
ClickFinish=Klik Selesai untuk menutup pemasang.
RunEntryExec=Jalankan %1

ConfirmUninstall=Yakin mau menghapus %1 beserta seluruh komponennya?
UninstallStatusLabel=Mohon tunggu, %1 sedang dihapus...
UninstalledAll=%1 berhasil dihapus dari komputer Anda.
UninstalledMost=%1 sudah dihapus.%n%nBeberapa item tidak bisa dihapus dan bisa Anda hapus manual.
StatusExtractFiles=Menyalin berkas...
StatusCreateIcons=Membuat pintasan...
StatusUninstalling=Menghapus %1...
ErrorTitle=Galat
SetupAborted=Pemasangan tidak selesai.%n%nPerbaiki masalahnya lalu jalankan pemasang lagi.

[CustomMessages]
id.FFmpegNote=Catatan: aplikasi ini memakai FFmpeg. Kalau FFmpeg belum ada di komputer, aplikasi akan menawarkan mengunduhnya sekali (sekitar 170 MB) saat pertama dipakai.
id.RemoveDataPrompt=Hapus juga pengaturan dan FFmpeg yang pernah diunduh aplikasi ini (%1)?%n%nPilih Tidak kalau Anda berencana memasangnya lagi.

[Code]
function AppDataDir(): String;
begin
  Result := ExpandConstant('{userappdata}\vmerge');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { Ditempel di halaman Siap, bukan sebagai halaman info tersendiri: satu
    halaman wizard tambahan hanya untuk satu kalimat itu mengganggu, tapi
    penggunanya tetap perlu tahu kenapa aplikasi minta mengunduh nanti. }
  if CurPageID = wpReady then
    WizardForm.ReadyMemo.Lines.Add(#13#10 + ExpandConstant('{cm:FFmpegNote}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir, Prompt: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Dir := AppDataDir();
    { FFmpeg hasil unduhan berukuran ~170 MB. Menghapusnya diam-diam berarti
      pemasangan ulang harus mengunduh lagi; meninggalkannya diam-diam berarti
      170 MB tertinggal selamanya setelah pengguna mengira sudah bersih. Jadi
      tanyakan - dan hanya kalau foldernya memang ada. }
    if DirExists(Dir) then
      { SuppressibleMsgBox, bukan MsgBox: MsgBox biasa tetap menampilkan
        dialog modal walau dijalankan dengan /VERYSILENT, sehingga uninstall
        tak berpenjaga (deployment IT, atau pemasangan ulang yang memanggil
        uninstaller lama) menggantung selamanya menunggu klik - terbukti saat
        pengujian. Jawaban bawaan saat senyap adalah IDNO: uninstall senyap
        hampir selalu bagian dari upgrade, dan menghapus FFmpeg 170 MB di situ
        berarti versi barunya harus mengunduh ulang tanpa ada yang meminta. }
    begin
      { Dirakit ke variabel lebih dulu: kalau argumen "[Dir]" jatuh di awal
        baris, kompiler Inno membacanya sebagai tag seksi dan gagal build. }
      Prompt := FmtMessage(ExpandConstant('{cm:RemoveDataPrompt}'), [Dir]);
      if SuppressibleMsgBox(Prompt, mbConfirmation, MB_YESNO, IDNO) = IDYES then
        DelTree(Dir, True, True, True);
    end;
  end;
end;
