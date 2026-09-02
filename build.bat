@echo off
REM ============================================================
REM  Video Merger - bangun VideoMerger.exe
REM
REM  Cara pakai:
REM      build.bat            -> bangun Release ke dist\
REM      build.bat test       -> bangun lalu jalankan rangkaian tes
REM
REM  Syarat: .NET SDK 8 dan .NET Framework 4.8 Developer Pack.
REM      winget install Microsoft.DotNet.SDK.8
REM      winget install Microsoft.DotNet.Framework.DeveloperPack_4
REM
REM  Hasilnya butuh .NET Framework 4.8 di komputer pengguna, yang sudah
REM  ikut Windows 10 1903 ke atas dan Windows 11.
REM
REM  Set VMERGE_NOPAUSE=1 untuk jalan tanpa "tekan tombol" (dipakai CI).
REM ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo.
echo === [1/3] Mencari .NET SDK ===
REM SDK tidak selalu ada di PATH, terutama kalau dipasang lewat winget
REM tanpa hak admin, jadi lokasi bakunya ikut dicoba.
set "DOTNET="
where dotnet >nul 2>&1 && for /f "delims=" %%F in ('where dotnet') do (
    if not defined DOTNET set "DOTNET=%%F"
)
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
)
if not defined DOTNET (
    echo GAGAL: .NET SDK tidak ditemukan.
    echo Pasang dulu: winget install Microsoft.DotNet.SDK.8
    if not defined VMERGE_NOPAUSE pause
    exit /b 1
)
"!DOTNET!" --list-sdks >nul 2>&1
if errorlevel 1 (
    echo GAGAL: dotnet ada tetapi SDK-nya belum terpasang.
    echo Pasang dulu: winget install Microsoft.DotNet.SDK.8
    if not defined VMERGE_NOPAUSE pause
    exit /b 1
)
echo Ditemukan: !DOTNET!

echo.
echo === [2/3] Membangun Release ===
if exist "dist" rmdir /s /q "dist"
"!DOTNET!" publish "src\VideoMerger.App\VideoMerger.App.csproj" ^
    -c Release -o "dist" --nologo -v minimal
if errorlevel 1 (
    echo GAGAL membangun.
    if not defined VMERGE_NOPAUSE pause
    exit /b 1
)

REM Berkas bantu yang tidak dibutuhkan pengguna akhir.
del /q "dist\*.pdb" 2>nul
del /q "dist\*.xml" 2>nul
REM Core.dll sudah tertanam di dalam exe; salinan di sebelahnya cuma
REM membingungkan dan membuat orang mengira exe-nya tidak berdiri sendiri.
del /q "dist\VideoMerger.Core.dll" 2>nul

if not exist "dist\VideoMerger.exe" (
    echo GAGAL: dist\VideoMerger.exe tidak terbentuk.
    if not defined VMERGE_NOPAUSE pause
    exit /b 1
)

if /i "%~1"=="test" (
    echo.
    echo === [3/3] Menjalankan tes ===
    "!DOTNET!" run --project "src\VideoMerger.Tests\VideoMerger.Tests.csproj" ^
        -c Release --nologo -v quiet
    if errorlevel 1 (
        echo TES GAGAL.
        if not defined VMERGE_NOPAUSE pause
        exit /b 1
    )
) else (
    echo.
    echo === [3/3] Tes dilewati ^(jalankan "build.bat test" untuk ikut menguji^) ===
)

echo.
for %%A in ("dist\VideoMerger.exe") do set /a SZKB=%%~zA/1024
echo ============================================================
echo  BERHASIL
echo  File  : %CD%\dist\VideoMerger.exe
echo  Ukuran: !SZKB! KB
echo ============================================================
if not defined VMERGE_NOPAUSE pause
