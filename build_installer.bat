@echo off
REM ============================================================
REM  Video Merger - buat installer (VideoMerger-<versi>-Setup.exe)
REM
REM  Cara pakai:
REM      build_installer.bat                -> installer biasa (FFmpeg dicari
REM                                            atau diunduh saat pertama pakai)
REM      build_installer.bat --with-ffmpeg  -> tidak dipakai lagi; untuk
REM                                            installer offline penuh, taruh
REM                                            ffmpeg di folder ffmpeg\ (lihat
REM                                            di bawah)
REM
REM  Untuk installer offline penuh tanpa membengkakkan exe, taruh ffmpeg.exe
REM  dan ffprobe.exe di folder ffmpeg\ di sebelah berkas ini; setup.iss akan
REM  mendeteksi dan ikut memasangnya.
REM
REM  Syarat:
REM      - .NET SDK 8 dan .NET Framework 4.8 Developer Pack
REM      - Inno Setup 6  (winget install JRSoftware.InnoSetup)
REM
REM  Set VMERGE_NOPAUSE=1 untuk jalan tanpa "tekan sembarang tombol" (CI).
REM ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "VERSION=1.2.0"
set "SETUP=installer\Output\VideoMerger-%VERSION%-Setup.exe"

echo.
echo === [1/3] Membangun VideoMerger.exe ===
REM build_exe.bat dipaksa tidak jeda supaya rantai build tidak berhenti di
REM tengah menunggu tombol; setelan pemanggil dikembalikan setelahnya agar
REM skrip ini sendiri tetap berhenti di akhir kalau diklik dua kali.
set "OUTER_NOPAUSE=%VMERGE_NOPAUSE%"
set "VMERGE_NOPAUSE=1"
REM Jalur lengkap, bukan nama polos: kalau NoDefaultCurrentDirectoryInExePath
REM aktif (lazim di PC yang dikeraskan oleh kebijakan grup), cmd tidak lagi
REM mencari batch di folder kerja dan panggilan ini gagal "is not recognized".
call "%~dp0build.bat" %*
set "BUILD_RC=%ERRORLEVEL%"
set "VMERGE_NOPAUSE=%OUTER_NOPAUSE%"
if not "%BUILD_RC%"=="0" (
    echo GAGAL membangun exe.
    if not defined VMERGE_NOPAUSE pause
    exit /b 1
)
if not exist "dist\VideoMerger.exe" (
    echo GAGAL: dist\VideoMerger.exe tidak terbentuk.
    if not defined VMERGE_NOPAUSE pause
    exit /b 1
)

echo.
echo === [2/3] Mencari Inno Setup ===
REM Inno Setup bisa terpasang per-mesin (Program Files) atau per-pengguna
REM (LocalAppData, yang dipakai winget tanpa hak admin), dan tidak pernah
REM menaruh dirinya di PATH. Cari semuanya sebelum menyerah.
set "ISCC="
for %%D in (
    "%LOCALAPPDATA%\Programs\Inno Setup 6"
    "%ProgramFiles(x86)%\Inno Setup 6"
    "%ProgramFiles%\Inno Setup 6"
) do ( if not defined ISCC if exist "%%~D\ISCC.exe" set "ISCC=%%~D\ISCC.exe" )
if not defined ISCC (
    for /f "delims=" %%F in ('where ISCC 2^>nul') do (
        if not defined ISCC set "ISCC=%%F"
    )
)
if not defined ISCC (
    echo GAGAL: Inno Setup 6 tidak ditemukan.
    echo Pasang dulu:  winget install JRSoftware.InnoSetup
    echo atau unduh :  https://jrsoftware.org/isdl.php
    if not defined VMERGE_NOPAUSE pause
    exit /b 1
)
echo Ditemukan: !ISCC!

echo.
echo === [3/3] Membangun installer ===
"!ISCC!" /Q "installer\setup.iss"
if errorlevel 1 (
    echo GAGAL membangun installer.
    if not defined VMERGE_NOPAUSE pause
    exit /b 1
)

echo.
if not exist "%SETUP%" (
    echo GAGAL: %SETUP% tidak terbentuk.
    if not defined VMERGE_NOPAUSE pause
    exit /b 1
)
for %%A in ("%SETUP%") do set /a SZMB=%%~zA/1048576
echo ============================================================
echo  BERHASIL
echo  File  : %CD%\%SETUP%
echo  Ukuran: !SZMB! MB
echo ============================================================
if not defined VMERGE_NOPAUSE pause
