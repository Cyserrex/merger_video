"""Running one ffmpeg process: progress, cancellation, and failure detection.

Extracted from merger.py so the subtitle burner reuses it rather than growing
a second copy. The hard-won part is not the launching - it is knowing when
ffmpeg has actually failed, because the exit code frequently says 0 when the
output is wrong:

  * an input that cannot be opened mid-run is logged, then ffmpeg finalises
    whatever it already has and exits 0
  * pressing 'q' to cancel also exits 0

So the exit code is only one of three signals here, alongside the cancel flag
and a scan of the tail of stderr.
"""

from __future__ import annotations

import subprocess
import threading
import time
from typing import Callable, Optional

from .model import Progress, Stage, human_duration
from .util import popen_stream, terminate_tree

NEWLINE = chr(10)

# Lines ffmpeg logs when it gives up on an input but still exits successfully.
FATAL_LOG_MARKERS = (
    "impossible to open",
    "error opening input",
    "error during demuxing",
)


class MergeError(Exception):
    """Raised when the job cannot continue; message is user-facing."""


class Cancelled(Exception):
    """Raised internally when the user aborts."""


def parse_progress_time(fields: dict) -> float:
    """Seconds encoded so far, from one ``-progress`` block.

    ``out_time_us`` is authoritative. ``out_time_ms`` is a long-standing
    ffmpeg misnomer - it also carries microseconds - so it is divided by 1e6,
    not 1e3.
    """
    for key, divisor in (("out_time_us", 1e6), ("out_time_ms", 1e6)):
        raw = fields.get(key)
        if raw and raw not in ("N/A", "-9223372036854775807"):
            try:
                value = int(raw) / divisor
                if value >= 0:
                    return value
            except ValueError:
                pass
    raw = fields.get("out_time", "")
    if raw and raw != "N/A":
        try:
            neg = raw.startswith("-")
            hh, mm, ss = raw.lstrip("-").split(":")
            value = int(hh) * 3600 + int(mm) * 60 + float(ss)
            return 0.0 if neg else value
        except ValueError:
            pass
    return 0.0


