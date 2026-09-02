"""Ordering the clip list.

The order chosen here *is* the order of the finished video, so "looks right in
Explorer" matters more than "is lexicographically pure". The default therefore
delegates to the same Win32 comparator Explorer itself uses, with a pure-Python
natural sort as the fallback on non-Windows or if the DLL call is unavailable.
"""

from __future__ import annotations

import functools
import os
import re
from datetime import datetime
from typing import Callable, Iterable, Optional

from .model import SortKey, VideoFile
from .util import IS_WINDOWS

# --------------------------------------------------------- natural ordering --

_NUM_RE = re.compile(r"(\d+)")


def natural_key(text: str) -> tuple:
    """Fallback comparator: split digits from text so 'v2' < 'v10'.

    Digit runs compare numerically; everything else compares case-folded. The
    trailing type tag keeps int and str from ever being compared directly.
    """
    parts = _NUM_RE.split(text)
    key: list = []
    for part in parts:
        if part.isdigit():
            # -len so that, for equal values, the more zero-padded spelling
            # sorts first: Explorer orders video05 before video5.
            key.append((1, int(part), -len(part), ""))
        elif part:
            key.append((0, 0, 0, part.casefold()))
    return tuple(key)


def _make_win32_comparator() -> Optional[Callable[[str, str], int]]:
    """Wrap StrCmpLogicalW, the exact comparator Windows Explorer sorts with."""
    if not IS_WINDOWS:
        return None
    try:
        import ctypes
        from ctypes import wintypes

        shlwapi = ctypes.WinDLL("shlwapi", use_last_error=True)
        func = shlwapi.StrCmpLogicalW
        func.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
        func.restype = ctypes.c_int
        # Smoke-test it before trusting it.
        if func("a2", "a10") >= 0:
            return None
        return func
    except (OSError, AttributeError, ValueError):
        return None


_win32_cmp = _make_win32_comparator()


def explorer_sort_key(text: str):
    """Sort key matching Windows Explorer, or natural_key if unavailable.

    The trailing raw name is a tiebreaker, not decoration: StrCmpLogicalW
    reports "a.mp4" and "A.mp4" as *equal*, which would leave the order of
    such a pair dependent on the order they happened to be scanned in. That
    cannot happen inside one NTFS folder, but a recursive scan can easily
    collect both from different subfolders.
    """
    if _win32_cmp is None:
        return (natural_key(text), text)
    return (functools.cmp_to_key(_win32_cmp)(text), text)


# ------------------------------------------------- timestamps in file names --

# Ordered most-specific first; the first pattern that yields a valid datetime
# wins. Anchored loosely because CCTV exporters bolt prefixes/suffixes on.
_TS_PATTERNS: tuple[tuple[re.Pattern, str], ...] = (
    # 2024-01-05 08.00.00 / 2024-01-05_08-00-00 / 2024_01_05 08:00:00
    (re.compile(r"(20\d{2})[-_.]?(\d{2})[-_.]?(\d{2})"
                r"[ _T-]+(\d{2})[-_.:]?(\d{2})[-_.:]?(\d{2})"), "ymdhms"),
    # 20240105080000 (14 digits, no separators) - the common CCTV form
    (re.compile(r"(?<!\d)(20\d{2})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})"
                r"(?:\d{3})?(?!\d)"), "ymdhms"),
    # 05-01-2024 08.00.00  (day first)
    (re.compile(r"(?<!\d)(\d{2})[-_.](\d{2})[-_.](20\d{2})"
                r"[ _T-]+(\d{2})[-_.:]?(\d{2})[-_.:]?(\d{2})"), "dmyhms"),
    # date only: 2024-01-05 / 20240105
    (re.compile(r"(?<!\d)(20\d{2})[-_.]?(\d{2})[-_.]?(\d{2})(?!\d)"), "ymd"),
)


def parse_timestamp_from_name(name: str) -> Optional[datetime]:
    """Best-effort recording time recovered from a file name.

    Returns None rather than guessing when nothing plausible is present, so
    callers can fall back to filesystem times.
    """
    stem = os.path.splitext(os.path.basename(name))[0]
    for pattern, kind in _TS_PATTERNS:
        for match in pattern.finditer(stem):
            groups = [int(g) for g in match.groups()]
            try:
                if kind == "ymdhms":
                    dt = datetime(*groups)  # type: ignore[arg-type]
                elif kind == "dmyhms":
                    day, month, year, hh, mm, ss = groups
                    dt = datetime(year, month, day, hh, mm, ss)
                else:
                    dt = datetime(groups[0], groups[1], groups[2])
            except ValueError:
                continue  # e.g. month 13 - not really a timestamp
            if 2000 <= dt.year <= 2099:
                return dt
    return None


# ----------------------------------------------------------------- sorting --

_FAR_FUTURE = datetime(9999, 1, 1)


def sort_files(files: Iterable[VideoFile], key: SortKey,
               descending: bool = False) -> list[VideoFile]:
    """Return a new list ordered by `key`.

    Files missing the chosen key (no metadata date, unparseable name) sink to
    the bottom in a stable way instead of scattering: they keep their previous
    relative order, sorted by name as a tiebreaker.
    """
    items = list(files)
    if key is SortKey.MANUAL:
        return items

    # Name is the universal tiebreaker, so start from name order.
    items.sort(key=lambda f: explorer_sort_key(f.name))

    if key is SortKey.NAME:
        pass
    elif key is SortKey.NAME_PLAIN:
        items.sort(key=lambda f: f.name.casefold())
    elif key is SortKey.MODIFIED:
        items.sort(key=lambda f: f.mtime)
    elif key is SortKey.CREATED:
        items.sort(key=lambda f: f.ctime)
    elif key is SortKey.SIZE:
        items.sort(key=lambda f: f.size)
    elif key is SortKey.DURATION:
        items.sort(key=lambda f: f.duration)
    elif key is SortKey.MEDIA_CREATED:
        items.sort(key=lambda f: (f.media_created is None,
                                  f.media_created or _FAR_FUTURE))
    elif key is SortKey.NAME_TIMESTAMP:
        items.sort(key=lambda f: (f.name_ts is None, f.name_ts or _FAR_FUTURE))
    elif key is SortKey.RECORDED:
        # Best available answer to "when was this filmed", in order of how
        # much we trust it. Filesystem creation time is never consulted: it
        # becomes the copy time the moment footage is pulled off an SD card,
        # which would collapse every clip onto the same instant.
        items.sort(key=_recorded_at)

    if descending:
        items.reverse()
    return items


def _recorded_at(f: VideoFile) -> datetime:
    """Recording time: container metadata, else the filename, else mtime."""
    if f.media_created:
        return f.media_created
    if f.name_ts:
        return f.name_ts
    try:
        return datetime.fromtimestamp(f.mtime)
    except (OSError, OverflowError, ValueError):
        return _FAR_FUTURE


def move_items(order: list, indices: Iterable[int], delta: int) -> list[int]:
    """Move the rows at `indices` up (delta<0) or down (delta>0) in place.

    Returns the new indices of the moved rows. A block that has reached the
    edge simply stops, which is what a user expects from Up/Down buttons.
    """
    idx = sorted(indices, reverse=delta > 0)
    if not idx:
        return []
    moved: list[int] = []
    for i in idx:
        j = i + delta
        if j < 0 or j >= len(order):
            moved.append(i)
            continue
        if j in moved:          # blocked by an already-parked sibling
            moved.append(i)
            continue
        order[i], order[j] = order[j], order[i]
        moved.append(j)
    return sorted(moved)
