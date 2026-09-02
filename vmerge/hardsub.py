"""Burning subtitles permanently into video.

A soft subtitle is a separate track the player has to find, decode and draw.
Plenty of hardware will not: DVD/VCD players, older smart TVs, car head units,
and most USB-stick playback on a TV ignore the track entirely, so the video
plays with no text at all. Burning ("hardsub") paints the text into the
picture, which every player on earth can show because by then it is just
video.

The price is fixed and unavoidable: the picture changes, so the video stream
must be re-encoded. Audio is copied untouched - it is unaffected by subtitles,
and copying it saves both time and a generation of quality loss.

One ffmpeg run per video, each verified afterwards, so a batch of 50 episodes
reports exactly which ones worked.
"""

from __future__ import annotations

import os
import shutil
import tempfile
from dataclasses import dataclass, field
from typing import Callable, Optional

from .ffmpeg_locator import FFmpegTools
from .model import Progress, Stage, TargetSpec, VideoFile, human_duration
from .runner import NEWLINE, Cancelled, FFmpegTask, MergeError
from .subtitle import (BurnPlan, SubtitleError, SubtitleStyle, SubtitleTrack,
                       prepare_burn)
from .util import disk_free, run_capture, unique_path

# Containers that can hold the burned result. MKV and MP4 cover everything a
# TV or DVD player will read off a USB stick.
OUTPUT_EXTENSIONS = (".mp4", ".mkv", ".mov", ".avi", ".ts")


@dataclass
class HardsubItem:
    """One video plus the subtitle chosen for it."""

    video: VideoFile
    track: Optional[SubtitleTrack] = None
    external_path: str = ""
    tracks: list[SubtitleTrack] = field(default_factory=list)
    sidecars: list[str] = field(default_factory=list)
    selected: bool = True
    result_path: str = ""
    error: str = ""

    @property
    def has_source(self) -> bool:
        return bool(self.external_path or self.track)

    @property
    def source_label(self) -> str:
        if self.external_path:
            return os.path.basename(self.external_path)
        if self.track:
            return self.track.label
        return "(belum dipilih)"


@dataclass
class HardsubJob:
    items: list[HardsubItem]
    output_dir: str = ""
    suffix: str = " - hardsub"
    container: str = ".mp4"
    style: SubtitleStyle = field(default_factory=SubtitleStyle)
    target: TargetSpec = field(default_factory=TargetSpec)
    hwaccel_encoder: str = ""
    copy_audio: bool = True
    faststart: bool = True
    overwrite: bool = False

    def output_for(self, item: HardsubItem) -> str:
        stem = os.path.splitext(os.path.basename(item.video.path))[0]
        folder = self.output_dir or os.path.dirname(item.video.path)
        return os.path.join(folder, f"{stem}{self.suffix}{self.container}")


@dataclass
class HardsubResult:
    done: list[str] = field(default_factory=list)
    failed: list[tuple[str, str]] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return bool(self.done) and not self.failed


