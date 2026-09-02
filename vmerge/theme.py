"""The look of the app: one palette, applied to ttk once at startup.

Tk's own widgets look like Windows 95 and its native "vista" ttk theme cannot
be recoloured - the engine draws from OS bitmaps and silently ignores most
style options. "clam" is the only bundled theme that honours background,
foreground, borders and padding on every widget, so everything here is built
on clam and restyled rather than fought with.

Colours live in PALETTE alone. Nothing else in the app should name a colour.
"""

from __future__ import annotations

import tkinter as tk
from tkinter import font as tkfont
from tkinter import ttk

PALETTE = {
    "bg": "#EEF1F6",           # window behind the cards
    "card": "#FFFFFF",
    "border": "#D6DCE7",
    "text": "#1B2333",
    "muted": "#6B7480",
    "accent": "#2F6BE4",
    "accent_hover": "#2559C4",
    "accent_dim": "#A8BFF0",
    "danger": "#C22B3E",
    "success": "#1B7F45",
    "warning": "#9A6400",
    "row_alt": "#F7F9FC",
    "selection": "#D9E5FB",
    "field": "#FFFFFF",
    "field_border": "#C2CBDA",
    "track": "#DFE5EF",
}

# Segoe UI Variable ships with Windows 11 and is what modern Windows apps use;
# Segoe UI covers 10. The tuple is tried in order and the first installed one
# wins, so this degrades instead of falling back to Tk's default bitmap font.
FONT_CANDIDATES = ("Segoe UI Variable Text", "Segoe UI", "Tahoma")
MONO_CANDIDATES = ("Cascadia Mono", "Consolas", "Courier New")


def _first_available(root: tk.Misc, names: tuple[str, ...],
                     fallback: str) -> str:
    try:
        installed = {n.lower() for n in tkfont.families(root)}
    except tk.TclError:
        return fallback
    for name in names:
        if name.lower() in installed:
            return name
    return fallback


