"""Building and running the ffmpeg commands that produce the merged file.

Three paths, in increasing cost:

  COPY      one ffmpeg run, concat demuxer + ``-c copy``. No decoding at all,
            so 100 x 5-minute clips finish in seconds. Only valid when every
            clip shares the same codec parameters.

  SMART     re-encode only the clips that differ from the majority, then
            COPY-join everything. Re-probes the re-encoded clips afterwards
            and falls back to REENCODE if they still do not line up.

  REENCODE  normalise every clip to one target spec, then COPY-join.
            Deliberately *not* one big ``-filter_complex concat``: with 100
            inputs that opens 100 files at once, blows past the Windows
            command-line limit, and reports a single opaque progress number.
            Per-clip passes give per-clip progress and survive one bad file.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import threading
import time
from typing import Callable, Optional, Sequence

from .ffmpeg_locator import FFmpegTools
from .model import (MergeJob, MergeMode, Progress, Stage, TargetSpec,
                    VideoFile, human_duration)
from .probe import can_stream_copy, probe_file
from .util import (disk_free, popen_stream, run_capture,
                   terminate_tree, unique_path)


# Containers ffmpeg can reliably MUX into. VIDEO_EXTENSIONS is deliberately
# wider than this because it lists what we can READ; .dav, .264 and friends
# are demuxable but have no output muxer.
MUXABLE_EXTENSIONS = frozenset(
    {".mp4", ".m4v", ".mkv", ".mov", ".ts", ".avi", ".webm", ".flv", ".mpg"})

# ffprobe profile names -> the spellings libx264/libx265 accept.
# ffprobe reports the *decoder* name; these are the encoders that can
# actually write each one back out.
VIDEO_ENCODERS = {
    "h264": "libx264", "hevc": "libx265", "vp9": "libvpx-vp9",
    "vp8": "libvpx", "av1": "libsvtav1", "theora": "libtheora",
    "mpeg4": "mpeg4", "mpeg2video": "mpeg2video",
}
AUDIO_ENCODERS = {
    "aac": "aac", "": "aac", "mp3": "libmp3lame", "ac3": "ac3",
    "eac3": "eac3", "opus": "libopus", "vorbis": "libvorbis",
    "flac": "flac", "alac": "alac", "pcm_s16le": "pcm_s16le",
}

X264_PROFILES = {
    "constrained baseline": "baseline",
    "baseline": "baseline",
    "main": "main",
    "high": "high",
    "high 10": "high10",
    "high 4:2:2": "high422",
    "high 4:4:4 predictive": "high444",
}


NEWLINE = chr(10)


class MergeError(Exception):
    """Raised when the merge cannot continue; message is user-facing."""


class Cancelled(Exception):
    """Raised internally when the user aborts."""


# --------------------------------------------------------- concat list I/O --

def escape_concat_path(path: str) -> str:
    r"""Format one path for the concat demuxer's ``file '...'`` directive.

    Native Windows backslashes are kept. Rewriting them as forward slashes is
    a popular suggestion but fixes nothing on its own - an unescaped quote
    fails either way - and it mangles UNC paths (``\\server\share`` would
    become ``//server/share``).

    The one escape that genuinely matters is the single quote: it would close
    the quoted string, so it is emitted as ``'\''`` - close, escaped quote,
    reopen.
    """
    return os.path.abspath(path).replace("'", "'\\''")


def write_concat_list(paths: Sequence[str], list_path: str) -> str:
    """Write the concat list file and return its path.

    Written as UTF-8 *without* a BOM on purpose: a leading BOM makes ffmpeg
    reject the very first line with ``unknown keyword '?file'``.

    No ``duration`` directives are emitted. They would let ffprobe report the
    total length of the concat input up front, which this app does not need
    (it sums the per-file durations it already probed), and a declared
    duration that disagrees with the real packets by even a few milliseconds
    shifts every following segment.
    """
    with open(list_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("ffconcat version 1.0\n")
        for path in paths:
            fh.write(f"file '{escape_concat_path(path)}'\n")
    return list_path


def _timescale_of(time_base: str) -> int:
    """'1/15360' -> 15360. Returns 0 when the time base is unknown."""
    try:
        num, _, den = time_base.partition("/")
        return int(den) if int(num) == 1 and int(den) > 0 else 0
    except (ValueError, AttributeError):
        return 0


# ------------------------------------------------------- progress plumbing --

def _parse_progress_time(fields: dict) -> float:
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


class Merger:
    """Runs one MergeJob. Not reusable; construct a new one per job."""

    def __init__(self, tools: FFmpegTools, job: MergeJob,
                 on_progress: Optional[Callable[[Progress], None]] = None,
                 on_log: Optional[Callable[[str], None]] = None):
        self.tools = tools
        self.job = job
        self.on_progress = on_progress
        self.on_log = on_log
        self._cancel = threading.Event()
        self._proc: Optional[subprocess.Popen] = None
        self._proc_lock = threading.Lock()
        self._temp_dir = ""
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

    # -- main --------------------------------------------------------------
    def run(self) -> str:
        self._started = time.time()
        files = [f for f in self.job.files if f.selected and f.valid]
        if len(files) < 1:
            raise MergeError("Tidak ada video valid yang dipilih.")
        if len(files) == 1:
            raise MergeError(
                "Hanya satu video yang dipilih - tidak ada yang digabung.")

        out_path = self.job.output_path
        try:
            os.makedirs(os.path.dirname(os.path.abspath(out_path)) or ".",
                        exist_ok=True)
        except OSError as exc:
            raise MergeError(
                "Folder tujuan tidak bisa dibuat atau ditulisi:" + chr(10)
                + chr(10) + f"{out_path}" + chr(10) + chr(10)
                + f"{exc.strerror or exc}") from exc
        if not self.job.overwrite:
            out_path = unique_path(out_path)
        self._check_output_not_input(files, out_path)

        mode = self._decide_mode(files)
        self._check_inputs_readable(files)
        self._check_disk(files, out_path, mode)

        try:
            if mode is MergeMode.COPY:
                self._run_copy(files, out_path)
            else:
                self._run_reencode(files, out_path, smart=(mode is MergeMode.SMART))
        except Cancelled:
            self._cleanup_temp()
            self._remove_partial(out_path)
            self._emit(stage=Stage.CANCELLED, message="Dibatalkan oleh pengguna.")
            raise
        except BaseException:
            self._cleanup_temp()
            raise

        try:
            self._verify_output(out_path, sum(f.duration for f in files),
                                files, mode)
        finally:
            # Verification raises on a bad result, and that path used to skip
            # cleanup entirely - leaving the whole normalised set on disk
            # (tens of GB for a long job) and polluting the next folder scan.
            self._cleanup_temp()
        size = os.path.getsize(out_path) if os.path.exists(out_path) else 0
        elapsed = time.time() - self._started
        self._emit(stage=Stage.DONE, fraction=1.0, output_size=size,
                   message=f"Selesai dalam {human_duration(elapsed)}.")
        return out_path

    # -- verification ------------------------------------------------------
    def _check_inputs_readable(self, files: list[VideoFile]) -> None:
        """Open every input before starting.

        If a file disappears or is locked partway through a concat, ffmpeg
        stops there, finalises the container and exits 0. On an 8-hour job
        that means a cheerful "done" over a video that is minutes long, so the
        list is validated up front - which also catches the network share that
        dropped between scanning and merging.
        """
        missing: list[str] = []
        for f in files:
            try:
                with open(f.path, "rb") as fh:
                    fh.read(1)
            except OSError as exc:
                missing.append(f"{f.name} ({exc.strerror or exc})")
        if missing:
            raise MergeError(
                "Video berikut tidak bisa dibaca lagi (terhapus, dipindah, "
                "sedang dipakai program lain, atau drive jaringan terputus):\n\n"
                + "\n".join("- " + m for m in missing[:10])
                + (f"\n... dan {len(missing) - 10} lainnya"
                   if len(missing) > 10 else ""))

    @staticmethod
    def _duration_tolerance(files: list[VideoFile], mode: MergeMode) -> float:
        """How much the finished duration may differ before it is a failure.

        A percentage is the wrong shape here. At the size this app is built
        for - 100 clips of 5 minutes - a 2% window is 600 seconds, so losing
        two entire clips would still be reported as success.

        Instead the allowance is built from the measured per-join drift
        (about 20 ms per boundary when copying, a few hundred when
        re-encoding through a scale/pad/fps chain) and then capped at half
        the shortest clip, which guarantees that losing any single clip is
        always caught.
        """
        joins = max(0, len(files) - 1)
        per_join = 0.05 if mode is MergeMode.COPY else 0.30
        allowance = max(1.0, per_join * joins)
        shortest = min((f.duration for f in files if f.duration > 0),
                       default=0.0)
        if shortest > 0:
            allowance = min(allowance, max(1.0, shortest * 0.5))
        return allowance

    def _verify_output(self, out_path: str, expected: float,
                       files: list[VideoFile], mode: MergeMode) -> None:
        """Refuse to call a truncated result a success.

        ffmpeg's exit code cannot be trusted here - it reports 0 even when it
        gave up partway through the list - so the finished file is measured
        against the duration the inputs are known to add up to.
        """
        if not os.path.exists(out_path) or os.path.getsize(out_path) == 0:
            raise MergeError("File hasil tidak terbentuk.")
        if expected <= 0:
            return

        try:
            probe = probe_file(self.tools, VideoFile(
                path=out_path, size=os.path.getsize(out_path)))
            actual = probe.duration
        except Exception:
            return          # cannot measure; do not block a possibly-fine file

        if actual <= 0:
            raise MergeError(
                "File hasil terbentuk tapi durasinya tidak terbaca - "
                "kemungkinan besar rusak.")

        tolerance = self._duration_tolerance(files, mode)

        if expected - actual > tolerance:
            raise MergeError(
                "Hasil gabungan TERPOTONG dan tidak bisa dipakai.\n\n"
                f"Durasi seharusnya : {human_duration(expected)}\n"
                f"Durasi yang jadi  : {human_duration(actual)}\n\n"
                "Biasanya ini terjadi karena salah satu video tidak bisa "
                "dibaca di tengah proses (drive terlepas, file terkunci, atau "
                "file rusak). Coba muat ulang daftar lalu ulangi.")

        # The container duration can look perfectly healthy while the video
        # track itself is wrecked - a timestamp mismatch stretches the audio
        # to the expected length and leaves the picture short. So the video
        # stream is measured separately whenever it reports a duration.
        if 0 < probe.v_duration and expected - probe.v_duration > tolerance:
            raise MergeError(
                "Hasil gabungan rusak: gambar dan suara tidak sama "
                "panjang.\n\n"
                f"Durasi seharusnya : {human_duration(expected)}\n"
                f"Durasi gambar     : {human_duration(probe.v_duration)}\n"
                f"Durasi keseluruhan: {human_duration(actual)}\n\n"
                "File tidak bisa dipakai. Coba pilih metode "
                "\"Encode ulang semua\".")

        if actual - expected > tolerance:
            self._log(f"Catatan: durasi hasil ({human_duration(actual)}) sedikit "
                      f"lebih panjang dari perkiraan "
                      f"({human_duration(expected)}).")

    # -- planning ----------------------------------------------------------
    def _decide_mode(self, files: list[VideoFile]) -> MergeMode:
        mode = self.job.mode
        ok, reasons = can_stream_copy(files)
        if mode is MergeMode.AUTO:
            if ok:
                self._log("Semua video punya parameter identik -> mode CEPAT "
                          "(tanpa encode ulang).")
                return MergeMode.COPY
            self._log("Parameter video berbeda-beda, perlu encode ulang:")
            for reason in reasons[:5]:
                self._log("  - " + reason)
            return MergeMode.SMART
        if mode is MergeMode.COPY and not ok:
            raise MergeError(
                "Mode cepat tidak bisa dipakai karena parameter video berbeda:\n\n"
                + "\n".join("- " + r for r in reasons[:6])
                + "\n\nPakai mode Otomatis atau Encode ulang.")
        return mode

    def _check_output_not_input(self, files: list[VideoFile],
                                out_path: str) -> None:
        target = os.path.normcase(os.path.abspath(out_path))
        for f in files:
            if os.path.normcase(os.path.abspath(f.path)) == target:
                raise MergeError(
                    "File keluaran sama dengan salah satu video sumber "
                    f"({f.name}). Pilih nama lain.")

    def _estimate_output_bytes(self, files: list[VideoFile],
                               mode: MergeMode) -> int:
        """Rough size of the finished file, used only for the disk check.

        Stream copy is easy: the output is the inputs, minus a little because
        100 separate container headers collapse into one.

        Re-encoding is estimated from pixels rather than from a fixed MB per
        second. A flat rate is badly wrong in both directions - 2.2 MB/s would
        demand 126 GB for an 8-hour 720p job that really produces about 8 GB,
        and would refuse to start on any ordinary disk.
        """
        total_in = sum(f.size for f in files)
        if mode is MergeMode.COPY:
            return int(total_in * 1.02)

        duration = sum(f.duration for f in files)
        # _check_disk runs before the target is chosen, so estimate from the
        # footage itself. Using job.target here priced every job as 1080p30 -
        # a 4x overstatement for a folder of 640x480 CCTV clips.
        probe = self._auto_target(files) if files else self.job.target
        pixels_per_second = (max(1, probe.width * probe.height)
                             * max(1.0, probe.fps))
        # ~0.08 bits per pixel is a generous CRF 23 figure; clamped so an odd
        # target spec cannot produce a nonsensical demand.
        bits_per_second = min(25_000_000, max(800_000, pixels_per_second * 0.08))
        estimated = duration * bits_per_second / 8.0
        return int(estimated * 1.25)

    def _check_disk(self, files: list[VideoFile], out_path: str,
                    mode: MergeMode) -> None:
        need = self._estimate_output_bytes(files, mode)
        if mode is not MergeMode.COPY:
            # The normalised clips live alongside the final file until the
            # join finishes, so the peak requirement is roughly double.
            need *= 2
        free = disk_free(os.path.dirname(os.path.abspath(out_path)) or ".")
        if free and need > free:
            raise MergeError(
                f"Ruang disk kemungkinan tidak cukup.\n"
                f"Perkiraan dibutuhkan: {need / 1024**3:.1f} GB\n"
                f"Tersedia: {free / 1024**3:.1f} GB")

    # -- fast path ---------------------------------------------------------
    def _run_copy(self, files: list[VideoFile], out_path: str) -> None:
        self._temp_dir = self._make_temp(out_path)
        list_path = write_concat_list(
            [f.path for f in files], os.path.join(self._temp_dir, "concat.txt"))

        total = sum(f.duration for f in files)
        self._emit(stage=Stage.MERGING, message="Menggabungkan (mode cepat)...",
                   seconds_total=total, total_items=len(files))

        cmd = [self.tools.ffmpeg, "-hide_banner", "-y",
               "-f", "concat", "-safe", "0", "-i", list_path,
               "-c", "copy",
               "-map", "0",
               # Kept because an 8-hour join can push the muxing queue past
               # its default 10s window and abort. The usual companions
               # (-fflags +genpts, -avoid_negative_ts) are deliberately NOT
               # here: measured against ffmpeg 8.1.1 they changed nothing for
               # the concat demuxer, including on raw H.264 and on inputs with
               # a non-zero start time.
               "-max_interleave_delta", "0"]
        cmd += self._container_flags(out_path)
        cmd += ["-progress", "pipe:1", "-nostats", out_path]

        self._run_ffmpeg(cmd, total, base=0.0, span=1.0, stage=Stage.MERGING,
                         label="Menggabungkan")

    # -- re-encode path ----------------------------------------------------
    def _resolve_encoder(self) -> str:
        """Confirm the requested encoder actually works, else fall back to CPU.

        `ffmpeg -encoders` lists what the build was compiled with, not what
        this machine can run: h264_amf is listed on a box with no AMD driver
        and dies on the first frame. Encoding one throwaway frame settles it
        in well under a second, which beats discovering it four hours into an
        eight-hour job. Judged on the exit code only - some encoders (SVT-AV1)
        print a banner to stderr even when they succeed.
        """
        encoder = self.job.hwaccel_encoder
        if not encoder:
            return ""
        cmd = [self.tools.ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
               "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=1",
               "-c:v", encoder, "-frames:v", "1", "-f", "null", "-"]
        try:
            if run_capture(cmd, timeout=45).returncode == 0:
                return encoder
        except (OSError, subprocess.SubprocessError):
            pass
        self._log(f"Encoder {encoder} tidak bisa dipakai di komputer ini "
                  f"(driver/GPU tidak mendukung). Beralih ke CPU.")
        return ""

    def _run_reencode(self, files: list[VideoFile], out_path: str,
                      smart: bool) -> None:
        self.job.hwaccel_encoder = self._resolve_encoder()
        self._temp_dir = self._make_temp(out_path)
        target = self.job.target
        majority = self._majority_signature(files) if smart else None

        pass_through = []
        to_encode = []
        for f in files:
            if smart and majority is not None and f.copy_signature() == majority:
                pass_through.append(f)
            else:
                to_encode.append(f)

        # Re-encoded clips must land in the same container with the same
        # timescale as the clips they will be copy-joined to. Writing them to
        # .mkv (timebase 1/1000) and joining them to .mp4 sources (1/15360)
        # is exactly the mismatch that silently stretches the output.
        temp_ext = ".mkv"
        timescale = 0

        if smart and to_encode:
            sample = next((f for f in files
                           if f.copy_signature() == majority), None)
            source_ext = (os.path.splitext(sample.path)[1].lower()
                          if sample is not None else "")
            if sample is None or source_ext not in MUXABLE_EXTENSIONS:
                # The untouched clips sit in a container ffmpeg can read but
                # not write (.dav from a Dahua DVR, .264, .divx...), so there
                # is no way to produce segments that copy-join to them.
                # Re-encoding everything is slower but actually works.
                self._log(f"Wadah {source_ext or '(tidak dikenal)'} tidak bisa "
                          f"ditulis ulang; semua video di-encode ulang.")
                smart = False
            else:
                target = self._target_from_signature(files, majority) or target
                temp_ext = source_ext
                timescale = _timescale_of(sample.v_time_base)
                self._log(
                    f"Mode hemat: {len(pass_through)} video dipakai apa adanya, "
                    f"{len(to_encode)} video di-encode ulang ke "
                    f"{target.describe()}.")

        if not smart:
            to_encode = list(files)
            pass_through = []
            target = self._auto_target(files)
            # Everything is re-encoded with identical settings, so any single
            # container works; MKV avoids MP4's index rewrite on every clip.
            temp_ext = ".mkv"
            timescale = 0
            self._log(f"Encode ulang semua video ke {target.describe()}.")

        total_encode = sum(f.duration for f in to_encode) or 1.0
        # Re-encoding dominates the runtime; reserve the last slice for the join.
        encode_span = 0.94 if to_encode else 0.0
        done_seconds = 0.0
        normalised: dict[str, str] = {}

        for index, f in enumerate(to_encode, start=1):
            self._check_cancel()
            temp_out = os.path.join(self._temp_dir,
                                    f"norm_{index:04d}{temp_ext}")
            cmd = self._normalize_cmd(f, temp_out, target, timescale)
            base = (done_seconds / total_encode) * encode_span
            span = (f.duration / total_encode) * encode_span
            self._emit(stage=Stage.NORMALIZING, fraction=base,
                       current_index=index, total_items=len(to_encode),
                       message=f"Encode ulang {index}/{len(to_encode)}: {f.name}")
            self._run_ffmpeg(cmd, f.duration, base=base, span=span,
                             stage=Stage.NORMALIZING,
                             label=f"Encode {index}/{len(to_encode)}",
                             current_index=index, total_items=len(to_encode))
            if not os.path.exists(temp_out) or os.path.getsize(temp_out) == 0:
                raise MergeError(f"Gagal meng-encode ulang: {f.name}")
            normalised[f.path] = temp_out
            done_seconds += f.duration

        join_base = encode_span
        if smart and to_encode:
            join_base = self._verify_smart(files, normalised, target,
                                           encode_span, temp_ext, timescale)

        ordered = [normalised.get(f.path, f.path) for f in files]
        list_path = write_concat_list(
            ordered, os.path.join(self._temp_dir, "concat.txt"))

        total = sum(f.duration for f in files)
        self._emit(stage=Stage.MERGING, fraction=join_base,
                   message="Menyatukan hasil...", seconds_total=total)

        cmd = [self.tools.ffmpeg, "-hide_banner", "-y",
               "-f", "concat", "-safe", "0", "-i", list_path,
               "-c", "copy", "-map", "0",
               "-max_interleave_delta", "0"]
        cmd += self._container_flags(out_path)
        cmd += ["-progress", "pipe:1", "-nostats", out_path]
        self._run_ffmpeg(cmd, total, base=join_base, span=1.0 - join_base,
                         stage=Stage.MERGING, label="Menyatukan")

    def _verify_smart(self, files: list[VideoFile], normalised: dict,
                      target: TargetSpec, encode_span: float,
                      temp_ext: str = ".mkv", timescale: int = 0) -> float:
        """Confirm re-encoded clips really do match the pass-through clips.

        If libx264 produced parameters that still differ, copy-joining would
        yield a file that plays only the first segment correctly, so we
        re-encode the remaining clips too rather than ship a broken video.
        """
        signatures = set()
        for f in files:
            path = normalised.get(f.path)
            if path:
                probe = probe_file(self.tools, VideoFile(
                    path=path, size=os.path.getsize(path)))
                signatures.add(probe.copy_signature())
            else:
                signatures.add(f.copy_signature())
            self._check_cancel()

        if len(signatures) <= 1:
            return encode_span

        self._log("Parameter hasil encode masih berbeda dari video asli; "
                  "meng-encode ulang sisanya agar hasil tidak rusak.")
        remaining = [f for f in files if f.path not in normalised]
        total_remaining = sum(f.duration for f in remaining) or 1.0
        done = 0.0
        # This pass was not in the plan, so it has no budget of its own. Let
        # it creep across the sliver reserved for the join rather than pinning
        # the bar at 94% for what can be a long stretch.
        fixup_span = max(0.0, 1.0 - encode_span) * 0.6
        for index, f in enumerate(remaining, start=1):
            self._check_cancel()
            temp_out = os.path.join(self._temp_dir,
                                    f"fix_{index:04d}{temp_ext}")
            self._run_ffmpeg(
                self._normalize_cmd(f, temp_out, target, timescale),
                f.duration,
                base=encode_span + (done / total_remaining) * fixup_span,
                span=(f.duration / total_remaining) * fixup_span,
                stage=Stage.NORMALIZING,
                label=f"Menyamakan {index}/{len(remaining)}",
                current_index=index, total_items=len(remaining))
            normalised[f.path] = temp_out
            done += f.duration
        return encode_span + fixup_span

    # -- command building --------------------------------------------------
    def _normalize_cmd(self, f: VideoFile, out_path: str,
                       target: TargetSpec, timescale: int = 0) -> list[str]:
        """Re-encode one clip to exactly `target`, letterboxing if needed."""
        # scale keeps the aspect ratio, pad centres it on a fixed canvas, and
        # setsar pins the pixel aspect so the concat sees identical geometry.
        vf = (f"scale={target.width}:{target.height}"
              f":force_original_aspect_ratio=decrease:flags=bicubic,"
              f"pad={target.width}:{target.height}"
              f":(ow-iw)/2:(oh-ih)/2:color=black,"
              f"setsar=1,fps={target.fps:g},format={target.pix_fmt}")

        cmd = [self.tools.ffmpeg, "-hide_banner", "-y",
               "-i", f.path]

        if not f.has_audio:
            # A clip with no audio would desync everything after it, so give
            # it silence with exactly the target audio parameters.
            layout = "stereo" if target.channels == 2 else "mono"
            cmd += ["-f", "lavfi", "-i",
                    f"anullsrc=channel_layout={layout}:"
                    f"sample_rate={target.sample_rate}"]
            cmd += ["-map", "0:v:0", "-map", "1:a:0", "-shortest"]
        else:
            cmd += ["-map", "0:v:0", "-map", "0:a:0"]

        cmd += ["-vf", vf]
        cmd += self._video_encoder_flags(target)
        # async=1 stretches/squeezes to fill gaps rather than letting a clip
        # with a ragged audio track drift against its own video, and
        # first_pts=0 pins the track to zero so segments butt together
        # cleanly. Both matter for CCTV audio, which is frequently gappy.
        layout = "stereo" if target.channels == 2 else "mono"
        cmd += ["-af", f"aresample={target.sample_rate}:async=1:first_pts=0,"
                       f"aformat=sample_fmts=fltp:"
                       f"sample_rates={target.sample_rate}:"
                       f"channel_layouts={layout}"]
        cmd += ["-c:a", target.a_encoder, "-b:a", target.a_bitrate,
                "-ar", str(target.sample_rate), "-ac", str(target.channels)]
        if os.path.splitext(out_path)[1].lower() in (".mp4", ".m4v", ".mov"):
            # Matching the destination timescale is what keeps these segments
            # copy-joinable with the untouched clips around them.
            if timescale:
                cmd += ["-video_track_timescale", str(timescale)]
            # Deliberately NOT -avoid_negative_ts/-muxdelay 0 here. They look
            # like a fix in isolation - a run where every segment is written
            # this way gains nothing and loses nothing - but this code path
            # only ever runs in the mode where normalised segments are joined
            # to UNTOUCHED originals, and those carry the ordinary AAC
            # priming offset. Measured on that mix: with the flags 2
            # non-monotonic-DTS warnings and 6.063 s from 6.0; without them
            # 0 warnings and 6.041 s. The segments have to follow the same
            # timestamp convention as the files they sit between.

        cmd += ["-map_metadata", "-1", "-map_chapters", "-1",
                "-progress", "pipe:1", "-nostats", out_path]
        return cmd

    def _video_encoder_flags(self, target: TargetSpec) -> list[str]:
        encoder = self.job.hwaccel_encoder or target.v_encoder
        if encoder in ("h264_nvenc", "hevc_nvenc"):
            # NVENC's -cq is not x264's -crf. libx264 -crf 23 measured
            # 1743 kbps where h264_nvenc -cq 23 gave 7070 kbps - a 4x bigger
            # file for the same nominal number. The right offset is
            # content-dependent (+10 matched on bitrate for one clip, +5 on
            # another) and matching bitrate understates quality anyway, since
            # NVENC is less efficient per bit. +5 errs toward keeping quality.
            return ["-c:v", encoder, "-preset", "p5", "-rc", "vbr",
                    "-cq", str(min(51, target.crf + 5)),
                    "-pix_fmt", target.pix_fmt]
        if encoder in ("h264_qsv", "hevc_qsv"):
            return ["-c:v", encoder, "-global_quality", str(target.crf),
                    "-pix_fmt", target.pix_fmt]
        if encoder in ("h264_amf", "hevc_amf"):
            return ["-c:v", encoder, "-quality", "balanced",
                    "-rc", "cqp", "-qp_i", str(target.crf),
                    "-qp_p", str(target.crf), "-pix_fmt", target.pix_fmt]
        flags = ["-c:v", encoder, "-preset", target.preset,
                 "-crf", str(target.crf), "-pix_fmt", target.pix_fmt]
        if target.v_profile and encoder in ("libx264", "libx265"):
            # Without this libx264 always writes High. CCTV sources are
            # usually Main or Baseline, so the re-encoded clips never matched
            # the untouched ones and the "only fix the odd files out" mode
            # quietly fell back to re-encoding all 100 of them.
            flags += ["-profile:v", target.v_profile]
        return flags + [
                # Fixed GOP keeps every normalised clip structurally identical.
                "-g", str(int(max(1, round(target.fps * 2))))]

    def _container_flags(self, out_path: str) -> list[str]:
        ext = os.path.splitext(out_path)[1].lower()
        if ext in (".mp4", ".m4v", ".mov"):
            flags = ["-movflags", "+faststart"] if self.job.faststart else []
            # Timed-metadata / data streams cannot live in MP4 and would abort
            # the mux; dropping them is harmless for a video merge.
            return flags + ["-dn", "-map", "-0:d?", "-map", "-0:t?"]
        return []

    # -- target selection --------------------------------------------------
    @staticmethod
    def _majority_signature(files: list[VideoFile]):
        counts: dict = {}
        for f in files:
            counts[f.copy_signature()] = counts.get(f.copy_signature(), 0) + 1
        return max(counts, key=lambda k: counts[k]) if counts else None

    @staticmethod
    def _target_from_signature(files: list[VideoFile],
                               signature) -> Optional[TargetSpec]:
        """Build a TargetSpec that reproduces the majority group exactly."""
        sample = next((f for f in files if f.copy_signature() == signature), None)
        if sample is None:
            return None
        encoder = VIDEO_ENCODERS.get(sample.v_codec, "libx264")
        return TargetSpec(
            # yuv420p needs even dimensions; a 1919-pixel-wide source would
            # otherwise make libx264 refuse to start.
            width=sample.width + (sample.width % 2),
            height=sample.height + (sample.height % 2),
            fps=sample.fps or 30.0,
            pix_fmt=sample.pix_fmt or "yuv420p",
            v_encoder=encoder,
            # The profile only means anything to the x26x encoders.
            v_profile=(X264_PROFILES.get(sample.v_profile.strip().lower(), "")
                       if encoder in ("libx264", "libx265") else ""),
            # ffprobe reports decoder names, which are not always encoder
            # names: "opus" and "vorbis" decode fine but only libopus and
            # libvorbis can encode, so passing the probe result straight
            # through made ffmpeg abort with "Unknown encoder".
            a_encoder=AUDIO_ENCODERS.get(sample.a_codec, "aac"),
            sample_rate=sample.sample_rate or 48000,
            channels=sample.channels or 2,
        )

    def _auto_target(self, files: list[VideoFile]) -> TargetSpec:
        """Target for a full re-encode: the most common frame size wins.

        Using the *modal* size rather than the maximum avoids upscaling 99
        clips just because one stray 4K file is in the folder.
        """
        import dataclasses
        # A copy: this is also called from the disk estimate, long before the
        # user has committed to anything, and it used to overwrite the job's
        # own target as a side effect.
        base = dataclasses.replace(self.job.target)
        sizes: dict = {}
        for f in files:
            if f.width and f.height:
                sizes[(f.width, f.height)] = sizes.get((f.width, f.height), 0) + 1
        if sizes:
            width, height = max(sizes, key=lambda k: sizes[k])
            base.width, base.height = width, height
        rates = [f.fps for f in files if f.fps > 0]
        if rates:
            base.fps = max(set(rates), key=rates.count)
        if base.width % 2:
            base.width += 1
        if base.height % 2:
            base.height += 1
        return base

    # -- process handling --------------------------------------------------
    def _run_ffmpeg(self, cmd: list[str], duration: float, base: float,
                    span: float, stage: Stage, label: str,
                    current_index: int = 0, total_items: int = 0) -> None:
        """Run one ffmpeg invocation, streaming -progress into callbacks."""
        self._check_cancel()
        self._log("$ " + " ".join(
            f'"{c}"' if " " in c else c for c in cmd[:1] + cmd[1:]))

        proc = popen_stream(cmd, merge_stderr=False, stdin_pipe=True)
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

                seconds = _parse_progress_time(fields)
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
            if ("impossible to open" in lowered
                    or "error opening input" in lowered
                    or "error during demuxing" in lowered):
                raise MergeError(
                    "FFmpeg gagal membuka salah satu video di tengah "
                    "proses, sehingga hasilnya tidak lengkap.\n\n" + line)

        if code != 0:
            if self._cancel.is_set():
                raise Cancelled()
            detail = NEWLINE.join(stderr_tail[-8:]) or "(tidak ada pesan)"
            # Windows reports these unsigned, so -22 arrives as 4294967274.
            shown = code - 2 ** 32 if code > 2 ** 31 else code
            raise MergeError(f"FFmpeg gagal (kode {shown})."
                             + NEWLINE + NEWLINE + detail)

    # -- temp management ---------------------------------------------------
    def _make_temp(self, out_path: str) -> str:
        """Temp folder on the same volume as the output, so renames are cheap.

        Includes the object id as well as the pid so two merges running from
        one process (or two app windows) cannot share - and then delete -
        each other's working files.
        """
        parent = os.path.dirname(os.path.abspath(out_path)) or "."
        path = os.path.join(parent, f".vmerge_tmp_{os.getpid()}_{id(self):x}")
        try:
            os.makedirs(path, exist_ok=True)
        except OSError as exc:
            raise MergeError(
                "Tidak bisa menulis di folder tujuan:" + chr(10) + chr(10)
                + f"{parent}" + chr(10) + chr(10)
                + f"{exc.strerror or exc}" + chr(10) + chr(10)
                + "Pilih folder lain (mis. Documents atau drive D:).") from exc
        return path

    def _cleanup_temp(self) -> None:
        if self._temp_dir and os.path.isdir(self._temp_dir):
            shutil.rmtree(self._temp_dir, ignore_errors=True)
        self._temp_dir = ""

    @staticmethod
    def _remove_partial(out_path: str) -> None:
        """A half-written output is unplayable; do not leave it behind."""
        try:
            if os.path.exists(out_path):
                os.unlink(out_path)
        except OSError:
            pass