class FFmpegTask:
    """Base for anything that drives ffmpeg with progress and a Cancel button.

    Subclasses get `_run_ffmpeg`, `cancel`, and the reporting helpers. They
    are responsible for their own temp folders and for what the commands say.
    """

    def __init__(self, tools,
                 on_progress: Optional[Callable[[Progress], None]] = None,
                 on_log: Optional[Callable[[str], None]] = None):
        self.tools = tools
        self.on_progress = on_progress
        self.on_log = on_log
        self._cancel = threading.Event()
        self._proc: Optional[subprocess.Popen] = None
        self._proc_lock = threading.Lock()
        self._started = 0.0

    # -- control -----------------------------------------------------------
    def cancel(self) -> None:
        """Ask the running ffmpeg to stop. Safe to call from the GUI thread."""
        self._cancel.set()
        with self._proc_lock:
            proc = self._proc
        if proc and proc.poll() is None:
            # 'q' lets ffmpeg flush and close the container cleanly; if it is
            # wedged, terminate_tree() below is the backstop.
            try:
                if proc.stdin and not proc.stdin.closed:
                    proc.stdin.write("q")
                    proc.stdin.flush()
            except (OSError, ValueError):
                pass

            # The reader loop only notices the cancel flag when the next
            # progress block arrives. If ffmpeg is wedged it will never send
            # one, and the loop would block forever - so the escalation runs
            # on its own timer rather than behind that loop.
            def watchdog(target=proc) -> None:
                try:
                    target.wait(timeout=10)
                except subprocess.SubprocessError:
                    terminate_tree(target)

            threading.Thread(target=watchdog, daemon=True).start()

    @property
    def cancelled(self) -> bool:
        return self._cancel.is_set()

    def _check_cancel(self) -> None:
        if self._cancel.is_set():
            raise Cancelled()

    # -- reporting ---------------------------------------------------------
    def _log(self, text: str) -> None:
        if self.on_log and text:
            self.on_log(text.rstrip())

    def _emit(self, **kwargs) -> None:
        if self.on_progress:
            self.on_progress(Progress(**kwargs))

    # -- the run -----------------------------------------------------------
    def _run_ffmpeg(self, cmd: list[str], duration: float, base: float,
                    span: float, stage: Stage, label: str,
                    current_index: int = 0, total_items: int = 0,
                    cwd: Optional[str] = None) -> None:
        """Run one ffmpeg invocation, streaming -progress into callbacks."""
        self._check_cancel()
        self._log("$ " + " ".join(
            f'"{c}"' if " " in c else c for c in cmd[:1] + cmd[1:]))

        proc = popen_stream(cmd, merge_stderr=False, stdin_pipe=True, cwd=cwd)
        with self._proc_lock:
            self._proc = proc

        stderr_tail: list[str] = []

        def drain_stderr() -> None:
            try:
                for line in proc.stderr:      # type: ignore[union-attr]
                    line = line.rstrip()
                    if not line:
                        continue
                    stderr_tail.append(line)
                    del stderr_tail[:-40]
                    self._log(line)
            except (OSError, ValueError):
                pass

        reader = threading.Thread(target=drain_stderr, daemon=True)
        reader.start()

        fields: dict = {}
        last_emit = 0.0
        try:
            for line in proc.stdout:          # type: ignore[union-attr]
                if self._cancel.is_set():
                    break
                line = line.strip()
                if "=" not in line:
                    continue
                key, _, value = line.partition("=")
                fields[key] = value
                if key != "progress":
                    continue

                seconds = parse_progress_time(fields)
                frac_local = min(1.0, seconds / duration) if duration > 0 else 0.0
                try:
                    speed = float((fields.get("speed") or "0").rstrip("x") or 0)
                except ValueError:
                    speed = 0.0
                try:
                    out_size = int(fields.get("total_size") or 0)
                except ValueError:
                    out_size = 0

                remaining = (duration - seconds) / speed if speed > 0.01 else 0.0
                now = time.time()
                # Throttle: ffmpeg emits a block ~2x/second per file and the
                # Tk queue does not need more than that in aggregate.
                if now - last_emit >= 0.20 or value == "end":
                    last_emit = now
                    if value == "end":
                        # Everything has been written, but ffmpeg is not done:
                        # +faststart now rewrites the whole file to move the
                        # index to the front, which on a 30 GB output takes
                        # minutes. Without this the bar sits at 100% and the
                        # app looks hung.
                        self._emit(stage=Stage.FINALIZING,
                                   fraction=base + span,
                                   message="Menyusun indeks video "
                                           "(bisa lama untuk file besar)...",
                                   current_index=current_index,
                                   total_items=total_items)
                        fields.clear()
                        continue
                    self._emit(stage=stage,
                               fraction=base + span * frac_local,
                               message=f"{label} - {human_duration(seconds)}"
                                       f" / {human_duration(duration)}",
                               current_index=current_index,
                               total_items=total_items,
                               seconds_done=seconds, seconds_total=duration,
                               speed=speed, eta_seconds=remaining,
                               output_size=out_size)
                fields.clear()
        except (OSError, ValueError):
            pass

        if self._cancel.is_set():
            # cancel() already sent 'q'. Give ffmpeg a few seconds to close the
            # container properly before resorting to killing the tree, so no
            # orphaned ffmpeg keeps writing to the temp folder we are about to
            # delete. Note ffmpeg exits 0 after a 'q', which is why the cancel
            # flag - not the exit code - decides what happened here.
            try:
                proc.wait(timeout=8)
            except subprocess.TimeoutExpired:
                terminate_tree(proc)
            reader.join(timeout=2)
            with self._proc_lock:
                self._proc = None
            raise Cancelled()

        code = proc.wait()
        reader.join(timeout=5)
        with self._proc_lock:
            self._proc = None

        # An input that cannot be opened does not make ffmpeg fail. It logs a
        # line, stops reading the list there, finalises what it has and exits
        # 0. Catching the log line is the only way to tell that apart from a
        # genuine success at the moment it happens.
        for line in stderr_tail:
            lowered = line.lower()
            if any(marker in lowered for marker in FATAL_LOG_MARKERS):
                raise MergeError(
                    "FFmpeg gagal membuka salah satu video di tengah "
                    "proses, sehingga hasilnya tidak lengkap." + NEWLINE
                    + NEWLINE + line)

        if code != 0:
            if self._cancel.is_set():
                raise Cancelled()
            detail = NEWLINE.join(stderr_tail[-8:]) or "(tidak ada pesan)"
            # Windows reports these unsigned, so -22 arrives as 4294967274.
            shown = code - 2 ** 32 if code > 2 ** 31 else code
            raise MergeError(f"FFmpeg gagal (kode {shown})."
                             + NEWLINE + NEWLINE + detail)