class Theme:
    """Holds the resolved fonts and applies the styles."""

    def __init__(self, root: tk.Misc):
        self.root = root
        self.c = PALETTE
        family = _first_available(root, FONT_CANDIDATES, "Tahoma")
        mono = _first_available(root, MONO_CANDIDATES, "Courier New")
        self.font = (family, 10)
        self.font_small = (family, 9)
        self.font_bold = (family, 10, "bold")
        self.font_title = (family, 11, "bold")
        self.font_head = (family, 15, "bold")
        self.font_mono = (mono, 9)
        self.apply()

    # ------------------------------------------------------------------
    def apply(self) -> None:
        c = self.c
        style = ttk.Style(self.root)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass

        self.root.configure(bg=c["bg"])
        # Tk's non-ttk widgets (menus, the log box) read these instead.
        self.root.option_add("*Font", self.font)
        self.root.option_add("*background", c["bg"])

        style.configure(".", background=c["bg"], foreground=c["text"],
                        font=self.font, borderwidth=0, focuscolor=c["accent"])

        self._frames(style)
        self._labels(style)
        self._buttons(style)
        self._inputs(style)
        self._notebook(style)
        self._tree(style)
        self._progress(style)
        self._misc(style)

    # ------------------------------------------------------------------
    def _frames(self, style: ttk.Style) -> None:
        c = self.c
        style.configure("TFrame", background=c["bg"])
        style.configure("Card.TFrame", background=c["card"],
                        relief="solid", borderwidth=1,
                        bordercolor=c["border"])
        style.configure("CardBody.TFrame", background=c["card"])
        style.configure("Header.TFrame", background=c["card"])
        style.configure("Toolbar.TFrame", background=c["card"])

    def _labels(self, style: ttk.Style) -> None:
        c = self.c
        style.configure("TLabel", background=c["bg"], foreground=c["text"])
        for name, bg in (("Card.TLabel", c["card"]), ("TLabel", c["bg"])):
            style.configure(name, background=bg, foreground=c["text"])
        style.configure("CardTitle.TLabel", background=c["card"],
                        foreground=c["text"], font=self.font_title)
        style.configure("Head.TLabel", background=c["card"],
                        foreground=c["text"], font=self.font_head)
        style.configure("Hint.TLabel", background=c["card"],
                        foreground=c["muted"], font=self.font_small)
        style.configure("HintBg.TLabel", background=c["bg"],
                        foreground=c["muted"], font=self.font_small)
        style.configure("Bad.TLabel", background=c["card"],
                        foreground=c["danger"], font=self.font_small)
        style.configure("Good.TLabel", background=c["card"],
                        foreground=c["success"], font=self.font_small)
        style.configure("Warn.TLabel", background=c["card"],
                        foreground=c["warning"], font=self.font_small)
        style.configure("Status.TLabel", background=c["card"],
                        foreground=c["text"])

    def _buttons(self, style: ttk.Style) -> None:
        c = self.c
        # Flat, no bevel: clam's default button has a raised 3D edge that is
        # the single most dated-looking thing in the toolkit.
        style.configure("TButton", background="#E7ECF4", foreground=c["text"],
                        borderwidth=1, bordercolor=c["field_border"],
                        relief="flat", padding=(12, 6), font=self.font)
        style.map("TButton",
                  background=[("disabled", "#F0F2F6"),
                              ("pressed", "#D2DAE8"), ("active", "#DEE5F0")],
                  foreground=[("disabled", "#A6AEBB")],
                  bordercolor=[("active", c["accent_dim"])])

        style.configure("Accent.TButton", background=c["accent"],
                        foreground="#FFFFFF", borderwidth=0, relief="flat",
                        padding=(20, 10), font=self.font_bold)
        style.map("Accent.TButton",
                  background=[("disabled", c["accent_dim"]),
                              ("pressed", c["accent_hover"]),
                              ("active", c["accent_hover"])],
                  foreground=[("disabled", "#EEF3FD")])

        style.configure("Danger.TButton", background=c["card"],
                        foreground=c["danger"], borderwidth=1,
                        bordercolor="#E3B3BA", padding=(14, 8))
        style.map("Danger.TButton",
                  background=[("active", "#FBEEF0"),
                              ("disabled", c["card"])],
                  foreground=[("disabled", "#D8AFB5")])

        # Small square buttons for list toolbars.
        style.configure("Tool.TButton", background=c["card"],
                        foreground=c["text"], borderwidth=1,
                        bordercolor=c["field_border"], padding=(9, 4),
                        font=self.font_small)
        style.map("Tool.TButton",
                  background=[("active", "#EDF1F8"), ("disabled", c["card"])],
                  foreground=[("disabled", "#B4BAC5")])

        style.configure("Link.TButton", background=c["card"],
                        foreground=c["accent"], borderwidth=0,
                        padding=(4, 2), font=self.font_small)
        style.map("Link.TButton", background=[("active", c["card"])],
                  foreground=[("active", c["accent_hover"])])

    def _inputs(self, style: ttk.Style) -> None:
        c = self.c
        style.configure("TEntry", fieldbackground=c["field"],
                        background=c["field"], foreground=c["text"],
                        bordercolor=c["field_border"], borderwidth=1,
                        relief="flat", padding=6, insertcolor=c["text"])
        style.map("TEntry",
                  bordercolor=[("focus", c["accent"])],
                  fieldbackground=[("disabled", "#F3F5F9")],
                  foreground=[("disabled", c["muted"])])

        style.configure("TCombobox", fieldbackground=c["field"],
                        background=c["field"], foreground=c["text"],
                        bordercolor=c["field_border"], borderwidth=1,
                        arrowcolor=c["muted"], relief="flat", padding=5)
        style.map("TCombobox",
                  bordercolor=[("focus", c["accent"])],
                  fieldbackground=[("readonly", c["field"]),
                                   ("disabled", "#F3F5F9")],
                  foreground=[("disabled", c["muted"])],
                  arrowcolor=[("disabled", "#C0C6D2")])

        style.configure("TSpinbox", fieldbackground=c["field"],
                        background=c["field"], foreground=c["text"],
                        bordercolor=c["field_border"], borderwidth=1,
                        arrowcolor=c["muted"], relief="flat", padding=5)

        style.configure("TCheckbutton", background=c["card"],
                        foreground=c["text"], focuscolor=c["card"])
        style.map("TCheckbutton",
                  background=[("active", c["card"])],
                  foreground=[("disabled", "#AEB5C0")],
                  indicatorcolor=[("selected", c["accent"]),
                                  ("!selected", c["field"])])
        style.configure("TRadiobutton", background=c["card"],
                        foreground=c["text"], focuscolor=c["card"])
        style.map("TRadiobutton", background=[("active", c["card"])],
                  indicatorcolor=[("selected", c["accent"])])

    def _notebook(self, style: ttk.Style) -> None:
        c = self.c
        style.configure("TNotebook", background=c["bg"], borderwidth=0,
                        tabmargins=(2, 6, 2, 0))
        style.configure("TNotebook.Tab", background=c["bg"],
                        foreground=c["muted"], borderwidth=0,
                        padding=(20, 10), font=self.font_bold)
        style.map("TNotebook.Tab",
                  background=[("selected", c["card"])],
                  foreground=[("selected", c["accent"]),
                              ("active", c["text"])],
                  expand=[("selected", (0, 0, 0, 0))])

    def _tree(self, style: ttk.Style) -> None:
        c = self.c
        style.configure("Treeview", background=c["card"],
                        fieldbackground=c["card"], foreground=c["text"],
                        borderwidth=0, rowheight=28, font=self.font_small)
        style.map("Treeview",
                  background=[("selected", c["selection"])],
                  foreground=[("selected", c["text"])])
        # Flat headings; clam's default is a raised grey bar.
        style.configure("Treeview.Heading", background="#F2F5FA",
                        foreground=c["muted"], relief="flat",
                        borderwidth=0, padding=(6, 7), font=self.font_small)
        style.map("Treeview.Heading",
                  background=[("active", "#E8EDF6")])
        style.layout("Treeview", [("Treeview.treearea", {"sticky": "nswe"})])

    def _progress(self, style: ttk.Style) -> None:
        c = self.c
        style.configure("Horizontal.TProgressbar", background=c["accent"],
                        troughcolor=c["track"], borderwidth=0, thickness=8,
                        lightcolor=c["accent"], darkcolor=c["accent"])
        style.configure("Slim.Horizontal.TProgressbar",
                        background=c["accent"], troughcolor=c["track"],
                        borderwidth=0, thickness=6,
                        lightcolor=c["accent"], darkcolor=c["accent"])

    def _misc(self, style: ttk.Style) -> None:
        c = self.c
        style.configure("TSeparator", background=c["border"])
        style.configure("Vertical.TScrollbar", background="#DAE0EA",
                        troughcolor=c["card"], borderwidth=0,
                        arrowcolor=c["muted"], relief="flat")
        style.map("Vertical.TScrollbar", background=[("active", "#C4CCDA")])
        style.configure("Horizontal.TScrollbar", background="#DAE0EA",
                        troughcolor=c["card"], borderwidth=0,
                        arrowcolor=c["muted"], relief="flat")
        style.configure("TLabelframe", background=c["card"],
                        bordercolor=c["border"], borderwidth=1)
        style.configure("TLabelframe.Label", background=c["card"],
                        foreground=c["muted"], font=self.font_small)


