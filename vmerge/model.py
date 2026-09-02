"""Central data contract shared by every module.

Nothing here touches ffmpeg or Tk; it is pure data so the scanner, the merger
and the GUI can all agree on the same shapes.
"""

from __future__ import annotations

import math
import os
from dataclasses import dataclass, field
from datetime import datetime
from enum import Enum
from typing import Any, Callable, Optional

APP_NAME = "Video Merger"
APP_ID = "vmerge"
APP_VERSION = "1.0.0"

# Extensions we consider "a video" when scanning a folder. Deliberately wide:
# CCTV exports (.dav, .264, .h264) and camcorder formats (.mts, .m2ts) count.
VIDEO_EXTENSIONS: tuple[str, ...] = (
    ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
    ".mpg", ".mpeg", ".m2v", ".ts", ".m2ts", ".mts", ".vob", ".3gp", ".3g2",
    ".asf", ".rm", ".rmvb", ".ogv", ".mxf", ".dav", ".264", ".h264", ".hevc",
    ".divx", ".f4v", ".m4s", ".dv",
)


class SortKey(str, Enum):
    """How the file list is ordered before merging."""

    NAME = "name"                    # natural order, like Windows Explorer
    NAME_PLAIN = "name_plain"        # plain lexicographic, case-insensitive
    MODIFIED = "modified"            # filesystem last-modified time
    CREATED = "created"              # filesystem creation time
    RECORDED = "recorded"            # metadata -> filename -> mtime, tiered
    MEDIA_CREATED = "media_created"  # creation_time tag inside the container
    NAME_TIMESTAMP = "name_ts"       # timestamp parsed out of the filename
    DURATION = "duration"
    SIZE = "size"
    MANUAL = "manual"                # user dragged rows around; keep as-is

    @property
    def label(self) -> str:
        return _SORT_LABELS[self]


_SORT_LABELS = {
    SortKey.NAME: "Nama file (urutan alami)",
    SortKey.NAME_PLAIN: "Nama file (A-Z biasa)",
    SortKey.RECORDED: "Tanggal rekam (otomatis)",
    SortKey.MODIFIED: "Tanggal diubah",
    SortKey.CREATED: "Tanggal dibuat",
    SortKey.MEDIA_CREATED: "Tanggal rekam (metadata)",
    SortKey.NAME_TIMESTAMP: "Tanggal dari nama file",
    SortKey.DURATION: "Durasi",
    SortKey.SIZE: "Ukuran file",
    SortKey.MANUAL: "Urutan manual",
}


class MergeMode(str, Enum):
    """Strategy used to join the clips."""

    AUTO = "auto"            # pick COPY when safe, otherwise RE-ENCODE
    COPY = "copy"            # concat demuxer + -c copy (seconds, lossless)
    REENCODE = "reencode"    # normalise every clip, then encode once
    SMART = "smart"          # re-encode only mismatched clips, then copy-concat

    @property
    def label(self) -> str:
        return _MODE_LABELS[self]


_MODE_LABELS = {
    MergeMode.AUTO: "Otomatis (disarankan)",
    MergeMode.COPY: "Cepat - tanpa encode ulang",
    MergeMode.REENCODE: "Encode ulang semua",
    MergeMode.SMART: "Hemat - encode ulang yang beda saja",
}


class Stage(str, Enum):
    """Which step of the job a progress event belongs to."""

    SCANNING = "scanning"
    PROBING = "probing"
    NORMALIZING = "normalizing"
    MERGING = "merging"
    FINALIZING = "finalizing"
    DONE = "done"
    FAILED = "failed"
    CANCELLED = "cancelled"


