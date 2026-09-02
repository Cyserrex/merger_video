"""Finding subtitles and turning them into something ffmpeg can burn in.

"Hardsub" means painting the subtitle into the picture itself, so it survives
being played on a TV, a DVD/VCD player, or any app that ignores the soft
subtitle track sitting beside the video. It is the one operation here that
*always* costs a full re-encode: the pixels change, so nothing can be copied.

Two shapes of subtitle exist and they need entirely different treatment:

  TEXT   subrip/ass/ssa/mov_text/webvtt - rendered by libass. Reached with the
         ``subtitles`` filter, which takes a *file path*.

  IMAGE  hdmv_pgs_subtitle (Blu-ray), dvd_subtitle (DVD), dvb_subtitle - these
         are pictures already. libass cannot touch them; they are composited
         with ``overlay`` straight from the input stream, no path involved.

The path handling below looks paranoid because the ``subtitles`` filter parses
its argument three times over (filter-graph split, option split, then libass),
so a Windows path like ``D:\\Video\\Anime [2024]\\ep01.srt`` hits a colon, two
backslashes and a pair of brackets that all mean something. Rather than escape
all of that, every text subtitle is materialised into the job's temp folder
under a plain ASCII name and referenced by bare filename with ffmpeg's working
directory set there. That is immune to every character a filename can hold.
"""

from __future__ import annotations

import json
import os
import shutil
from dataclasses import dataclass, field
from typing import Optional

from .ffmpeg_locator import FFmpegTools
from .util import run_capture

# Subtitle codecs libass can render. mov_text is what MP4 files carry.
TEXT_SUB_CODECS = frozenset({
    "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "subviewer",
    "microdvd", "sami", "realtext", "stl", "subviewer1", "vplayer", "pjs",
    "mpl2", "jacosub",
})

# Picture-based subtitles: composited, never rendered.
IMAGE_SUB_CODECS = frozenset({
    "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub",
    "hdmv_text_subtitle",
})

# Sidecar files that sit next to a video. Order matters: .ass carries styling
# that .srt cannot, so it wins when a release ships both.
SIDECAR_EXTENSIONS = (".ass", ".ssa", ".srt", ".vtt", ".sub", ".smi", ".ttml")

PROBE_TIMEOUT = 60.0

# Two-letter and three-letter tags seen in the wild, mapped to something a
# person reading the dropdown will recognise.
_LANGUAGE_NAMES = {
    "ind": "Indonesia", "id": "Indonesia", "in": "Indonesia",
    "eng": "Inggris", "en": "Inggris",
    "jpn": "Jepang", "ja": "Jepang",
    "kor": "Korea", "ko": "Korea",
    "zho": "Mandarin", "chi": "Mandarin", "zh": "Mandarin",
    "ara": "Arab", "ar": "Arab",
    "may": "Melayu", "msa": "Melayu", "ms": "Melayu",
    "tha": "Thai", "th": "Thai",
    "vie": "Vietnam", "vi": "Vietnam",
    "spa": "Spanyol", "es": "Spanyol",
    "fra": "Prancis", "fre": "Prancis", "fr": "Prancis",
    "deu": "Jerman", "ger": "Jerman", "de": "Jerman",
    "nld": "Belanda", "dut": "Belanda", "nl": "Belanda",
    "por": "Portugis", "pt": "Portugis",
    "rus": "Rusia", "ru": "Rusia",
    "hin": "Hindi", "hi": "Hindi",
    "und": "tidak diketahui",
}


def language_name(tag: str) -> str:
    """'ind' -> 'Indonesia'. Unknown tags come back unchanged."""
    if not tag:
        return ""
    return _LANGUAGE_NAMES.get(tag.strip().lower(), tag)


@dataclass
class SubtitleTrack:
    """One subtitle stream inside a video file.

    `stream_index` is the index *among subtitle streams* (the N in ``0:s:N``),
    not the absolute stream index, because that is what -map and the
    ``subtitles`` filter's ``si`` option both want.
    """

    stream_index: int
    codec: str = ""
    language: str = ""
    title: str = ""
    forced: bool = False
    default: bool = False

    @property
    def is_text(self) -> bool:
        return self.codec in TEXT_SUB_CODECS

    @property
    def is_image(self) -> bool:
        return self.codec in IMAGE_SUB_CODECS

    @property
    def burnable(self) -> bool:
        return self.is_text or self.is_image

    @property
    def label(self) -> str:
        """What the dropdown shows."""
        bits = [f"#{self.stream_index + 1}"]
        name = language_name(self.language)
        if name:
            bits.append(name)
        title = (self.title or "").strip()
        if title and title.lower() not in (name.lower(),
                                           self.language.lower()):
            bits.append(title)
        marks = []
        if self.default:
            marks.append("bawaan")
        if self.forced:
            marks.append("paksa")
        if self.is_image:
            marks.append("gambar")
        text = " - ".join(bits)
        if marks:
            text += f" ({', '.join(marks)})"
        return text