def card(parent: tk.Misc, title: str = "", subtitle: str = "",
         theme: "Theme | None" = None) -> ttk.Frame:
    """A white panel with an optional heading. Returns the body to fill.

    The body is a separate frame so callers can grid into it from row 0
    without having to know whether a title took row 0 already.
    """
    outer = ttk.Frame(parent, style="Card.TFrame", padding=(0, 0))
    outer.columnconfigure(0, weight=1)
    row = 0
    if title:
        head = ttk.Frame(outer, style="CardBody.TFrame")
        head.grid(row=0, column=0, sticky="ew", padx=16, pady=(13, 0))
        head.columnconfigure(0, weight=1)
        ttk.Label(head, text=title, style="CardTitle.TLabel").grid(
            row=0, column=0, sticky="w")
        if subtitle:
            ttk.Label(head, text=subtitle, style="Hint.TLabel").grid(
                row=1, column=0, sticky="w", pady=(1, 0))
        row = 1
    body = ttk.Frame(outer, style="CardBody.TFrame")
    body.grid(row=row, column=0, sticky="nsew", padx=16, pady=(9, 14))
    outer.rowconfigure(row, weight=1)
    body.columnconfigure(0, weight=1)
    # Let callers grid the card itself while filling the body.
    outer.body = body                      # type: ignore[attr-defined]
    return outer


def pill(parent: tk.Misc, text: str = "", kind: str = "muted") -> ttk.Label:
    """A small status chip, e.g. the FFmpeg version indicator."""
    style = {"muted": "Hint.TLabel", "good": "Good.TLabel",
             "bad": "Bad.TLabel", "warn": "Warn.TLabel"}.get(kind,
                                                             "Hint.TLabel")
    return ttk.Label(parent, text=text, style=style)
