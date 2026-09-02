"""Reading media parameters with ffprobe.

Probing is what lets the app decide between the seconds-long stream-copy path
and the hours-long re-encode path, so it runs on every file before merging.
One ffprobe call per file at ~30 ms each would be 3 s for 100 files serially;
a small thread pool brings that down and keeps the UI responsive.
"""

from __future__ import annotations

import json
import os
import subprocess
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from fractions import Fraction
from typing import Callable, Iterable, Optional

from .ffmpeg_locator import FFmpegTools
from .model import VideoFile
from .util import run_capture

PROBE_TIMEOUT = 60.0


def _parse_rate(value: str) -> float:
    """'30000/1001' -> 29.97. Returns 0.0 for '0/0' and junk."""
    if not value:
        return 0.0
    try:
        frac = Fraction(value)
        return float(frac) if frac.denominator else 0.0
    except (ValueError, ZeroDivisionError):
        return 0.0


def _normalise_fraction(value: str) -> str:
    """'90000/3003' -> '30000/1001', so equal rates compare as equal strings.

    Comparing these as raw strings is what makes an otherwise-identical pair of
    clips look incompatible (or worse, a genuinely different pair look
    compatible), so every rate and time base goes through here.
    """
    if not value:
        return ""
    try:
        frac = Fraction(value)
    except (ValueError, ZeroDivisionError):
        return value
    if frac.denominator == 0:
        return value
    return f"{frac.numerator}/{frac.denominator}"


def _parse_creation_time(raw: str) -> Optional[datetime]:
    """Container creation_time is ISO-8601 UTC, e.g. 2024-01-05T08:00:00.000000Z."""
    if not raw:
        return None
    text = raw.strip().replace("Z", "+00:00")
    try:
        dt = datetime.fromisoformat(text)
    except ValueError:
        for fmt in ("%Y-%m-%dT%H:%M:%S.%f%z", "%Y-%m-%dT%H:%M:%S%z",
                    "%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S"):
            try:
                dt = datetime.strptime(text, fmt)
                break
            except ValueError:
                continue
        else:
            return None
    # Normalise to naive local time so it sorts against filesystem timestamps.
    if dt.tzinfo is not None:
        dt = dt.astimezone().replace(tzinfo=None)
    return dt


def _rotation_of(stream: dict) -> int:
    """Display rotation in degrees, from side data or the legacy tag."""
    for side in stream.get("side_data_list") or []:
        if "rotation" in side:
            try:
                return int(round(float(side["rotation"]))) % 360
            except (TypeError, ValueError):
                pass
    tag = (stream.get("tags") or {}).get("rotate")
    if tag:
        try:
            return int(float(tag)) % 360
        except ValueError:
            pass
    return 0


def probe_file(tools: FFmpegTools, video: VideoFile) -> VideoFile:
    """Fill `video` in place with what ffprobe reports. Never raises."""
    video.probed = True
    video.valid = False

    if video.size == 0:
        video.error = "File kosong (0 byte)"
        return video

    cmd = [
        tools.ffprobe, "-v", "error", "-hide_banner",
        "-print_format", "json",
        "-show_format", "-show_streams",
        "-i", video.path,
    ]
    try:
        res = run_capture(cmd, timeout=PROBE_TIMEOUT)
    except subprocess.TimeoutExpired:
        video.error = "ffprobe timeout - file mungkin rusak"
        return video
    except OSError as exc:
        video.error = f"Gagal menjalankan ffprobe: {exc}"
        return video

    if res.returncode != 0:
        msg = (res.stderr or "").strip().splitlines()
        video.error = msg[-1] if msg else "Bukan file media yang valid"
        return video

    try:
        data = json.loads(res.stdout or "{}")
    except ValueError:
        video.error = "Output ffprobe tidak bisa dibaca"
        return video

    fmt = data.get("format") or {}
    streams = data.get("streams") or []

    video.format_name = fmt.get("format_name", "")
    video.duration = _parse_duration(fmt, streams)
    video.media_created = _parse_creation_time(
        (fmt.get("tags") or {}).get("creation_time", ""))

    v_streams = [s for s in streams if s.get("codec_type") == "video"
                 and not _is_cover_art(s)]
    a_streams = [s for s in streams if s.get("codec_type") == "audio"]

    video.n_video_streams = len(v_streams)
    video.n_audio_streams = len(a_streams)
    video.has_video = bool(v_streams)
    video.has_audio = bool(a_streams)

    if v_streams:
        s = v_streams[0]
        video.v_codec = s.get("codec_name", "")
        video.v_codec_tag = s.get("codec_tag_string", "") or ""
        video.width = int(s.get("width") or 0)
        video.height = int(s.get("height") or 0)
        video.pix_fmt = s.get("pix_fmt", "")
        video.sar = s.get("sample_aspect_ratio", "") or "1:1"
        video.fps = (_parse_rate(s.get("avg_frame_rate", ""))
                     or _parse_rate(s.get("r_frame_rate", "")))
        video.v_rate = _normalise_fraction(s.get("r_frame_rate", ""))
        video.v_time_base = _normalise_fraction(s.get("time_base", ""))
        video.field_order = s.get("field_order", "") or ""
        video.color_range = s.get("color_range", "") or ""
        video.color_space = s.get("color_space", "") or ""
        video.v_profile = str(s.get("profile", "") or "")
        try:
            video.v_level = int(s.get("level") or 0)
        except (TypeError, ValueError):
            video.v_level = 0
        video.rotation = _rotation_of(s)
        try:
            video.v_duration = float(s.get("duration") or 0)
        except (TypeError, ValueError):
            video.v_duration = 0.0
        if not video.media_created:
            video.media_created = _parse_creation_time(
                (s.get("tags") or {}).get("creation_time", ""))

    if a_streams:
        s = a_streams[0]
        video.a_codec = s.get("codec_name", "")
        video.a_codec_tag = s.get("codec_tag_string", "") or ""
        try:
            video.sample_rate = int(s.get("sample_rate") or 0)
        except (TypeError, ValueError):
            video.sample_rate = 0
        video.channels = int(s.get("channels") or 0)
        video.channel_layout = s.get("channel_layout", "") or (
            {1: "mono", 2: "stereo"}.get(video.channels, ""))
        video.a_profile = str(s.get("profile", "") or "")
        video.a_time_base = _normalise_fraction(s.get("time_base", ""))

    if not video.has_video:
        video.error = "Tidak ada stream video di dalam file"
        return video
    if video.duration <= 0:
        video.error = "Durasi tidak terbaca - file mungkin rusak/terpotong"
        return video

    video.valid = True
    video.error = ""
    return video


