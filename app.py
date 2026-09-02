"""Video Merger - entry point.

No arguments  -> graphical interface.
Any argument  -> command line mode (see --help).

The .exe is built as a Windows GUI subsystem binary so double-clicking it does
not flash a console. That leaves it with no stdout, so command-line mode first
borrows the calling terminal (or opens one) before argparse tries to print.
"""

from __future__ import annotations

import multiprocessing
import os
import sys

ATTACH_PARENT_PROCESS = -1


def _ancestor_pids(limit: int = 8):
    """Yield our parent, grandparent, ... PIDs, nearest first.

    Needed because a onefile PyInstaller build runs the Python code in a
    *child* of the bootloader stub. ATTACH_PARENT_PROCESS therefore aims at
    the stub - which owns no console - instead of the cmd.exe the user
    actually typed into, so attaching always failed and a stray console
    window was opened instead.
    """
    import ctypes
    from ctypes import wintypes

    class PROCESSENTRY32(ctypes.Structure):
        _fields_ = [("dwSize", wintypes.DWORD),
                    ("cntUsage", wintypes.DWORD),
                    ("th32ProcessID", wintypes.DWORD),
                    ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
                    ("th32ModuleID", wintypes.DWORD),
                    ("cntThreads", wintypes.DWORD),
                    ("th32ParentProcessID", wintypes.DWORD),
                    ("pcPriClassBase", ctypes.c_long),
                    ("dwFlags", wintypes.DWORD),
                    ("szExeFile", ctypes.c_char * 260)]

    kernel32 = ctypes.windll.kernel32
    snapshot = kernel32.CreateToolhelp32Snapshot(0x00000002, 0)
    if snapshot in (-1, 0xFFFFFFFF, None):
        return
    parents: dict[int, int] = {}
    try:
        entry = PROCESSENTRY32()
        entry.dwSize = ctypes.sizeof(PROCESSENTRY32)
        if kernel32.Process32First(snapshot, ctypes.byref(entry)):
            while True:
                parents[entry.th32ProcessID] = entry.th32ParentProcessID
                if not kernel32.Process32Next(snapshot, ctypes.byref(entry)):
                    break
    except Exception:
        return
    finally:
        kernel32.CloseHandle(snapshot)

    pid = os.getpid()
    seen = {pid}
    for _ in range(limit):
        pid = parents.get(pid, 0)
        if not pid or pid in seen:
            return
        seen.add(pid)
        yield pid


def _ensure_console() -> tuple[bool, bool]:
    """Give a windowed build somewhere to write.

    Returns (have_output, opened_our_own_window).

    Four separate defects lived here, each verified from a real cmd.exe:

    1. Streams that already exist are never touched, so `> out.txt` and pipes
       survive. Reopening CONOUT$ unconditionally emptied the user's file.

    2. stdout and stderr are handled independently. `... > out.txt` leaves a
       valid stdout and a null stderr; treating that as "no streams" threw
       the redirection away.

    3. The console is searched for up the *process tree*, not just at the
       immediate parent. A onefile build sits one stub process below the
       shell, so ATTACH_PARENT_PROCESS could never find it and every run
       opened a stray console window and printed into that instead.

    4. AllocConsole is the last resort, not the second one.
    """
    if os.name != "nt":
        return True, False
    if sys.stdout is not None and sys.stderr is not None:
        return True, False

    import ctypes
    kernel32 = ctypes.windll.kernel32
    opened_own = False

    if not kernel32.GetConsoleWindow():
        attached = bool(kernel32.AttachConsole(ATTACH_PARENT_PROCESS))
        if not attached:
            for pid in _ancestor_pids():
                if kernel32.AttachConsole(pid):
                    attached = True
                    break
        if not attached:
            opened_own = bool(kernel32.AllocConsole())
            if not opened_own:
                return False, False

    for name in ("stdout", "stderr"):
        if getattr(sys, name) is None:
            try:
                setattr(sys, name, open("CONOUT$", "w", encoding="utf-8",
                                        errors="replace", buffering=1))
            except OSError:
                pass
    if sys.stdin is None:
        try:
            sys.stdin = open("CONIN$", "r", encoding="utf-8",
                             errors="replace")
        except OSError:
            pass
    return sys.stdout is not None, opened_own


def _pause_briefly() -> None:
    """Hold our own console open long enough to read, but never forever.

    A bare input() here is what turned an unattended run into a process
    that had to be killed from Task Manager.
    """
    try:
        print("\nTekan tombol apa saja untuk menutup "
              "(otomatis tertutup dalam 30 detik)...", flush=True)
    except (OSError, ValueError):
        return
    try:
        import msvcrt
        import time
        deadline = time.monotonic() + 30.0
        while time.monotonic() < deadline:
            if msvcrt.kbhit():
                msvcrt.getch()
                return
            time.sleep(0.1)
    except Exception:
        pass

def _report_without_console(message: str) -> None:
    """Last resort: no console could be obtained, so use a message box."""
    try:
        import ctypes
        ctypes.windll.user32.MessageBoxW(None, message, "Video Merger", 0x10)
    except Exception:
        pass


def _show_crash(exc: BaseException, log_file: str) -> None:
    """Brief, dismissible notice - never a dialog that blocks forever."""
    try:
        import ctypes
        ctypes.windll.user32.MessageBoxW(
            None,
            f"Video Merger berhenti karena kesalahan tak terduga:"
            f"\n\n{type(exc).__name__}: {exc}\n\n"
            f"Rincian teknis disimpan di:\n{log_file}",
            "Video Merger", 0x10)
    except Exception:
        pass


def main() -> int:
    # PyInstaller onefile + multiprocessing would otherwise re-run the whole
    # app in each child process; harmless to call unconditionally.
    multiprocessing.freeze_support()

    if len(sys.argv) > 1:
        have_output, opened_own = _ensure_console()
        if not have_output:
            _report_without_console(
                "Mode baris perintah butuh jendela terminal.\n"
                "Jalankan lewat Command Prompt / PowerShell, atau buka "
                "aplikasi tanpa argumen untuk tampilan grafis.")
            return 3
        # A Windows console defaults to a legacy code page, so printing
        # a file named "vidéo ñ.mp4" raises UnicodeEncodeError and takes
        # the whole run down. Reconfigure whatever stream we ended up with.
        for stream in (sys.stdout, sys.stderr):
            try:
                stream.reconfigure(encoding="utf-8", errors="replace")
            except (AttributeError, OSError, ValueError):
                pass

        from vmerge.cli import main as cli_main
        code = cli_main(sys.argv[1:])
        if opened_own:
            # Our own console window closes with the process, so hold it
            # open long enough to read. A terminal we merely borrowed stays
            # put, and a redirected run must never block at all.
            _pause_briefly()
        return code

    # DPI awareness has to be claimed before Tk is imported, let alone before
    # a window exists. Doing it later still bumps the reported DPI but leaves
    # Tk with the virtualised screen size, so the window stays blurry on a
    # scaled display. Hence the import sits *after* this call.
    from vmerge.util import enable_dpi_awareness, install_crash_handler
    enable_dpi_awareness()
    install_crash_handler(_show_crash)

    from vmerge.gui import run as gui_run
    return gui_run()


if __name__ == "__main__":
    sys.exit(main())
