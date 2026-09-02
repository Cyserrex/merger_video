"""User preferences, persisted as JSON under %APPDATA%\\vmerge\\settings.json.

Every read is defensive: a corrupt or hand-edited file must never stop the app
from starting, so unknown keys are ignored and bad values fall back to defaults.
"""

from __future__ import annotations

import json
import os
import tempfile
from typing import Any

from .model import APP_ID, MergeMode, SortKey

DEFAULTS: dict[str, Any] = {
    "last_input_dir": "",
    "last_output_dir": "",
    "recursive": False,
    "sort_key": SortKey.NAME.value,
    "sort_desc": False,
    "merge_mode": MergeMode.AUTO.value,
    "output_container": ".mp4",
    "crf": 23,
    "preset": "veryfast",
    "hwaccel_encoder": "",       # "" = otomatis/software
    "faststart": True,
    "window_geometry": "",
    "ffmpeg_dir": "",            # manual override set from the GUI
    "keep_log_lines": 500,
    "active_tab": 0,

    # -- hardsub -----------------------------------------------------------
    "hardsub_input_dir": "",
    "hardsub_output_dir": "",    # "" = di sebelah tiap video sumbernya
    "hardsub_suffix": " - hardsub",
    "hardsub_container": ".mp4",
    # Lower than the merge default on purpose: sharp text edges are exactly
    # what compression smears first, and unreadable subtitles defeat the
    # whole point of burning them in.
    "hardsub_crf": 20,
    "hardsub_copy_audio": True,
    "sub_style_enabled": False,
    "sub_font": "Arial",
    "sub_size": 24,
    "sub_primary": "#FFFFFF",
    "sub_outline_color": "#000000",
    "sub_outline": 2,
    "sub_bold": False,
    "sub_margin_v": 20,
}


def config_dir() -> str:
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    return os.path.join(base, APP_ID)


def config_path() -> str:
    return os.path.join(config_dir(), "settings.json")


def _coerce(key: str, value: Any) -> Any:
    """Force a stored value to the type its default has, or reject it.

    Checking only that the JSON is a dict is not enough. A hand-edited
    `"crf": "tinggi"` used to reach int() in the GUI builder and abort the
    whole window before it appeared - the app simply would not open, with no
    way for the user to tell why. Anything that will not convert is dropped
    and the default stands.
    """
    default = DEFAULTS[key]
    if isinstance(default, bool):
        if isinstance(value, bool):
            return value
        if isinstance(value, (int, float)):
            return bool(value)
        if isinstance(value, str):
            return value.strip().lower() in ("1", "true", "yes", "ya")
        raise ValueError(key)
    if isinstance(default, int):
        return int(value)          # rejects None, lists, "abc"
    if isinstance(default, str):
        if not isinstance(value, str):
            raise ValueError(key)
        return value
    return value


class Settings:
    def __init__(self, data: dict[str, Any] | None = None):
        self._data = dict(DEFAULTS)
        if data:
            for key, value in data.items():
                if key not in DEFAULTS:
                    continue
                try:
                    self._data[key] = _coerce(key, value)
                except (TypeError, ValueError):
                    pass       # keep the default; never fail to start
        # Values that are the right type but out of range are just as fatal
        # downstream, so clamp the ones the UI feeds straight into widgets.
        try:
            self._data["crf"] = max(0, min(51, int(self._data["crf"])))
        except (TypeError, ValueError):
            self._data["crf"] = DEFAULTS["crf"]
        try:
            self._data["keep_log_lines"] = max(
                50, min(100000, int(self._data["keep_log_lines"])))
        except (TypeError, ValueError):
            self._data["keep_log_lines"] = DEFAULTS["keep_log_lines"]

    # -- dict-ish access ---------------------------------------------------
    def __getitem__(self, key: str) -> Any:
        return self._data.get(key, DEFAULTS.get(key))

    def __setitem__(self, key: str, value: Any) -> None:
        self._data[key] = value

    def get(self, key: str, default: Any = None) -> Any:
        return self._data.get(key, DEFAULTS.get(key, default))

    def update(self, **kwargs: Any) -> None:
        self._data.update(kwargs)

    def as_dict(self) -> dict[str, Any]:
        return dict(self._data)

    # -- typed helpers -----------------------------------------------------
    @property
    def sort_key(self) -> SortKey:
        try:
            return SortKey(self._data.get("sort_key"))
        except ValueError:
            return SortKey.NAME

    @property
    def merge_mode(self) -> MergeMode:
        try:
            return MergeMode(self._data.get("merge_mode"))
        except ValueError:
            return MergeMode.AUTO

    # -- persistence -------------------------------------------------------
    @classmethod
    def load(cls) -> "Settings":
        try:
            with open(config_path(), "r", encoding="utf-8") as fh:
                data = json.load(fh)
            if not isinstance(data, dict):
                data = None
        except (OSError, ValueError):
            data = None
        return cls(data)

    def save(self) -> bool:
        """Atomic write, so a crash mid-save cannot corrupt the settings."""
        try:
            os.makedirs(config_dir(), exist_ok=True)
            target = config_path()
            fd, tmp = tempfile.mkstemp(dir=config_dir(), suffix=".tmp")
            try:
                with os.fdopen(fd, "w", encoding="utf-8") as fh:
                    json.dump(self._data, fh, indent=2, ensure_ascii=False)
                os.replace(tmp, target)
            except BaseException:
                try:
                    os.unlink(tmp)
                except OSError:
                    pass
                raise
            return True
        except OSError:
            return False