class Hardsubber(FFmpegTask):
    """Runs one HardsubJob. Not reusable; construct a new one per job."""

    def __init__(self, tools: FFmpegTools, job: HardsubJob,
                 on_progress: Optional[Callable[[Progress], None]] = None,
                 on_log: Optional[Callable[[str], None]] = None):
        super().__init__(tools, on_progress=on_progress, on_log=on_log)
        self.job = job
        self._temp_dir = ""

    # -- main --------------------------------------------------------------
    def run(self) -> HardsubResult:
        items = [i for i in self.job.items if i.selected]
        if not items:
            raise MergeError("Tidak ada video yang dipilih.")

        missing = [i for i in items if not i.has_source]
        if missing:
            names = NEWLINE.join(f"  - {i.video.name}" for i in missing[:8])
            raise MergeError(
                f"{len(missing)} video belum punya subtitle yang dipilih:"
                + NEWLINE + names)

        self._check_inputs_readable(items)
        self._check_disk(items)

        self._temp_dir = tempfile.mkdtemp(prefix="vmerge_sub_")
        result = HardsubResult()
        total_seconds = sum(i.video.duration for i in items) or 1.0
        done_seconds = 0.0

        try:
            for index, item in enumerate(items, start=1):
                self._check_cancel()
                base = done_seconds / total_seconds
                span = item.video.duration / total_seconds
                try:
                    out = self._burn_one(item, index, len(items), base, span)
                    item.result_path = out
                    result.done.append(out)
                except Cancelled:
                    raise
                except (MergeError, SubtitleError, OSError) as exc:
                    # One unreadable episode in a batch of 50 must not throw
                    # away the 49 that worked. Record it and carry on; the
                    # caller reports the list at the end.
                    item.error = str(exc)
                    result.failed.append((item.video.name, str(exc)))
                    self._log(f"GAGAL {item.video.name}: {exc}")
                done_seconds += item.video.duration
        finally:
            self._cleanup_temp()

        if not result.done:
            detail = NEWLINE.join(f"  - {name}: {msg.splitlines()[0]}"
                                  for name, msg in result.failed[:5])
            raise MergeError("Tidak ada video yang berhasil diproses."
                             + NEWLINE + NEWLINE + detail)
        return result

    # -- one file ----------------------------------------------------------
    def _burn_one(self, item: HardsubItem, index: int, count: int,
                  base: float, span: float) -> str:
        out_path = self.job.output_for(item)
        os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)

        if os.path.normcase(os.path.abspath(out_path)) == \
                os.path.normcase(os.path.abspath(item.video.path)):
            # Writing over the input while reading it produces a truncated
            # file and loses the original. Never worth "fixing" silently.
            raise MergeError(
                f"Nama hasil sama dengan video aslinya:{NEWLINE}{out_path}"
                f"{NEWLINE}{NEWLINE}Ubah akhiran nama atau folder tujuan.")
        if os.path.exists(out_path) and not self.job.overwrite:
            out_path = unique_path(out_path)

        self._emit(stage=Stage.NORMALIZING, fraction=base,
                   current_index=index, total_items=count,
                   message=f"Menyiapkan subtitle {index}/{count}: "
                           f"{item.video.name}")

        work = os.path.join(self._temp_dir, f"job{index:04d}")
        plan = prepare_burn(
            self.tools, item.video.path, work,
            track=item.track, external_path=item.external_path,
            style=self.job.style, slot=index)
        self._log(f"Subtitle {item.video.name}: {plan.source_label} "
                  f"({plan.kind})")

        temp_out = os.path.join(work, "out" + self.job.container)
        cmd = self._burn_cmd(item, plan, temp_out)

        self._run_ffmpeg(
            cmd, item.video.duration, base=base, span=span * 0.97,
            stage=Stage.NORMALIZING,
            label=f"Membakar subtitle {index}/{count}",
            current_index=index, total_items=count,
            # Bare filename in the filter; see subtitle.py's module docstring.
            cwd=plan.work_dir if plan.kind == "text" else None)

        self._verify(temp_out, item.video.duration, item.video.name)

        self._emit(stage=Stage.FINALIZING, fraction=base + span * 0.98,
                   current_index=index, total_items=count,
                   message=f"Memindahkan hasil {index}/{count}...")
        # Encode into the temp folder and move afterwards, so an interrupted
        # run never leaves a half-written file sitting next to the originals
        # looking finished.
        shutil.move(temp_out, out_path)
        self._log(f"Selesai: {out_path}")
        return out_path

    def _burn_cmd(self, item: HardsubItem, plan: BurnPlan,
                  out_path: str) -> list[str]:
        cmd = [self.tools.ffmpeg, "-hide_banner", "-y", "-i", item.video.path]

        if plan.kind == "image":
            # Bitmap subtitles are composited, not rendered. overlay needs
            # both inputs in one graph, so this cannot use -vf.
            cmd += ["-filter_complex",
                    f"[0:v][0:s:{plan.stream_index}]overlay[v]",
                    "-map", "[v]"]
        else:
            cmd += ["-vf", plan.filter_arg, "-map", "0:v:0"]

        if item.video.has_audio:
            cmd += ["-map", "0:a"]
            if self.job.copy_audio:
                # Subtitles do not touch audio, so re-encoding it would only
                # cost time and a generation of quality.
                cmd += ["-c:a", "copy"]
            else:
                cmd += ["-c:a", self.job.target.a_encoder,
                        "-b:a", self.job.target.a_bitrate]

        cmd += self._video_flags()
        # The soft subtitle track is deliberately dropped: it is now painted
        # into the picture, and keeping it makes players draw the text twice.
        cmd += ["-sn", "-dn"]
        cmd += self._container_flags(out_path)
        cmd += ["-progress", "pipe:1", "-nostats", out_path]
        return cmd

    def _video_flags(self) -> list[str]:
        target = self.job.target
        encoder = self.job.hwaccel_encoder or "libx264"
        if encoder in ("h264_nvenc", "hevc_nvenc"):
            # Same offset as the merger: NVENC's -cq is not x264's -crf.
            return ["-c:v", encoder, "-preset", "p5", "-rc", "vbr",
                    "-cq", str(min(51, target.crf + 5))]
        if encoder in ("h264_qsv", "hevc_qsv"):
            return ["-c:v", encoder, "-global_quality", str(target.crf)]
        if encoder in ("h264_amf", "hevc_amf"):
            return ["-c:v", encoder, "-quality", "balanced", "-rc", "cqp",
                    "-qp_i", str(target.crf), "-qp_p", str(target.crf)]
        # yuv420p is not the encoder default for 10-bit or 4:2:2 sources, and
        # anything else is exactly what the old players this feature exists
        # for cannot decode.
        return ["-c:v", encoder, "-preset", target.preset,
                "-crf", str(target.crf), "-pix_fmt", "yuv420p"]

    def _container_flags(self, out_path: str) -> list[str]:
        if os.path.splitext(out_path)[1].lower() in (".mp4", ".m4v", ".mov"):
            return ["-movflags", "+faststart"] if self.job.faststart else []
        return []

    # -- checks ------------------------------------------------------------
    def _check_inputs_readable(self, items: list[HardsubItem]) -> None:
        """Open every input now, rather than failing 40 minutes in."""
        bad = []
        for item in items:
            self._check_cancel()
            try:
                with open(item.video.path, "rb") as handle:
                    handle.read(1)
            except OSError as exc:
                bad.append(f"  - {item.video.name}: {exc.strerror or exc}")
        if bad:
            raise MergeError("Video berikut tidak bisa dibaca:" + NEWLINE
                             + NEWLINE.join(bad[:10]))

    def _check_disk(self, items: list[HardsubItem]) -> None:
        """Refuse before starting if the destination clearly cannot hold it."""
        needed = 0
        for item in items:
            out = self.job.output_for(item)
            # Re-encoded output is usually smaller than the source, but a
            # low CRF on a heavily-compressed source can exceed it. 1.2x is
            # a cheap margin that still catches a genuinely full disk.
            needed += int(item.video.size * 1.2)
        folder = self.job.output_dir or os.path.dirname(
            items[0].video.path) or "."
        free = disk_free(folder)
        if free and needed > free:
            raise MergeError(
                f"Ruang disk tidak cukup di {folder}." + NEWLINE
                + f"Perkiraan dibutuhkan {needed / 2**30:.1f} GB, "
                  f"tersedia {free / 2**30:.1f} GB.")

    def _verify(self, out_path: str, expected: float, name: str) -> None:
        """A burned file that is far too short means ffmpeg gave up mid-way.

        Same lesson as the merger: ffmpeg exits 0 after abandoning an input,
        so the duration of what it actually wrote is the only honest check.
        """
        if not os.path.exists(out_path) or os.path.getsize(out_path) == 0:
            raise MergeError(f"Hasil kosong untuk {name}.")
        if expected <= 0:
            return
        actual = _duration_of(self.tools, out_path)
        if actual <= 0:
            raise MergeError(f"Durasi hasil untuk {name} tidak terbaca.")
        # One second, or 1% for very long files - burning does not change the
        # timeline, so anything beyond a rounding difference is a real loss.
        tolerance = max(1.0, expected * 0.01)
        if abs(actual - expected) > tolerance:
            raise MergeError(
                f"Durasi hasil {name} tidak cocok: "
                f"{human_duration(actual)} dari {human_duration(expected)}. "
                f"Video sumber kemungkinan rusak di tengah.")

    # -- temp --------------------------------------------------------------
    def _cleanup_temp(self) -> None:
        if self._temp_dir and os.path.isdir(self._temp_dir):
            shutil.rmtree(self._temp_dir, ignore_errors=True)
        self._temp_dir = ""