@dataclass
class VideoFile:
    """One candidate file plus everything ffprobe told us about it."""

    path: str
    size: int = 0
    mtime: float = 0.0
    ctime: float = 0.0

    # --- filled in by probe.py -------------------------------------------
    probed: bool = False
    valid: bool = False
    error: str = ""
    duration: float = 0.0
    v_duration: float = 0.0   # duration of the video stream itself
    format_name: str = ""
    media_created: Optional[datetime] = None

    has_video: bool = False
    v_codec: str = ""
    v_codec_tag: str = ""
    width: int = 0
    height: int = 0
    pix_fmt: str = ""
    sar: str = ""           # sample aspect ratio, e.g. "1:1"
    fps: float = 0.0        # from avg_frame_rate, for display only
    v_rate: str = ""        # r_frame_rate, normalised: "30000/1001"
    v_time_base: str = ""   # normalised: "1/15360"
    field_order: str = ""
    color_range: str = ""
    color_space: str = ""
    v_profile: str = ""
    v_level: int = 0
    rotation: int = 0       # 0/90/180/270, from display matrix side data
    n_video_streams: int = 0

    has_audio: bool = False
    a_codec: str = ""
    a_codec_tag: str = ""
    sample_rate: int = 0
    channels: int = 0
    channel_layout: str = ""
    a_profile: str = ""
    a_time_base: str = ""
    n_audio_streams: int = 0

    # --- UI state ---------------------------------------------------------
    selected: bool = True
    name_ts: Optional[datetime] = None  # timestamp parsed from the filename

    @property
    def name(self) -> str:
        return os.path.basename(self.path)

    @property
    def resolution(self) -> str:
        return f"{self.width}x{self.height}" if self.width else "-"

    def copy_signature(self) -> tuple:
        """Parameters that must match across clips for `-c copy` to be safe.

        Two clips sharing a signature can be concatenated without re-encoding.

        Every field here was chosen because a difference in it was *measured*
        to corrupt the output while ffmpeg still exited 0 with no warning. The
        two non-obvious ones:

          v_time_base   Two clips identical in every other respect but muxed
                        with different MP4 timescales (1/15360 vs 1/30000)
                        produce an output almost twice as long, with the
                        second half playing at half speed.
          v_rate        30 fps joined to 29.97 fps does the same thing. It is
                        compared as a normalised fraction so that 30000/1001
                        and 90000/3003 count as equal.
          rotation      A phone's display matrix lives in the container, not
                        the frames. Copy-joining a 90-degree clip onto an
                        upright one keeps only the first clip's matrix, so
                        half the video ends up sideways.

        `v_level` is deliberately absent: level 3.0 and 4.1 join cleanly, so
        including it would force needless re-encodes.
        """
        return (
            self.v_codec, self.v_codec_tag, self.v_profile,
            self.width, self.height, self.pix_fmt, self.sar,
            self.field_order, self.color_range, self.color_space,
            self.v_time_base, self.v_rate, self.rotation,
            self.n_video_streams,
            self.a_codec, self.a_codec_tag, self.a_profile,
            self.sample_rate, self.channels, self.channel_layout,
            self.a_time_base, self.n_audio_streams,
        )

    def signature_diff(self, other: "VideoFile") -> list[str]:
        """Human-readable reasons this clip cannot be copy-joined to `other`."""
        pairs = [
            ("codec video", self.v_codec, other.v_codec),
            ("tag codec video", self.v_codec_tag, other.v_codec_tag),
            ("resolusi", self.resolution, other.resolution),
            ("pixel format", self.pix_fmt, other.pix_fmt),
            ("aspect ratio", self.sar, other.sar),
            ("profil video", self.v_profile, other.v_profile),
            ("frame rate", self.v_rate, other.v_rate),
            ("time base video", self.v_time_base, other.v_time_base),
            ("rotasi", self.rotation, other.rotation),
            ("urutan field", self.field_order, other.field_order),
            ("color range", self.color_range, other.color_range),
            ("color space", self.color_space, other.color_space),
            ("jumlah stream video", self.n_video_streams, other.n_video_streams),
            ("codec audio", self.a_codec, other.a_codec),
            ("tag codec audio", self.a_codec_tag, other.a_codec_tag),
            ("profil audio", self.a_profile, other.a_profile),
            ("sample rate", self.sample_rate, other.sample_rate),
            ("jumlah channel", self.channels, other.channels),
            ("layout channel", self.channel_layout, other.channel_layout),
            ("time base audio", self.a_time_base, other.a_time_base),
            ("jumlah stream audio", self.n_audio_streams, other.n_audio_streams),
        ]
        def show(value) -> str:
            # 0 is meaningful here ("0 stream audio"), so only blank strings
            # and None become a dash.
            return "-" if value is None or value == "" else str(value)

        return [f"{what}: {show(a)} vs {show(b)}"
                for what, a, b in pairs if a != b]


