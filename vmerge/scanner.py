"""Walking a folder and collecting candidate video files."""

from __future__ import annotations

import os
from typing import Callable, Iterable, Optional

from .model import VIDEO_EXTENSIONS, VideoFile
from .sorting import parse_timestamp_from_name


FILE_ATTRIBUTE_REPARSE_POINT = 0x400


def _is_reparse_point(entry) -> bool:
    """True for junctions, symlinks and other reparse points."""
    try:
        attrs = getattr(entry.stat(follow_symlinks=False),
                        "st_file_attributes", 0)
    except OSError:
        return False
    return bool(attrs & FILE_ATTRIBUTE_REPARSE_POINT)


def is_video_name(name: str, extensions: Iterable[str] = VIDEO_EXTENSIONS) -> bool:
    return os.path.splitext(name)[1].lower() in tuple(extensions)


def scan_folder(folder: str, recursive: bool = False,
                extensions: Iterable[str] = VIDEO_EXTENSIONS,
                cancel: Optional[Callable[[], bool]] = None,
                on_found: Optional[Callable[[int], None]] = None
                ) -> list[VideoFile]:
    """Collect every video-looking file under `folder`.

    Files are returned unsorted and unprobed; `stat()` is read here because it
    is cheap and the date columns need it before ffprobe has run. Unreadable
    entries are skipped rather than raising, since a single locked file in a
    100-file folder should not abort the whole scan.
    """
    exts = tuple(e.lower() for e in extensions)
    found: list[VideoFile] = []
    seen: set[str] = set()

    def walk(path: str, depth: int) -> None:
        if cancel and cancel():
            return
        try:
            entries = list(os.scandir(path))
        except OSError:
            return
        for entry in entries:
            if cancel and cancel():
                return
            try:
                if _is_reparse_point(entry):
                    # Windows junctions are plain directories to os.scandir,
                    # follow_symlinks=False and all. One pointing back at an
                    # ancestor turns a recursive scan into an endless walk
                    # (measured: 1536 files found in a folder holding 24).
                    continue
                if entry.is_dir(follow_symlinks=False):
                    if recursive and depth < 24:
                        walk(entry.path, depth + 1)
                    continue
                if not entry.is_file(follow_symlinks=False):
                    continue
                if os.path.splitext(entry.name)[1].lower() not in exts:
                    continue
                real = os.path.normcase(os.path.abspath(entry.path))
                if real in seen:
                    continue
                seen.add(real)
                st = entry.stat()
                found.append(VideoFile(
                    path=os.path.abspath(entry.path),
                    size=st.st_size,
                    mtime=st.st_mtime,
                    # On Windows st_ctime is the creation time, which is what
                    # the "Tanggal dibuat" column should show.
                    ctime=getattr(st, "st_birthtime", st.st_ctime),
                    name_ts=parse_timestamp_from_name(entry.name),
                ))
                if on_found:
                    on_found(len(found))
            except OSError:
                continue

    walk(os.path.abspath(folder), 0)
    return found