def _is_cover_art(stream: dict) -> bool:
    """Embedded thumbnails show up as a 1-frame video stream; ignore them."""
    disposition = stream.get("disposition") or {}
    if disposition.get("attached_pic"):
        return True
    return stream.get("codec_name") in ("mjpeg", "png", "bmp") and \
        stream.get("avg_frame_rate") in ("0/0", "", None)


def _parse_duration(fmt: dict, streams: list) -> float:
    """Container duration, falling back to the longest stream.

    Raw streams (.h264, some .dav exports) carry no container duration at all,
    in which case the stream value is the only thing available.
    """
    try:
        value = float(fmt.get("duration") or 0)
        if value > 0:
            return value
    except (TypeError, ValueError):
        pass
    best = 0.0
    for s in streams:
        try:
            best = max(best, float(s.get("duration") or 0))
        except (TypeError, ValueError):
            continue
    return best


def probe_many(tools: FFmpegTools, files: Iterable[VideoFile],
               workers: int = 8,
               on_progress: Optional[Callable[[int, int, VideoFile], None]] = None,
               cancel: Optional[Callable[[], bool]] = None) -> list[VideoFile]:
    """Probe a batch in parallel, reporting progress as each one lands."""
    items = list(files)
    total = len(items)
    if not total:
        return items

    workers = max(1, min(workers, total, (os.cpu_count() or 4) * 2))
    done = 0
    # Not a `with` block: ThreadPoolExecutor.__exit__ waits for every queued
    # task, and its worker threads are non-daemon with an atexit hook. That
    # combination kept the process alive for a dozen seconds after the window
    # closed - the app looked shut but was not.
    pool = ThreadPoolExecutor(max_workers=workers)
    try:
        futures = {pool.submit(probe_file, tools, f): f for f in items}
        for future, video in futures.items():
            if cancel and cancel():
                break
            try:
                future.result()
            except Exception as exc:            # defensive: never lose the batch
                video.probed = True
                video.valid = False
                video.error = f"Gagal memeriksa: {exc}"
            done += 1
            if on_progress:
                on_progress(done, total, video)
    finally:
        pool.shutdown(wait=False, cancel_futures=True)
    return items


# ------------------------------------------------------- compatibility ------

def group_by_signature(files: Iterable[VideoFile]) -> dict:
    """Map copy_signature() -> list of files sharing it."""
    groups: dict = {}
    for f in files:
        groups.setdefault(f.copy_signature(), []).append(f)
    return groups


def can_stream_copy(files: Iterable[VideoFile]) -> tuple[bool, list[str]]:
    """Can these clips be joined with `-c copy`? Returns (ok, reasons_if_not)."""
    valid = [f for f in files if f.valid]
    if len(valid) < 2:
        return True, []
    first = valid[0]
    reasons: list[str] = []
    for other in valid[1:]:
        diff = first.signature_diff(other)
        if diff:
            reasons.append(f"{other.name}: " + "; ".join(diff))
    # Mixing "has audio" with "no audio" silently truncates audio after the
    # first clip, so treat it as a hard blocker even though the signature
    # comparison above already catches it.
    return (not reasons), reasons[:10]