@dataclass
class TargetSpec:
    """The uniform parameters every clip is normalised to when re-encoding."""

    width: int = 1920
    height: int = 1080
    fps: float = 30.0
    pix_fmt: str = "yuv420p"
    v_encoder: str = "libx264"
    v_profile: str = ""      # "main"/"high"/... so re-encodes can match sources
    # Measured on 1080p->720p source: veryfast/CRF23 runs at 8.4x realtime
    # (an 8-hour job in ~57 min) for ~2.25 GB, while medium runs at 3.9x
    # (~124 min) for a slightly larger file. ultrafast is a trap: its
    # bitrate ballooned to 5798 kbps, nearly 9x veryfast.
    crf: int = 23
    preset: str = "veryfast"
    a_encoder: str = "aac"
    a_bitrate: str = "192k"
    sample_rate: int = 48000
    channels: int = 2

    def describe(self) -> str:
        return (f"{self.width}x{self.height} @ {self.fps:g}fps, "
                f"{self.v_encoder} crf{self.crf}, "
                f"{self.a_encoder} {self.a_bitrate} {self.sample_rate}Hz")


@dataclass
class MergeJob:
    """Everything the worker needs to produce the output file."""

    files: list[VideoFile]
    output_path: str
    mode: MergeMode = MergeMode.AUTO
    target: TargetSpec = field(default_factory=TargetSpec)
    hwaccel_encoder: str = ""     # "" = software; else h264_nvenc/qsv/amf
    overwrite: bool = True
    faststart: bool = True

    @property
    def total_duration(self) -> float:
        return sum(f.duration for f in self.files if f.duration > 0)


@dataclass
class Progress:
    """One progress tick pushed from the worker thread to the GUI."""

    stage: Stage = Stage.MERGING
    fraction: float = 0.0        # 0..1 overall
    message: str = ""
    current_index: int = 0       # 1-based, for per-file stages
    total_items: int = 0
    seconds_done: float = 0.0
    seconds_total: float = 0.0
    speed: float = 0.0           # ffmpeg "speed=" multiplier
    eta_seconds: float = 0.0
    output_size: int = 0
    log_line: str = ""

    @property
    def percent(self) -> float:
        return max(0.0, min(100.0, self.fraction * 100.0))


ProgressCallback = Callable[[Progress], None]


# ------------------------------------------------------------------ format --

def human_size(num_bytes: float) -> str:
    if num_bytes is None or num_bytes <= 0:
        return "0 B"
    units = ("B", "KB", "MB", "GB", "TB")
    i = min(int(math.log(num_bytes, 1024)), len(units) - 1)
    value = num_bytes / (1024 ** i)
    return f"{value:.0f} {units[i]}" if i == 0 else f"{value:.2f} {units[i]}"


def human_duration(seconds: float) -> str:
    """0 -> '00:00:00'. Always HH:MM:SS so column widths stay stable."""
    if not seconds or seconds < 0 or seconds != seconds:  # NaN-safe
        return "00:00:00"
    total = int(round(seconds))
    return f"{total // 3600:02d}:{(total % 3600) // 60:02d}:{total % 60:02d}"


def human_eta(seconds: float) -> str:
    if not seconds or seconds <= 0 or seconds == float("inf"):
        return "-"
    total = int(round(seconds))
    if total < 60:
        return f"{total} detik"
    if total < 3600:
        return f"{total // 60} menit {total % 60} detik"
    return f"{total // 3600} jam {(total % 3600) // 60} menit"
