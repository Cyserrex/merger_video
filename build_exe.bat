@echo off
REM ============================================================
REM  Video Merger - build VideoMerger.exe
REM
REM  Cara pakai:
REM      build_exe.bat                 -> exe kecil (~12 MB), FFmpeg dicari
REM                                       di komputer atau diunduh otomatis
REM      build_exe.bat --with-ffmpeg   -> exe portabel, FFmpeg ikut dibundel
REM                                       (ukuran jadi ratusan MB)
REM
REM  Syarat: Python 3.9+ terpasang dan ada di PATH.
REM ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo.
echo === [1/5] Mencari Python ===
set "PY_CMD="
where py >nul 2>&1 && set "PY_CMD=py -3"
if "%PY_CMD%"=="" ( where python >nul 2>&1 && set "PY_CMD=python" )
if "%PY_CMD%"=="" (
    echo GAGAL: Python tidak ditemukan. Pasang dari https://www.python.org/downloads/
    echo Jangan lupa centang "Add Python to PATH" saat memasang.
    if not defined VMERGE_NOPAUSE pause & exit /b 1
)
%PY_CMD% --version

echo.
echo === [2/5] Menyiapkan virtual environment ===
if not exist ".venv\Scripts\python.exe" (
    %PY_CMD% -m venv .venv || ( echo GAGAL membuat venv & if not defined VMERGE_NOPAUSE pause & exit /b 1 )
)
set "VPY=.venv\Scripts\python.exe"

echo.
echo === [3/5] Memasang PyInstaller ===
"%VPY%" -m pip install --disable-pip-version-check -q --upgrade pip
"%VPY%" -m pip install --disable-pip-version-check -q pyinstaller || (
    echo GAGAL memasang PyInstaller & if not defined VMERGE_NOPAUSE pause & exit /b 1
)

echo.
echo === [4/5] Membuat ikon ===
"%VPY%" build_tools\make_icon.py

echo.
echo === [5/5] Membangun VideoMerger.exe ===
set "VMERGE_FFMPEG_DIR="
if /i "%~1"=="--with-ffmpeg" (
    if not "%~2"=="" (
        set "VMERGE_FFMPEG_DIR=%~2"
    ) else (
        for /f "delims=" %%F in ('where ffmpeg 2^>nul') do (
            if not defined VMERGE_FFMPEG_DIR set "VMERGE_FFMPEG_DIR=%%~dpF"
        )
    )
    if not defined VMERGE_FFMPEG_DIR (
        echo GAGAL: ffmpeg.exe tidak ditemukan di PATH.
        echo Pakai: build_exe.bat --with-ffmpeg "C:\path\ke\folder\bin"
        if not defined VMERGE_NOPAUSE pause & exit /b 1
    )
    echo Membundel FFmpeg dari: !VMERGE_FFMPEG_DIR!
)

rmdir /s /q build 2>nul
"%VPY%" -m PyInstaller --noconfirm --clean vmerge.spec || (
    echo GAGAL membangun exe & if not defined VMERGE_NOPAUSE pause & exit /b 1
)

echo.
if exist "dist\VideoMerger.exe" (
    for %%A in ("dist\VideoMerger.exe") do set "SZ=%%~zA"
    set /a SZMB=!SZ!/1048576
    echo ============================================================
    echo  BERHASIL
    echo  File  : %CD%\dist\VideoMerger.exe
    echo  Ukuran: !SZMB! MB
    echo ============================================================
) else (
    echo GAGAL: dist\VideoMerger.exe tidak terbentuk.
    if not defined VMERGE_NOPAUSE pause & exit /b 1
)
if not defined VMERGE_NOPAUSE pause
