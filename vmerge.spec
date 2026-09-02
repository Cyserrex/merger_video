# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for VideoMerger.exe.

Build:
    pyinstaller --noconfirm vmerge.spec

FFmpeg is NOT bundled by default: a gyan.dev full build is ~217 MB per
executable, which would turn a 12 MB app into a ~450 MB download that also
re-extracts to %TEMP% on every launch. The app finds ffmpeg on PATH / beside
the .exe, or offers to download it once into %APPDATA%.

To bundle anyway (fully offline, portable), set VMERGE_FFMPEG_DIR to a folder
holding ffmpeg.exe and ffprobe.exe before building:

    set VMERGE_FFMPEG_DIR=C:\\ffmpeg\\bin
    pyinstaller --noconfirm vmerge.spec
"""

import os

block_cipher = None

binaries = []
_ff_dir = os.environ.get('VMERGE_FFMPEG_DIR', '').strip('"')
if _ff_dir and os.path.isdir(_ff_dir):
    for _name in ('ffmpeg.exe', 'ffprobe.exe'):
        _src = os.path.join(_ff_dir, _name)
        if os.path.isfile(_src):
            # Lands in <bundle>/ffmpeg/, which ffmpeg_locator searches first.
            binaries.append((_src, 'ffmpeg'))
    print(f'[vmerge.spec] bundling ffmpeg from {_ff_dir}: '
          f'{[os.path.basename(b[0]) for b in binaries]}')
else:
    print('[vmerge.spec] building WITHOUT bundled ffmpeg '
          '(set VMERGE_FFMPEG_DIR to bundle it)')

a = Analysis(
    ['app.py'],
    pathex=[],
    binaries=binaries,
    datas=[('assets/vmerge.ico', 'assets')],
    hiddenimports=[
        # Statically imported today, but a missing tkinter submodule fails at
        # runtime with a bare ModuleNotFoundError and no console to show it.
        'tkinter', 'tkinter.ttk', 'tkinter.filedialog',
        'tkinter.messagebox', 'tkinter.scrolledtext',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    # The app only needs the stdlib + tkinter; excluding the heavy scientific
    # stack keeps the onefile exe small and the startup extraction fast.
    # Deliberately NOT excluded: email/http/urllib (the FFmpeg downloader
    # pulls them in) and xml (imported indirectly by parts of the stdlib).
    excludes=[
        'numpy', 'scipy', 'pandas', 'matplotlib', 'PIL', 'PyQt5', 'PyQt6',
        'PySide2', 'PySide6', 'IPython', 'notebook', 'pytest', 'setuptools',
        'pip', 'wheel', 'pydoc_data', 'lib2to3', 'pdb', 'doctest',
    ],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

# --onefile keeps distribution to a single file but re-extracts the whole
# payload to %TEMP% on every launch (measured: 2.5 s to reach __main__ versus
# 0.10 s for onedir). Set VMERGE_ONEDIR=1 to build the fast-starting folder
# layout instead.
ONEDIR = os.environ.get('VMERGE_ONEDIR', '').strip() not in ('', '0')

exe = EXE(
    pyz,
    a.scripts,
    [] if ONEDIR else a.binaries,
    [] if ONEDIR else a.zipfiles,
    [] if ONEDIR else a.datas,
    [],
    exclude_binaries=ONEDIR,
    name='VideoMerger',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,               # UPX packing is a common antivirus trigger
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,           # GUI app: no console window
    # PyInstaller's own traceback dialog is modal and waits for a click,
    # which would strand an overnight job. app.py installs a crash handler
    # that logs and exits instead.
    disable_windowed_traceback=True,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon='assets/vmerge.ico',
    version='version_info.txt',
)

if ONEDIR:
    coll = COLLECT(
        exe,
        a.binaries,
        a.zipfiles,
        a.datas,
        strip=False,
        upx=False,
        upx_exclude=[],
        name='VideoMerger',
    )
