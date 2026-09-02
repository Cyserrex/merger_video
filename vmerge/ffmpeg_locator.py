"""Finding ffmpeg.exe / ffprobe.exe on the user's machine.

A gyan.dev full build of ffmpeg.exe alone is ~217 MB, so bundling it inside a
onefile .exe would produce a ~450 MB download that also has to be re-extracted
to %TEMP% on every launch. Instead the app *locates* ffmpeg, in this order:

  1. an explicit folder the user picked in Settings
  2. files bundled into the build, if a bundled build was made
  3. next to the .exe, or in an ``ffmpeg\\bin`` subfolder next to it (portable)
  4. PATH
  5. the usual install locations (winget, chocolatey, scoop, C:\\ffmpeg, ...)

If none hit, the GUI offers to download a build automatically.
"""

from __future__ import annotations

import glob
import os
import re
import shutil
from dataclasses import dataclass
from typing import Optional

from .util import IS_WINDOWS, app_dir, resource_path, run_capture

EXE = ".exe" if IS_WINDOWS else ""
FFMPEG_NAME = f"ffmpeg{EXE}"
FFPROBE_NAME = f"ffprobe{EXE}"


@dataclass
class FFmpegTools:
    ffmpeg: str
    ffprobe: str
    version: str = ""
    source: str = ""

    @property
    def ok(self) -> bool:
        return bool(self.ffmpeg and self.ffprobe)


def _pair_in(folder: str) -> Optional[tuple[str, str]]:
    """Return (ffmpeg, ffprobe) if both executables live in `folder`."""
    if not folder or not os.path.isdir(folder):
        return None
    ff = os.path.join(folder, FFMPEG_NAME)
    fp = os.path.join(folder, FFPROBE_NAME)
    if os.path.isfile(ff) and os.path.isfile(fp):
        return ff, fp
    return None


def _candidate_dirs(manual_dir: str = "") -> list[tuple[str, str]]:
    """(source_label, directory) pairs to try, in priority order."""
    out: list[tuple[str, str]] = []

    if manual_dir:
        out.append(("pengaturan", manual_dir))
        out.append(("pengaturan", os.path.join(manual_dir, "bin")))

    # Bundled into the PyInstaller build (only if built with --add-binary).
    out.append(("bawaan aplikasi", resource_path("ffmpeg")))
    out.append(("bawaan aplikasi", resource_path()))

    # Portable layout: ffmpeg sitting beside the .exe.
    here = app_dir()
    out.append(("folder aplikasi", here))
    out.append(("folder aplikasi", os.path.join(here, "ffmpeg")))
    out.append(("folder aplikasi", os.path.join(here, "ffmpeg", "bin")))
    out.append(("folder aplikasi", os.path.join(here, "bin")))

    if IS_WINDOWS:
        local = os.environ.get("LOCALAPPDATA", "")
        appdata = os.environ.get("APPDATA", "")
        pf = os.environ.get("ProgramFiles", r"C:\Program Files")
        userprofile = os.path.expanduser("~")

        fixed = [
            os.path.join(appdata, "vmerge", "ffmpeg", "bin"),
            r"C:\ffmpeg\bin",
            os.path.join(pf, "ffmpeg", "bin"),
            os.path.join(userprofile, "scoop", "shims"),
            r"C:\ProgramData\chocolatey\bin",
        ]
        out += [("terpasang di sistem", d) for d in fixed]

        # winget keeps a versioned folder; glob rather than hardcode a version.
        if local:
            pattern = os.path.join(
                local, "Microsoft", "WinGet", "Packages",
                "Gyan.FFmpeg*", "ffmpeg-*", "bin")
            for found in sorted(glob.glob(pattern), reverse=True):
                out.append(("winget", found))
    return out


def _probe_version(tool_path: str) -> str:
    """Run the tool and return its version, or "" if it will not run.

    Used on ffprobe as well as ffmpeg, so the pattern matches either banner
    ("ffmpeg version 8.1.1" / "ffprobe version 8.1.1").
    """
    try:
        res = run_capture([tool_path, "-hide_banner", "-version"], timeout=15)
        if res.returncode != 0:
            return ""
        lines = (res.stdout or res.stderr or "").splitlines()
        if not lines:
            return ""
        match = re.search(r"ff(?:mpeg|probe) version (\S+)", lines[0])
        return match.group(1) if match else lines[0].strip()
    except Exception:
        return ""


def locate(manual_dir: str = "") -> Optional[FFmpegTools]:
    """Find a usable ffmpeg/ffprobe pair, or None."""
    for source, folder in _candidate_dirs(manual_dir):
        pair = _pair_in(folder)
        if pair:
            tools = FFmpegTools(pair[0], pair[1], source=source)
            tools.version = _probe_version(tools.ffmpeg)
            # ffprobe has to run too. A folder holding a working ffmpeg.exe
            # beside a truncated or blocked ffprobe.exe used to be accepted,
            # and then every single file came back "rusak".
            if tools.version and _probe_version(tools.ffprobe):
                return tools

    # Fall back to whatever PATH resolves to.
    ff = shutil.which("ffmpeg")
    fp = shutil.which("ffprobe")
    if ff and fp:
        tools = FFmpegTools(ff, fp, source="PATH")
        tools.version = _probe_version(ff)
        if tools.version and _probe_version(fp):
            return tools
    return None


def install_dir() -> str:
    """Where an auto-downloaded ffmpeg gets unpacked."""
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    return os.path.join(base, "vmerge", "ffmpeg")


DOWNLOAD_URL = (
    "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
)


def download_and_install(progress=None, cancel=None) -> Optional[FFmpegTools]:
    """Fetch the gyan.dev essentials build and unpack ffmpeg/ffprobe.

    `progress(downloaded_bytes, total_bytes, message)` is called as it goes;
    `cancel()` returning True aborts. Only the two executables we need are
    extracted, which keeps the install around 170 MB instead of the full zip.
    """
    import tempfile
    import urllib.request
    import zipfile

    def report(done, total, msg):
        if progress:
            progress(done, total, msg)

    target = install_dir()
    bin_dir = os.path.join(target, "bin")
    os.makedirs(bin_dir, exist_ok=True)

    tmp_zip = os.path.join(tempfile.gettempdir(), "vmerge_ffmpeg.zip")
    try:
        report(0, 0, "Menghubungi server gyan.dev...")
        req = urllib.request.Request(
            DOWNLOAD_URL, headers={"User-Agent": "vmerge/1.0"})
        with urllib.request.urlopen(req, timeout=60) as resp, \
                open(tmp_zip, "wb") as out:
            total = int(resp.headers.get("Content-Length") or 0)
            done = 0
            while True:
                if cancel and cancel():
                    return None
                chunk = resp.read(256 * 1024)
                if not chunk:
                    break
                out.write(chunk)
                done += len(chunk)
                report(done, total, "Mengunduh FFmpeg...")

        report(0, 0, "Mengekstrak...")
        with zipfile.ZipFile(tmp_zip) as zf:
            wanted = [n for n in zf.namelist()
                      if os.path.basename(n).lower()
                      in (FFMPEG_NAME, FFPROBE_NAME)]
            if not wanted:
                return None
            for name in wanted:
                dest = os.path.join(bin_dir, os.path.basename(name))
                with zf.open(name) as src, open(dest, "wb") as dst:
                    shutil.copyfileobj(src, dst, 1024 * 1024)
        return locate(manual_dir=target)
    except Exception:
        return None
    finally:
        try:
            os.unlink(tmp_zip)
        except OSError:
            pass