@dataclass
class SubtitleStyle:
    """Appearance overrides applied to text subtitles.

    Only used when `enabled`; otherwise an .ass keeps its own styling, which
    is almost always what a release group intended.
    """

    enabled: bool = False
    font: str = "Arial"
    size: int = 24
    primary: str = "#FFFFFF"      # fill
    outline_color: str = "#000000"
    outline: float = 2.0
    shadow: float = 0.0
    bold: bool = False
    margin_v: int = 20            # distance from the bottom edge, in points

    def force_style(self) -> str:
        """Build libass's force_style string.

        Colours in .ass are &HAABBGGRR - alpha first, then *reversed* RGB.
        Writing them as RRGGBB is the classic way to end up with blue text
        that should have been red.
        """
        parts = [
            f"FontName={self.font}",
            f"FontSize={self.size}",
            f"PrimaryColour={_ass_colour(self.primary)}",
            f"OutlineColour={_ass_colour(self.outline_color)}",
            f"BorderStyle=1",
            f"Outline={self.outline:g}",
            f"Shadow={self.shadow:g}",
            f"Bold={1 if self.bold else 0}",
            f"Alignment=2",
            f"MarginV={self.margin_v}",
        ]
        return ",".join(parts)


def _ass_colour(hex_rgb: str) -> str:
    """'#FF8800' -> '&H000088FF' (opaque, BGR order)."""
    text = (hex_rgb or "").strip().lstrip("#")
    if len(text) != 6:
        return "&H00FFFFFF"
    try:
        r, g, b = (int(text[i:i + 2], 16) for i in (0, 2, 4))
    except ValueError:
        return "&H00FFFFFF"
    return f"&H00{b:02X}{g:02X}{r:02X}"


# --------------------------------------------------------------- discovery --
def list_tracks(tools: FFmpegTools, path: str) -> list[SubtitleTrack]:
    """Every subtitle stream in `path`, in stream order. [] if none/unreadable."""
    cmd = [
        tools.ffprobe, "-v", "error", "-select_streams", "s",
        "-show_entries",
        "stream=index,codec_name:stream_tags=language,title:"
        "stream_disposition=default,forced",
        "-of", "json", path,
    ]
    try:
        res = run_capture(cmd, timeout=PROBE_TIMEOUT)
        if res.returncode != 0:
            return []
        data = json.loads(res.stdout or "{}")
    except Exception:
        return []

    tracks: list[SubtitleTrack] = []
    for order, stream in enumerate(data.get("streams") or []):
        tags = stream.get("tags") or {}
        disp = stream.get("disposition") or {}
        tracks.append(SubtitleTrack(
            stream_index=order,
            codec=(stream.get("codec_name") or "").lower(),
            language=tags.get("language") or "",
            title=tags.get("title") or "",
            forced=bool(disp.get("forced")),
            default=bool(disp.get("default")),
        ))
    return tracks


def sidecar_subs(video_path: str) -> list[str]:
    """Subtitle files sitting beside the video, best candidate first.

    Matches both ``ep01.srt`` and the very common ``ep01.id.srt`` /
    ``ep01.indonesian.srt`` naming, since anything starting with the video's
    own stem is almost certainly its subtitle.
    """
    folder = os.path.dirname(os.path.abspath(video_path))
    stem = os.path.splitext(os.path.basename(video_path))[0].lower()
    if not stem or not os.path.isdir(folder):
        return []

    found: list[tuple[int, int, str]] = []
    try:
        entries = os.listdir(folder)
    except OSError:
        return []

    for name in entries:
        base, ext = os.path.splitext(name)
        ext = ext.lower()
        if ext not in SIDECAR_EXTENSIONS:
            continue
        lowered = base.lower()
        if lowered == stem:
            rank = 0                       # exact match
        elif lowered.startswith(stem):
            rank = 1                       # ep01.id.srt
        else:
            continue
        found.append((rank, SIDECAR_EXTENSIONS.index(ext),
                      os.path.join(folder, name)))

    found.sort(key=lambda item: (item[0], item[1], item[2].lower()))
    return [path for _rank, _ext, path in found]


def pick_default_track(tracks: list[SubtitleTrack]) -> Optional[SubtitleTrack]:
    """The track a user most likely means: Indonesian, else default, else first.

    Forced tracks are skipped when anything else is available - they carry
    only the foreign-language lines, so burning one in place of the full
    subtitle silently produces a video with almost no text.
    """
    usable = [t for t in tracks if t.burnable]
    if not usable:
        return None
    full = [t for t in usable if not t.forced] or usable

    for tag in ("ind", "id", "in"):
        for track in full:
            if track.language.lower() == tag:
                return track
    for track in full:
        if track.default:
            return track
    return full[0]