def _duration_of(tools: FFmpegTools, path: str) -> float:
    cmd = [tools.ffprobe, "-v", "error", "-show_entries",
           "format=duration", "-of", "default=nw=1:nk=1", path]
    try:
        res = run_capture(cmd, timeout=60)
        return float((res.stdout or "0").strip() or 0)
    except (ValueError, OSError):
        return 0.0


def collect_sources(tools: FFmpegTools, videos: list[VideoFile],
                    cancel: Optional[Callable[[], bool]] = None
                    ) -> list[HardsubItem]:
    """Build one HardsubItem per video, with its subtitle options filled in.

    Embedded tracks and sidecar files are both offered. An embedded track wins
    the default pick when there is one, because a sidecar sitting in the
    folder may well belong to a different release.
    """
    from .subtitle import list_tracks, pick_default_track, sidecar_subs

    items: list[HardsubItem] = []
    for video in videos:
        if cancel and cancel():
            break
        tracks = list_tracks(tools, video.path)
        sidecars = sidecar_subs(video.path)
        item = HardsubItem(video=video, tracks=tracks, sidecars=sidecars)
        chosen = pick_default_track(tracks)
        if chosen is not None:
            item.track = chosen
        elif sidecars:
            item.external_path = sidecars[0]
        else:
            item.selected = False
            item.error = "Tidak ada subtitle di dalam video atau di folder ini"
        items.append(item)
    return items
