"""Windows/subprocess plumbing shared by the rest of the app.

The two things that bite hardest when a Tk app is frozen with
``pyinstaller --noconsole`` are (a) every child process flashing a black
console window and (b) the child inheriting a NULL stdin and hanging. Every
process in this app is launched through the helpers here so both are handled
in exactly one place.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from typing import Iterable, Optional, Sequence

IS_WINDOWS = os.name == "nt"
IS_FROZEN = bool(getattr(sys, "frozen", False))

# Encoding used for ffmpeg's stdout/stderr. ffmpeg emits UTF-8 regardless of
# the console code page, and errors="replace" keeps an odd byte from raising.
PIPE_ENCODING = "utf-8"


# ------------------------------------------------------------------- paths --

def app_dir() -> str:
    """Folder the user sees: next to the .exe when frozen, project root in dev.

    This is where we look for a side-by-side ``ffmpeg.exe`` / ``ffmpeg\\bin``,
    and it is NOT the same as the onefile extraction dir.
    """
    if IS_FROZEN:
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def resource_path(*parts: str) -> str:
    """Path to a file bundled into the build (icons, bundled ffmpeg, ...).

    onefile  -> sys._MEIPASS (the temp extraction dir)
    onedir   -> the folder holding the .exe
    dev      -> the project root
    """
    base = getattr(sys, "_MEIPASS", None) or app_dir()
    return os.path.join(base, *parts)


def unique_path(path: str) -> str:
    """Return `path`, or `name (2).ext` etc. if it already exists."""
    if not os.path.exists(path):
        return path
    stem, ext = os.path.splitext(path)
    n = 2
    while os.path.exists(f"{stem} ({n}){ext}"):
        n += 1
    return f"{stem} ({n}){ext}"


def disk_free(path: str) -> int:
    """Free bytes on the volume holding `path` (walks up to an existing dir)."""
    probe = os.path.abspath(path)
    while probe and not os.path.isdir(probe):
        parent = os.path.dirname(probe)
        if parent == probe:
            break
        probe = parent
    try:
        return shutil.disk_usage(probe).free
    except OSError:
        return 0


def reveal_in_explorer(path: str) -> None:
    """Open Explorer with the file selected. Never raises."""
    try:
        if IS_WINDOWS and os.path.exists(path):
            subprocess.Popen(["explorer", "/select,", os.path.normpath(path)],
                             **no_window_kwargs())
    except OSError:
        pass


# -------------------------------------------------------------- subprocess --

def no_window_kwargs() -> dict:
    """Keyword args that keep a child process from flashing a console window.

    CREATE_NO_WINDOW alone, deliberately. Enumerating top-level windows around
    a Popen call shows the difference: with no flags a real WindowsTerminal
    window appears; with STARTUPINFO + SW_HIDE the window is hidden but a
    conhost.exe process is still created; with CREATE_NO_WINDOW no window and
    no console host is created at all. This app spawns ffprobe once per file,
    so "hidden" is not good enough - it has to be "never created".
    """
    if not IS_WINDOWS:
        return {}
    return {"creationflags": subprocess.CREATE_NO_WINDOW}


def run_capture(cmd: Sequence[str], timeout: Optional[float] = None
                ) -> subprocess.CompletedProcess:
    """Run a short-lived command and capture its output as text.

    stdin is pinned to DEVNULL: under --noconsole the inherited stdin is an
    invalid handle, and ffmpeg reading from it can block forever.
    """
    return subprocess.run(
        list(cmd),
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        encoding=PIPE_ENCODING,
        errors="replace",
        timeout=timeout,
        **no_window_kwargs(),
    )


def popen_stream(cmd: Sequence[str], merge_stderr: bool = False,
                 stdin_pipe: bool = False) -> subprocess.Popen:
    """Launch a long-running command whose output we read line by line.

    `stdin_pipe=True` keeps ffmpeg's stdin open so we can send it "q" for a
    graceful stop (which flushes the container index) instead of killing it.
    """
    return subprocess.Popen(
        list(cmd),
        stdin=subprocess.PIPE if stdin_pipe else subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT if merge_stderr else subprocess.PIPE,
        encoding=PIPE_ENCODING,
        errors="replace",
        bufsize=1,               # line buffered; -progress writes line by line
        universal_newlines=True,
        **no_window_kwargs(),
    )


def terminate_tree(proc: subprocess.Popen, timeout: float = 5.0) -> None:
    """Kill a process and any children it spawned, without raising."""
    if proc is None or proc.poll() is not None:
        return
    if IS_WINDOWS:
        try:
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                           stdin=subprocess.DEVNULL,
                           stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL,
                           timeout=timeout,
                           **no_window_kwargs())
        except (OSError, subprocess.SubprocessError):
            pass
    try:
        proc.kill()
    except OSError:
        pass
    try:
        proc.wait(timeout=timeout)
    except subprocess.SubprocessError:
        pass


# ----------------------------------------------------------------- display --

def log_path() -> str:
    base = (os.environ.get("LOCALAPPDATA") or os.environ.get("APPDATA")
            or os.path.expanduser("~"))
    return os.path.join(base, "vmerge", "error.log")


def install_crash_handler(on_error=None) -> None:
    """Record unhandled exceptions and exit, instead of blocking on a dialog.

    A windowed PyInstaller build shows a modal Win32 "Unhandled exception"
    box and then waits for someone to click it. For a merge left running
    overnight that means the job is simply dead in the morning behind a
    dialog nobody saw. Writing the traceback to a log and exiting is both
    more useful and non-blocking. Worker threads get the same treatment -
    without threading.excepthook their tracebacks vanish entirely.
    """
    import threading
    import traceback

    def record(exc_type, exc, tb, where="utama") -> str:
        text = "".join(traceback.format_exception(exc_type, exc, tb))
        try:
            path = log_path()
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "a", encoding="utf-8") as fh:
                fh.write(f"=== thread {where} ===\n{text}\n")
        except OSError:
            pass
        return text

    def handle(exc_type, exc, tb) -> None:
        if issubclass(exc_type, KeyboardInterrupt):
            return
        record(exc_type, exc, tb)
        if on_error:
            try:
                on_error(exc, log_path())
            except Exception:
                pass
        os._exit(1)

    def handle_thread(args) -> None:
        record(args.exc_type, args.exc_value, args.exc_traceback,
               where=getattr(args.thread, "name", "pekerja"))

    sys.excepthook = handle
    threading.excepthook = handle_thread


def enable_dpi_awareness() -> None:
    """Stop Windows from bitmap-stretching the Tk window on scaled displays."""
    if not IS_WINDOWS:
        return
    try:
        import ctypes
        try:
            # PROCESS_PER_MONITOR_DPI_AWARE
            ctypes.windll.shcore.SetProcessDpiAwareness(2)
        except (AttributeError, OSError):
            ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass


def tk_scaling(root) -> float:
    """Points-per-pixel scaling so Tk fonts match the monitor DPI."""
    try:
        import ctypes
        dpi = ctypes.windll.user32.GetDpiForSystem() if IS_WINDOWS else 96
    except Exception:
        dpi = 96
    return max(1.0, dpi / 72.0)


def chunked(items: Iterable, size: int):
    """Yield lists of at most `size` items."""
    batch = []
    for item in items:
        batch.append(item)
        if len(batch) >= size:
            yield batch
            batch = []
    if batch:
        yield batch