# ------------------------------------------------------------- preparation --
@dataclass
class BurnPlan:
    """Everything needed to burn one subtitle into one video.

    `filter_arg` is a complete ffmpeg filter string. For text subtitles it
    names a file that lives in `work_dir`, and ffmpeg MUST be run with that as
    its working directory - see the module docstring.
    """

    kind: str                      # "text" | "image"
    filter_arg: str = ""           # text: "subtitles=sub.ass:..."
    stream_index: int = 0          # image: which 0:s:N to overlay
    work_dir: str = ""
    source_label: str = ""         # shown to the user
    extras: list[str] = field(default_factory=list)


class SubtitleError(Exception):
    """Subtitle could not be prepared; carries a message meant for the user."""


def prepare_burn(tools: FFmpegTools, video_path: str, work_dir: str,
                 track: Optional[SubtitleTrack] = None,
                 external_path: str = "",
                 style: Optional[SubtitleStyle] = None,
                 slot: int = 0) -> BurnPlan:
    """Materialise the chosen subtitle and return how to burn it.

    Exactly one source is used: `external_path` if given, otherwise `track`.
    """
    os.makedirs(work_dir, exist_ok=True)

    if external_path:
        return _plan_from_file(external_path, work_dir, style, slot)
    if track is None:
        raise SubtitleError("Tidak ada subtitle yang dipilih.")
    if track.is_image:
        # Nothing to extract: overlay reads the stream straight from the input.
        return BurnPlan(kind="image", stream_index=track.stream_index,
                        work_dir=work_dir,
                        source_label=f"trek {track.label}")
    if not track.is_text:
        raise SubtitleError(
            f"Jenis subtitle '{track.codec}' tidak bisa dibakar.")
    return _plan_from_embedded(tools, video_path, track, work_dir, style, slot)


def _safe_name(slot: int, ext: str) -> str:
    """A filename with nothing in it that any parser could misread."""
    return f"sub{slot:04d}{ext}"


def _plan_from_file(path: str, work_dir: str,
                    style: Optional[SubtitleStyle], slot: int) -> BurnPlan:
    if not os.path.isfile(path):
        raise SubtitleError(f"Berkas subtitle tidak ditemukan:\n{path}")
    ext = os.path.splitext(path)[1].lower()
    if ext not in SIDECAR_EXTENSIONS:
        raise SubtitleError(f"Format subtitle '{ext}' tidak dikenali.")

    local = os.path.join(work_dir, _safe_name(slot, ext))
    try:
        shutil.copyfile(path, local)
    except OSError as exc:
        raise SubtitleError(f"Gagal menyalin berkas subtitle:\n{exc}") from exc
    if os.path.getsize(local) == 0:
        raise SubtitleError(f"Berkas subtitle kosong:\n{path}")

    return BurnPlan(kind="text",
                    filter_arg=_text_filter(os.path.basename(local), style),
                    work_dir=work_dir,
                    source_label=os.path.basename(path))


def _plan_from_embedded(tools: FFmpegTools, video_path: str,
                        track: SubtitleTrack, work_dir: str,
                        style: Optional[SubtitleStyle], slot: int) -> BurnPlan:
    """Pull one embedded text track out to an .ass in the work folder.

    Extracting rather than pointing libass at the video itself is deliberate:
    ``subtitles=movie.mkv:si=2`` makes libass open and index the *whole* video
    a second time, which on a multi-GB file costs seconds per run and re-reads
    it from disk. The extraction is one cheap pass and the result is tiny.

    .ass is the extraction target even for SRT input because it is libass's
    native format, so nothing is lost on the way in.
    """
    local = os.path.join(work_dir, _safe_name(slot, ".ass"))
    cmd = [tools.ffmpeg, "-hide_banner", "-y", "-i", video_path,
           "-map", f"0:s:{track.stream_index}", "-c:s", "ass", local]
    res = run_capture(cmd, timeout=PROBE_TIMEOUT * 5)

    if res.returncode != 0 or not os.path.exists(local) \
            or os.path.getsize(local) == 0:
        detail = (res.stderr or "").strip().splitlines()
        raise SubtitleError(
            "Gagal mengeluarkan subtitle dari video.\n\n"
            + ("\n".join(detail[-3:]) if detail else "(tidak ada pesan)"))

    return BurnPlan(kind="text",
                    filter_arg=_text_filter(os.path.basename(local), style),
                    work_dir=work_dir,
                    source_label=f"trek {track.label}")


def _text_filter(local_name: str, style: Optional[SubtitleStyle]) -> str:
    """``subtitles=sub0001.ass`` plus styling, if the user asked for any.

    `local_name` is a bare ASCII filename by construction, so it needs no
    escaping at all - which is the entire point of copying the file here.
    """
    arg = f"subtitles={local_name}"
    if style and style.enabled:
        # Single quotes keep the commas inside force_style from being read as
        # filter separators.
        arg += f":force_style='{style.force_style()}'"
    return arg
