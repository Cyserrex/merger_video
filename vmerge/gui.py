"""Tkinter front end.

Threading rule enforced throughout this file: worker threads never touch a
widget. They push messages onto `self.queue`, and `_pump_queue` - running on
the Tk main loop via `after()` - is the only thing that updates the UI.
"""

from __future__ import annotations

import os
import queue
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText
from typing import Optional

from .ffmpeg_locator import FFmpegTools, download_and_install, locate
from .merger import Cancelled, MergeError, Merger
from .model import (APP_NAME, APP_VERSION, MergeJob, MergeMode, Progress,
                    SortKey, Stage, TargetSpec, VideoFile, human_duration,
                    human_eta, human_size)
from .probe import can_stream_copy, probe_many
from .scanner import scan_folder
from .settings import Settings
from .sorting import move_items, sort_files
from .theme import Theme
from .util import (app_dir, resource_path, reveal_in_explorer,
                   run_capture)

CHECKED = "☑"      # ballot box with check
UNCHECKED = "☐"    # ballot box

# (kunci, judul, lebar awal, perataan). Lebar di sini menentukan lebar
# minimum jendela: Treeview tidak mau menyusut di bawah jumlah kolomnya, dan
# nilai yang terlalu besar membuat isi aplikasi terpotong saat pertama dibuka.
COLUMNS = (
    ("check", "", 32, "center"),
    ("no", "#", 38, "center"),
    ("name", "Nama File", 190, "w"),
    ("duration", "Durasi", 70, "center"),
    ("resolution", "Resolusi", 80, "center"),
    ("codec", "Video", 58, "center"),
    ("audio", "Audio", 78, "center"),
    ("size", "Ukuran", 72, "e"),
    ("date", "Tanggal", 112, "center"),
    ("status", "Keterangan", 130, "w"),
)

HW_ENCODERS = (
    ("", "Otomatis (CPU / libx264)"),
    ("h264_nvenc", "NVIDIA NVENC (H.264)"),
    ("hevc_nvenc", "NVIDIA NVENC (H.265)"),
    ("h264_qsv", "Intel QuickSync (H.264)"),
    ("h264_amf", "AMD AMF (H.264)"),
)


class App(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.settings = Settings.load()
        self.files: list[VideoFile] = []
        self.tools: Optional[FFmpegTools] = None
        self.queue: "queue.Queue[tuple]" = queue.Queue()
        self.worker: Optional[threading.Thread] = None
        self.task: Optional[Merger] = None   # merge atau hardsub
        self.busy = False
        self._cancel_scan = threading.Event()
        self._drag_from: Optional[int] = None
        # The last ordering the user actually chose. var_sort flips to
        # "Urutan manual" the moment a row is dragged, and a fresh scan must
        # fall back to this instead of leaving the list in raw os.scandir
        # order - which looks like sorting silently stopped working.
        self._explicit_sort = self.settings.sort_key
        if self._explicit_sort is SortKey.MANUAL:
            self._explicit_sort = SortKey.NAME
        self._log_pending: list[str] = []

        self.title(f"{APP_NAME} {APP_VERSION}")
        self._set_icon()
        self._init_style()
        self._build_ui()
        self._size_window()

        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self.after(80, self._pump_queue)
        self.after(120, self._detect_ffmpeg)

    # ------------------------------------------------------------ chrome --
    def _set_icon(self) -> None:
        for candidate in (resource_path("assets", "vmerge.ico"),
                          os.path.join(app_dir(), "assets", "vmerge.ico")):
            try:
                if os.path.exists(candidate):
                    self.iconbitmap(candidate)
                    return
            except tk.TclError:
                continue

    def _init_style(self) -> None:
        self.theme = Theme(self)

    def _size_window(self) -> None:
        """Pick a starting size that fits both the content and the screen.

        The minimum is measured from the built layout rather than hardcoded:
        a hardcoded 940x620 was smaller than what the widgets actually needed,
        so the bottom of the window - the Merge button included - was simply
        cut off until the user resized it.

        The starting size is then clamped to the work area, because the same
        content that fits comfortably on 1920x1080 is taller than a 1366x768
        laptop screen once the taskbar is taken out.
        """
        self.update_idletasks()
        need_w = max(900, self.winfo_reqwidth())
        need_h = max(600, self.winfo_reqheight())

        # Leave room for the taskbar and window chrome.
        avail_w = max(640, self.winfo_screenwidth() - 60)
        avail_h = max(480, self.winfo_screenheight() - 90)
        self.minsize(min(need_w, avail_w), min(need_h, avail_h))

        saved = str(self.settings["window_geometry"] or "")
        if saved:
            try:
                self.geometry(saved)
                self.update_idletasks()
                # A geometry saved on a bigger monitor must not strand the
                # window off-screen on this one.
                if (self.winfo_width() <= avail_w
                        and self.winfo_height() <= avail_h
                        and self.winfo_x() < avail_w
                        and self.winfo_y() < avail_h):
                    return
            except tk.TclError:
                pass                      # stale or hand-edited: fall through

        self.geometry(f"{min(1120, avail_w)}x{min(760, avail_h)}")

    # ------------------------------------------------------------ layout --
    def _build_ui(self) -> None:
        self.columnconfigure(0, weight=1)
        self.rowconfigure(1, weight=1)

        self._build_header()

        self.tabs = ttk.Notebook(self)
        self.tabs.grid(row=1, column=0, sticky="nsew", padx=14, pady=(2, 0))

        page_merge = ttk.Frame(self.tabs, style="Card.TFrame")
        self.tabs.add(page_merge, text="   Gabungkan Video   ")
        self._build_merge_tab(page_merge)

        page_sub = ttk.Frame(self.tabs, style="Card.TFrame")
        self.tabs.add(page_sub, text="   Subtitle Permanen   ")
        from .gui_hardsub import HardsubPanel
        self.hardsub = HardsubPanel(page_sub, self)

        self._build_bottom()

        # Ctrl+Tab / Alt+underlined-letter between tabs, which is what
        # keyboard users and screen readers expect from a tab strip.
        self.tabs.enable_traversal()
        self.tabs.bind("<<NotebookTabChanged>>", self._on_tab_changed)
        try:
            self.tabs.select(int(self.settings["active_tab"]))
        except (tk.TclError, ValueError, IndexError):
            pass

    def _build_header(self) -> None:
        bar = ttk.Frame(self, style="Header.TFrame")
        bar.grid(row=0, column=0, sticky="ew", padx=14, pady=(12, 6))
        bar.columnconfigure(1, weight=1)

        ttk.Label(bar, text=APP_NAME, style="Head.TLabel").grid(
            row=0, column=0, sticky="w")
        ttk.Label(bar, text=f"  v{APP_VERSION}", style="Hint.TLabel").grid(
            row=0, column=1, sticky="w", pady=(6, 0))
        self.lbl_ffmpeg = ttk.Label(bar, text="Mencari FFmpeg...",
                                    style="Hint.TLabel")
        self.lbl_ffmpeg.grid(row=0, column=2, sticky="e", pady=(6, 0))

    # -- tab 1: merge ------------------------------------------------------
    def _build_merge_tab(self, page: ttk.Frame) -> None:
        page.columnconfigure(0, weight=1)
        page.rowconfigure(3, weight=1)

        self._build_source_row(page, row=0)
        ttk.Separator(page).grid(row=1, column=0, sticky="ew", padx=16,
                                 pady=(4, 0))
        self._build_list_toolbar(page, row=2)
        self._build_list(page, row=3)
        self._build_output_row(page, row=4)

    def _build_source_row(self, page: ttk.Frame, row: int) -> None:
        frame = ttk.Frame(page, style="CardBody.TFrame")
        frame.grid(row=row, column=0, sticky="ew", padx=16, pady=(14, 8))
        frame.columnconfigure(1, weight=1)

        ttk.Label(frame, text="Folder video", style="Card.TLabel").grid(
            row=0, column=0, padx=(0, 10), sticky="w")
        self.var_folder = tk.StringVar(value=self.settings["last_input_dir"])
        entry = ttk.Entry(frame, textvariable=self.var_folder)
        entry.grid(row=0, column=1, sticky="ew")
        entry.bind("<Return>", lambda _e: self.rescan())

        ttk.Button(frame, text="Pilih Folder", command=self.choose_folder
                   ).grid(row=0, column=2, padx=(8, 0))
        ttk.Button(frame, text="Muat Ulang", command=self.rescan
                   ).grid(row=0, column=3, padx=(6, 0))

        self.var_recursive = tk.BooleanVar(
            value=bool(self.settings["recursive"]))
        ttk.Checkbutton(frame, text="Termasuk subfolder",
                        variable=self.var_recursive, command=self.rescan
                        ).grid(row=1, column=1, sticky="w", pady=(8, 0))

    def _build_list_toolbar(self, page: ttk.Frame, row: int) -> None:
        bar = ttk.Frame(page, style="Toolbar.TFrame")
        bar.grid(row=row, column=0, sticky="ew", padx=16, pady=(10, 6))

        ttk.Label(bar, text="Urutkan", style="Card.TLabel").pack(side="left")
        self.var_sort = tk.StringVar(value=self.settings.sort_key.label)
        combo = ttk.Combobox(
            bar, textvariable=self.var_sort, state="readonly", width=24,
            values=[k.label for k in SortKey if k is not SortKey.MANUAL])
        combo.pack(side="left", padx=(8, 8))
        combo.bind("<<ComboboxSelected>>", lambda _e: self.apply_sort())

        self.var_desc = tk.BooleanVar(value=bool(self.settings["sort_desc"]))
        ttk.Checkbutton(bar, text="Menurun", variable=self.var_desc,
                        command=self.apply_sort).pack(side="left", padx=(0, 14))

        for text, cmd in (("Naik", lambda: self.move(-1)),
                          ("Turun", lambda: self.move(1)),
                          ("Hapus", self.remove_selected),
                          ("Centang Semua", lambda: self.set_all(True)),
                          ("Lepas Semua", lambda: self.set_all(False))):
            ttk.Button(bar, text=text, command=cmd, style="Tool.TButton"
                       ).pack(side="left", padx=2)

        self.lbl_summary = ttk.Label(bar, text="", style="Hint.TLabel")
        self.lbl_summary.pack(side="right")

    def _build_list(self, page: ttk.Frame, row: int) -> None:
        frame = ttk.Frame(page, style="CardBody.TFrame")
        frame.grid(row=row, column=0, sticky="nsew", padx=16)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(0, weight=1)

        self.tree = ttk.Treeview(frame, columns=[c[0] for c in COLUMNS],
                                 show="headings", selectmode="extended",
                                 # Ini tinggi MINIMUM, bukan tinggi tampil:
                                 # barisnya melar mengikuti jendela. 10 baris
                                 # bawaan Tk menuntut 280px dan membuat
                                 # jendela tidak muat di layar 1366x768.
                                 height=4)
        for key, title, width, anchor in COLUMNS:
            self.tree.heading(key, text=title)
            self.tree.column(key, width=width, anchor=anchor,
                             stretch=(key in ("name", "status")))
        self.tree.grid(row=0, column=0, sticky="nsew")
        c = self.theme.c
        self.tree.tag_configure("bad", foreground=c["danger"])
        self.tree.tag_configure("off", foreground=c["muted"])
        # Zebra striping: at 100+ rows a flat white table makes it genuinely
        # hard to follow one row across to its status column.
        self.tree.tag_configure("odd", background=c["row_alt"])

        scroll = ttk.Scrollbar(frame, orient="vertical",
                               command=self.tree.yview)
        scroll.grid(row=0, column=1, sticky="ns")
        self.tree.configure(yscrollcommand=scroll.set)

        self.tree.bind("<Button-1>", self._on_tree_press)
        self.tree.bind("<B1-Motion>", self._on_tree_drag)
        self.tree.bind("<ButtonRelease-1>", self._on_tree_release)
        self.tree.bind("<Double-1>", self._on_tree_double)
        self.tree.bind("<space>", lambda _e: self._toggle_selected())
        self.tree.bind("<Delete>", lambda _e: self.remove_selected())

    def _build_output_row(self, page: ttk.Frame, row: int) -> None:
        frame = ttk.Frame(page, style="CardBody.TFrame")
        frame.grid(row=row, column=0, sticky="ew", padx=16, pady=(12, 14))
        frame.columnconfigure(1, weight=1)

        ttk.Label(frame, text="Simpan sebagai", style="Card.TLabel").grid(
            row=0, column=0, padx=(0, 10), sticky="w")
        self.var_output = tk.StringVar()
        ttk.Entry(frame, textvariable=self.var_output).grid(
            row=0, column=1, columnspan=3, sticky="ew")
        ttk.Button(frame, text="Simpan Ke", command=self.choose_output).grid(
            row=0, column=4, padx=(8, 0))

        opts = ttk.Frame(frame, style="CardBody.TFrame")
        opts.grid(row=1, column=0, columnspan=5, sticky="ew", pady=(10, 0))

        ttk.Label(opts, text="Metode", style="Card.TLabel").pack(side="left")
        self.var_mode = tk.StringVar(value=self.settings.merge_mode.label)
        mode_box = ttk.Combobox(opts, textvariable=self.var_mode,
                                state="readonly", width=26,
                                values=[m.label for m in MergeMode])
        mode_box.pack(side="left", padx=(8, 18))
        mode_box.bind("<<ComboboxSelected>>", lambda _e: self._update_summary())

        ttk.Label(opts, text="Kualitas (CRF)", style="Card.TLabel").pack(
            side="left")
        self.var_crf = tk.StringVar(value=str(self.settings["crf"]))
        ttk.Spinbox(opts, from_=14, to=32, width=4, textvariable=self.var_crf
                    ).pack(side="left", padx=(8, 18))

        ttk.Label(opts, text="Encoder", style="Card.TLabel").pack(side="left")
        self.var_encoder = tk.StringVar(value=HW_ENCODERS[0][1])
        self.box_encoder = ttk.Combobox(
            opts, textvariable=self.var_encoder, state="readonly", width=24,
            values=[label for _v, label in HW_ENCODERS])
        self.box_encoder.pack(side="left", padx=(8, 0))

    # -- shared action bar -------------------------------------------------
    def _build_bottom(self) -> None:
        frame = ttk.Frame(self, style="Card.TFrame")
        frame.grid(row=2, column=0, sticky="ew", padx=14, pady=(10, 12))
        frame.columnconfigure(0, weight=1)

        body = ttk.Frame(frame, style="CardBody.TFrame")
        body.grid(row=0, column=0, sticky="ew", padx=16, pady=12)
        body.columnconfigure(0, weight=1)
        body.rowconfigure(3, weight=1)

        self.progress = ttk.Progressbar(body, mode="determinate",
                                        maximum=1000)
        self.progress.grid(row=0, column=0, columnspan=2, sticky="ew")

        status = ttk.Frame(body, style="CardBody.TFrame")
        status.grid(row=1, column=0, columnspan=2, sticky="ew", pady=(8, 10))
        status.columnconfigure(0, weight=1)
        self.lbl_status = ttk.Label(status, text="Siap.", style="Status.TLabel")
        self.lbl_status.grid(row=0, column=0, sticky="w")
        self.lbl_eta = ttk.Label(status, text="", style="Hint.TLabel")
        self.lbl_eta.grid(row=0, column=1, sticky="e")

        buttons = ttk.Frame(body, style="CardBody.TFrame")
        buttons.grid(row=2, column=0, columnspan=2, sticky="ew")
        self.btn_primary = ttk.Button(buttons, text="GABUNGKAN VIDEO",
                                      style="Accent.TButton",
                                      command=self._start_active_tab)
        self.btn_primary.pack(side="left")
        self.btn_cancel = ttk.Button(buttons, text="Batalkan",
                                     style="Danger.TButton",
                                     command=self.cancel, state="disabled")
        self.btn_cancel.pack(side="left", padx=8)
        self.btn_open = ttk.Button(buttons, text="Buka Folder Hasil",
                                   command=self.open_result, state="disabled")
        self.btn_open.pack(side="left")

        self.var_show_log = tk.BooleanVar(value=False)
        ttk.Checkbutton(buttons, text="Log teknis",
                        variable=self.var_show_log, command=self._toggle_log
                        ).pack(side="right")

        self.log_frame = ttk.Frame(body, style="CardBody.TFrame")
        self.log_frame.grid(row=3, column=0, columnspan=2, sticky="nsew",
                            pady=(10, 0))
        self.log_frame.columnconfigure(0, weight=1)
        self.log_frame.rowconfigure(0, weight=1)
        self.log = ScrolledText(self.log_frame, height=8, wrap="none",
                                font=self.theme.font_mono, state="disabled",
                                relief="flat", borderwidth=0,
                                background="#F7F9FC",
                                foreground=self.theme.c["text"])
        self.log.grid(row=0, column=0, sticky="nsew")
        self.log_frame.grid_remove()

    # -- tab plumbing ------------------------------------------------------
    def _on_tab_changed(self, _event=None) -> None:
        """Keep the one action button describing what it will actually do."""
        index = self._active_tab()
        self.btn_primary.configure(
            text="GABUNGKAN VIDEO" if index == 0 else "BAKAR SUBTITLE")
        self.settings["active_tab"] = index

    def _active_tab(self) -> int:
        try:
            return self.tabs.index(self.tabs.select())
        except (tk.TclError, ValueError):
            return 0

    def _start_active_tab(self) -> None:
        # One button, one busy flag, one worker: the two tabs deliberately
        # share them so a merge and a burn can never run at once and fight
        # over the same output folder.
        if self._active_tab() == 0:
            self.start_merge()
        else:
            self.hardsub.start()

    def _toggle_log(self) -> None:
        if self.var_show_log.get():
            self.log_frame.grid()
        else:
            self.log_frame.grid_remove()

    # -------------------------------------------------------------- setup --
    def _detect_ffmpeg(self) -> None:
        self.tools = locate(self.settings["ffmpeg_dir"])
        if self.tools:
            self.lbl_ffmpeg.configure(
                text=f"FFmpeg {_short_version(self.tools.version)} "
                     f"({self.tools.source})", style="Hint.TLabel")
            self._detect_encoders()
            if self.var_folder.get():
                self.rescan()
            return

        self.lbl_ffmpeg.configure(text="FFmpeg tidak ditemukan",
                                  style="Bad.TLabel")
        answer = messagebox.askyesnocancel(
            "FFmpeg tidak ditemukan",
            "Aplikasi ini memerlukan FFmpeg untuk memproses video, "
            "tetapi FFmpeg tidak ditemukan di komputer ini.\n\n"
            "Ya\t= Unduh otomatis sekarang (sekitar 80 MB)\n"
            "Tidak\t= Saya sudah punya, biar saya tunjukkan foldernya\n"
            "Batal\t= Nanti saja",
            icon="warning")
        if answer is True:
            self._download_ffmpeg()
        elif answer is False:
            folder = filedialog.askdirectory(
                title="Pilih folder yang berisi ffmpeg.exe dan ffprobe.exe")
            if folder:
                self.settings["ffmpeg_dir"] = folder
                self.settings.save()
                self._detect_ffmpeg()

    def _download_ffmpeg(self) -> None:
        self._cancel_scan.clear()
        self._set_busy(True, "Mengunduh FFmpeg...")

        def work() -> None:
            def report(done, total, msg):
                frac = (done / total) if total else 0.0
                self.queue.put(("progress", Progress(
                    stage=Stage.SCANNING, fraction=frac,
                    message=f"{msg} {human_size(done)}"
                            + (f" / {human_size(total)}" if total else ""))))
            # Without this, an unwritable %APPDATA% raised straight out of the
            # thread and the window stayed disabled with a "downloading"
            # label until it was killed.
            try:
                tools = download_and_install(progress=report,
                                         cancel=self._cancel_scan.is_set)
            except Exception:
                tools = None
            self.queue.put(("ffmpeg_ready", tools))

        self.worker = threading.Thread(target=work, daemon=True)
        self.worker.start()

    def _detect_encoders(self) -> None:
        """Only offer hardware encoders this ffmpeg build actually lists."""
        if not self.tools:
            return
        try:
            res = run_capture([self.tools.ffmpeg, "-hide_banner", "-encoders"],
                              timeout=20)
            listed = res.stdout or ""
        except Exception:
            listed = ""
        values = [HW_ENCODERS[0][1]]
        for value, label in HW_ENCODERS[1:]:
            if f" {value} " in listed:
                values.append(label)
        self.box_encoder.configure(values=values)
        saved = self.settings["hwaccel_encoder"]
        for value, label in HW_ENCODERS:
            if value == saved and label in values:
                self.var_encoder.set(label)
                break

    # ------------------------------------------------------------ actions --
    def _crf_value(self) -> int:
        """CRF from the spinbox, tolerating whatever the user typed into it.

        A ttk.Spinbox can be selected and emptied, and an IntVar bound to it
        then raises TclError on read. That happened inside the merge button's
        own callback, so pressing the button appeared to do nothing at all.
        """
        try:
            value = int(str(self.var_crf.get()).strip())
        except (ValueError, tk.TclError):
            value = int(self.settings["crf"])
        value = max(0, min(51, value))
        self.var_crf.set(str(value))
        return value

    def _busy_guard(self) -> bool:
        """True if an operation is running, in which case the caller must stop.

        The merge worker holds its own snapshot of the file list, so editing
        the list mid-run would not corrupt the output - but it would show the
        user a list that no longer describes what is being written, which is
        worse than simply refusing.
        """
        if self.busy:
            self.bell()
        return self.busy

    def choose_folder(self) -> None:
        if self._busy_guard():
            return
        initial = self.var_folder.get() or self.settings["last_input_dir"]
        folder = filedialog.askdirectory(title="Pilih folder berisi video",
                                         initialdir=initial or None)
        if folder:
            self.var_folder.set(folder)
            self.rescan()

    def rescan(self) -> None:
        folder = self.var_folder.get().strip()
        if not folder or not os.path.isdir(folder):
            return
        if self._busy_guard():
            return
        if not self.tools:
            self._detect_ffmpeg()
            if not self.tools:
                return

        self.settings.update(last_input_dir=folder,
                             recursive=self.var_recursive.get())
        self.settings.save()
        self._cancel_scan.clear()
        self._set_busy(True, "Memindai folder...")
        self.files = []
        self._refresh_tree()

        recursive = self.var_recursive.get()
        tools = self.tools

        def work() -> None:
            try:
                found = scan_folder(folder, recursive=recursive,
                                    cancel=self._cancel_scan.is_set)
                if not found:
                    self.queue.put(("scan_done", []))
                    return
                self.queue.put(("progress", Progress(
                    stage=Stage.PROBING,
                    message=f"Memeriksa {len(found)} file video...")))

                def on_probe(done, total, _video):
                    self.queue.put(("progress", Progress(
                        stage=Stage.PROBING, fraction=done / max(1, total),
                        message=f"Memeriksa video {done}/{total}...")))

                probe_many(tools, found, on_progress=on_probe,
                           cancel=self._cancel_scan.is_set)
                self.queue.put(("scan_done", found))
            except Exception as exc:                      # never kill the app
                self.queue.put(("error", f"Gagal memindai folder:\n{exc}"))

        self.worker = threading.Thread(target=work, daemon=True)
        self.worker.start()

    def apply_sort(self) -> None:
        if self._busy_guard():
            return
        key = next((k for k in SortKey if k.label == self.var_sort.get()),
                   SortKey.NAME)
        if key is SortKey.MANUAL:
            # Nothing to re-sort, but "Menurun" still has an obvious meaning
            # on a hand-made order: flip it. Without this the checkbox looked
            # broken for anyone who had dragged a row.
            if self.var_desc.get() != bool(self.settings["sort_desc"]):
                self.files.reverse()
            self.settings.update(sort_desc=self.var_desc.get())
            self._refresh_tree()
            return
        self._explicit_sort = key
        self.files = sort_files(self.files, key, descending=self.var_desc.get())
        self.settings.update(sort_key=key.value, sort_desc=self.var_desc.get())
        self._refresh_tree()

    def move(self, delta: int) -> None:
        if self._busy_guard():
            return
        indices = [self.tree.index(i) for i in self.tree.selection()]
        if not indices:
            return
        moved = move_items(self.files, indices, delta)
        self.var_sort.set(SortKey.MANUAL.label)
        self._refresh_tree()
        children = self.tree.get_children()
        self.tree.selection_set([children[i] for i in moved if i < len(children)])
        if moved:
            self.tree.see(children[min(moved[0], len(children) - 1)])

    def remove_selected(self) -> None:
        if self._busy_guard():
            return
        indices = {self.tree.index(i) for i in self.tree.selection()}
        if not indices:
            return
        # Drop the selection *before* rebuilding: _refresh_tree restores
        # what was selected by looking up self.files by tree row index, and
        # those indices now point past the end of the shortened list. Leaving
        # nothing selected also stops a second press of Delete from removing
        # a row the user never picked.
        self.tree.selection_remove(*self.tree.selection())
        self.files = [f for i, f in enumerate(self.files) if i not in indices]
        self._refresh_tree()

    def set_all(self, value: bool) -> None:
        if self._busy_guard():
            return
        for f in self.files:
            if f.valid or not value:
                f.selected = value
        self._refresh_tree()

    def _toggle_selected(self) -> None:
        if self._busy_guard():
            return
        for item in self.tree.selection():
            index = self.tree.index(item)
            if 0 <= index < len(self.files):
                video = self.files[index]
                if video.valid:
                    video.selected = not video.selected
        self._refresh_tree()

    def choose_output(self) -> None:
        if self._busy_guard():
            return
        initial_dir = (self.settings["last_output_dir"]
                       or self.var_folder.get() or os.path.expanduser("~"))
        path = filedialog.asksaveasfilename(
            title="Simpan video gabungan sebagai",
            initialdir=initial_dir,
            initialfile=self._suggest_name(),
            defaultextension=".mp4",
            filetypes=[("Video MP4", "*.mp4"), ("Matroska MKV", "*.mkv"),
                       ("QuickTime MOV", "*.mov"), ("Semua file", "*.*")])
        if path:
            self.var_output.set(path)
            self.settings["last_output_dir"] = os.path.dirname(path)
            self.settings.save()

    def _suggest_name(self) -> str:
        folder = self.var_folder.get().strip()
        base = os.path.basename(folder.rstrip("\\/")) or "gabungan"
        return f"{base} - gabungan.mp4"

    # -------------------------------------------------------------- merge --
    def start_merge(self) -> None:
        if self.busy or not self.tools:
            return
        # A scan the user interrupted leaves files nobody has looked at yet.
        # Merging then would quietly produce a video containing only the part
        # that happened to get probed - 66 clips out of 480, with no warning.
        unchecked = sum(1 for f in self.files if not f.probed)
        if unchecked:
            messagebox.showwarning(
                APP_NAME,
                f"{unchecked} video belum sempat diperiksa karena "
                f"pemindaian dibatalkan.\n\n"
                f"Kalau digabung sekarang, video-video itu TIDAK "
                f"akan ikut.\n\n"
                f"Tekan \"Muat Ulang\" dan tunggu sampai pemeriksaan "
                f"selesai.")
            return

        chosen = [f for f in self.files if f.selected and f.valid]
        if len(chosen) < 2:
            messagebox.showwarning(
                APP_NAME, "Pilih minimal 2 video yang valid untuk digabungkan.")
            return

        output = self.var_output.get().strip()
        if not output:
            self.choose_output()
            output = self.var_output.get().strip()
            if not output:
                return
        if os.path.exists(output):
            if not messagebox.askyesno(
                    APP_NAME,
                    f"File berikut sudah ada dan akan ditimpa:\n\n{output}\n\n"
                    "Lanjutkan?"):
                return

        mode = next((m for m in MergeMode if m.label == self.var_mode.get()),
                    MergeMode.AUTO)
        encoder = next((v for v, label in HW_ENCODERS
                        if label == self.var_encoder.get()), "")

        total = sum(f.duration for f in chosen)
        if mode in (MergeMode.AUTO, MergeMode.COPY):
            ok, _ = can_stream_copy(chosen)
        else:
            ok = False
        if not ok and total > 3600:
            if not messagebox.askyesno(
                    APP_NAME,
                    f"Video harus di-encode ulang karena parameternya berbeda.\n\n"
                    f"Total durasi {human_duration(total)} - proses ini bisa "
                    f"memakan waktu berjam-jam.\n\nLanjutkan?"):
                return

        crf = self._crf_value()
        self.settings.update(merge_mode=mode.value, crf=crf,
                             hwaccel_encoder=encoder,
                             last_output_dir=os.path.dirname(output))
        self.settings.save()

        job = MergeJob(
            files=chosen,
            output_path=output,
            mode=mode,
            target=TargetSpec(crf=crf, preset=self.settings["preset"]),
            hwaccel_encoder=encoder,
            faststart=bool(self.settings["faststart"]),
        )
        self.task = Merger(
            self.tools, job,
            on_progress=lambda p: self.queue.put(("progress", p)),
            on_log=lambda line: self.queue.put(("log", line)))
        self._set_busy(True, "Memulai...")
        self._clear_log()
        self.btn_open.configure(state="disabled")
        self._result_path = output

        def work() -> None:
            try:
                path = self.task.run()
                self.queue.put(("merge_done", path))
            except Cancelled:
                self.queue.put(("cancelled", None))
            except MergeError as exc:
                self.queue.put(("error", str(exc)))
            except Exception as exc:
                self.queue.put(("error", f"Kesalahan tak terduga:\n{exc}"))

        self.worker = threading.Thread(target=work, daemon=True)
        self.worker.start()

    def cancel(self) -> None:
        self._cancel_scan.set()
        if self.task:
            self.task.cancel()
        self.lbl_status.configure(text="Membatalkan...")
        self.btn_cancel.configure(state="disabled")

    def open_result(self) -> None:
        path = getattr(self, "_result_path", "")
        if path and os.path.exists(path):
            reveal_in_explorer(path)

    # ------------------------------------------------------------- queue ---
    def _pump_queue(self) -> None:
        """Only place in the app where worker output touches widgets."""
        try:
            for _ in range(200):
                kind, payload = self.queue.get_nowait()
                if kind == "progress":
                    self._apply_progress(payload)
                elif kind == "log":
                    self._log_pending.append(payload)
                elif kind == "scan_done":
                    self._on_scan_done(payload)
                elif kind == "merge_done":
                    self._on_merge_done(payload)
                elif kind == "cancelled":
                    self._set_busy(False, "Dibatalkan.")
                    self.progress["value"] = 0
                elif kind == "hardsub_scan_done":
                    self.hardsub.on_scan_done(payload)
                elif kind == "hardsub_done":
                    self.hardsub.on_done(payload)
                elif kind == "ffmpeg_ready":
                    self._on_ffmpeg_ready(payload)
                elif kind == "error":
                    self._set_busy(False, "Gagal.")
                    self.progress["value"] = 0
                    messagebox.showerror(APP_NAME, payload)
        except queue.Empty:
            pass
        except Exception as exc:
            # One bad message used to kill the pump for good: the reschedule
            # lived on the happy path, so the GUI went permanently deaf to
            # its worker threads while still looking alive.
            self._log_pending.append(f"[galat internal] {exc}")
        finally:
            try:
                if self._log_pending:
                    self._flush_log()
            except Exception:
                self._log_pending = []
            self.after(80, self._pump_queue)

    def _apply_progress(self, prog: Progress) -> None:
        self.progress["value"] = max(0, min(1000, int(prog.fraction * 1000)))
        if prog.message:
            self.lbl_status.configure(text=prog.message)
        bits = []
        if prog.speed:
            bits.append(f"{prog.speed:.2f}x")
        if prog.eta_seconds:
            bits.append("sisa " + human_eta(prog.eta_seconds))
        if prog.output_size:
            bits.append(human_size(prog.output_size))
        self.lbl_eta.configure(text="   ".join(bits))

    def _on_ffmpeg_ready(self, tools) -> None:
        self._set_busy(False, "Siap.")
        self.progress["value"] = 0
        if tools:
            self.settings["ffmpeg_dir"] = ""
            self.settings.save()
            self.tools = tools
            self.lbl_ffmpeg.configure(
                text=f"FFmpeg {_short_version(tools.version)} "
                     f"({tools.source})", style="Hint.TLabel")
            self._detect_encoders()
            messagebox.showinfo(APP_NAME, "FFmpeg berhasil dipasang.")
        else:
            messagebox.showerror(
                APP_NAME,
                "Gagal mengunduh FFmpeg. Periksa koneksi internet, atau unduh "
                "manual dari https://www.gyan.dev/ffmpeg/builds/ lalu letakkan "
                "ffmpeg.exe dan ffprobe.exe di folder aplikasi ini.")

    def _on_scan_done(self, found: list[VideoFile]) -> None:
        self._set_busy(False, "")
        self.progress["value"] = 0

        # Decide the output name FIRST, then drop it from the scan. The other
        # way round the filter was a no-op on a fresh start (the field is
        # empty then), so the previous session's merged file was listed,
        # ticked, and folded back into itself on the next run.
        if not self.var_output.get().strip():
            folder = self.var_folder.get() or self.settings["last_output_dir"]
            self.var_output.set(os.path.join(folder, self._suggest_name()))
        target = self.var_output.get().strip()
        if target:
            target = os.path.normcase(os.path.abspath(target))
            found = [f for f in found
                     if os.path.normcase(os.path.abspath(f.path)) != target]

        # A cancelled scan leaves files that were never probed. They are not
        # broken - we just do not know yet - so say that, instead of counting
        # them as corrupt while leaving them ticked and mergeable.
        unchecked = 0
        for f in found:
            if not f.probed:
                unchecked += 1
                f.selected = False
                f.error = "Belum diperiksa - pemindaian dibatalkan"

        self.files = found
        if not found:
            self.lbl_status.configure(text="Tidak ada file video di folder ini.")
            self._refresh_tree()
            return

        # A new scan has no hand-made order to protect.
        self.var_sort.set(self._explicit_sort.label)
        self.apply_sort()

        valid = sum(1 for f in found if f.valid)
        broken = len(found) - valid - unchecked
        parts = [f"{valid} video siap digabung"]
        if broken:
            parts.append(f"{broken} dilewati (rusak/bukan video)")
        if unchecked:
            parts.append(f"{unchecked} belum diperiksa karena dibatalkan")
        self.lbl_status.configure(text=", ".join(parts) + ".")

    def _on_merge_done(self, path: str) -> None:
        self._set_busy(False, "Selesai.")
        self.progress["value"] = 1000
        self.btn_open.configure(state="normal")
        self._result_path = path
        size = os.path.getsize(path) if os.path.exists(path) else 0
        if messagebox.askyesno(
                APP_NAME,
                f"Penggabungan selesai.\n\n{path}\nUkuran: {human_size(size)}\n\n"
                "Buka folder hasil sekarang?"):
            reveal_in_explorer(path)

    # -------------------------------------------------------------- view ---
    def _refresh_tree(self) -> None:
        selected_paths = {self.files[self.tree.index(i)].path
                          for i in self.tree.selection()
                          if self.tree.index(i) < len(self.files)}
        self.tree.delete(*self.tree.get_children())
        for index, f in enumerate(self.files, start=1):
            tags = ["odd"] if index % 2 else []
            if not f.valid:
                tags.append("bad")
            elif not f.selected:
                tags.append("off")
            audio = (f"{f.a_codec} {f.channels}ch" if f.has_audio
                     else "(tanpa audio)")
            date = f.media_created or f.name_ts
            date_text = (date.strftime("%Y-%m-%d %H:%M")
                         if date else _fmt_mtime(f.mtime))
            self.tree.insert(
                "", "end",
                values=(CHECKED if (f.selected and f.valid) else UNCHECKED,
                        index, f.name,
                        human_duration(f.duration) if f.valid else "-",
                        f.resolution if f.valid else "-",
                        f.v_codec or "-", audio if f.valid else "-",
                        human_size(f.size), date_text,
                        f.error if not f.valid else ""),
                tags=tags)
        children = self.tree.get_children()
        restore = [children[i] for i, f in enumerate(self.files)
                   if f.path in selected_paths and i < len(children)]
        if restore:
            self.tree.selection_set(restore)
        self._update_summary()

    def _update_summary(self) -> None:
        chosen = [f for f in self.files if f.selected and f.valid]
        total = sum(f.duration for f in chosen)
        size = sum(f.size for f in chosen)
        text = (f"{len(chosen)} video dipilih  |  total {human_duration(total)}"
                f"  |  {human_size(size)}")
        if len(chosen) >= 2:
            ok, _ = can_stream_copy(chosen)
            text += "  |  " + ("dapat digabung cepat (tanpa encode ulang)"
                               if ok else "perlu encode ulang")
        self.lbl_summary.configure(text=text)

    def _clear_log(self) -> None:
        self.log.configure(state="normal")
        self.log.delete("1.0", "end")
        self.log.configure(state="disabled")

    def _flush_log(self) -> None:
        """Batched insert: ffmpeg can emit hundreds of lines per second."""
        lines, self._log_pending = self._log_pending, []
        self.log.configure(state="normal")
        self.log.insert("end", "\n".join(lines) + "\n")
        limit = int(self.settings["keep_log_lines"] or 500)
        total = int(self.log.index("end-1c").split(".")[0])
        if total > limit:
            self.log.delete("1.0", f"{total - limit}.0")
        self.log.see("end")
        self.log.configure(state="disabled")

    def _set_busy(self, busy: bool, status: str = "") -> None:
        self.busy = busy
        state = "disabled" if busy else "normal"
        self.btn_primary.configure(state=state)
        self.btn_cancel.configure(state="normal" if busy else "disabled")
        if status:
            self.lbl_status.configure(text=status)
        self.configure(cursor="watch" if busy else "")

    # ------------------------------------------------------ tree gestures --
    def _on_tree_press(self, event) -> None:
        region = self.tree.identify_region(event.x, event.y)
        if region != "cell":
            self._drag_from = None
            return
        item = self.tree.identify_row(event.y)
        if not item:
            self._drag_from = None
            return
        if self.tree.identify_column(event.x) == "#1":   # the checkbox column
            index = self.tree.index(item)
            if (not self.busy and 0 <= index < len(self.files)
                    and self.files[index].valid):
                video = self.files[index]
                video.selected = not video.selected
                # Repaint one cell, not the table. A full rebuild froze the
                # window for 0.7 s on a large folder - per tick.
                self.tree.set(item, "check",
                              CHECKED if video.selected else UNCHECKED)
                stripe = ("odd",) if (index + 1) % 2 else ()
                self.tree.item(item, tags=stripe + (
                    () if video.selected else ("off",)))
                self._update_summary()
            self._drag_from = None
            return "break"
        self._drag_from = self.tree.index(item)

    def _on_tree_drag(self, event) -> None:
        if self._drag_from is None or self.busy:
            return
        item = self.tree.identify_row(event.y)
        if not item:
            return
        target = self.tree.index(item)
        if target == self._drag_from:
            return

        video = self.files.pop(self._drag_from)
        self.files.insert(target, video)

        # Mouse motion fires far faster than a full table rebuild can keep up
        # with: rebuilding costs 7 ms at 100 rows but 63 ms at 1000, which is
        # a visibly stuttering drag. Moving the one row and renumbering the
        # span it crossed is independent of how long the list is.
        children = self.tree.get_children()
        self.tree.move(children[self._drag_from], "", target)
        lo, hi = sorted((self._drag_from, target))
        self._renumber(lo, hi)

        self._drag_from = target
        self.var_sort.set(SortKey.MANUAL.label)
        children = self.tree.get_children()
        if target < len(children):
            self.tree.selection_set(children[target])

    def _renumber(self, lo: int, hi: int) -> None:
        """Rewrite the "#" column for rows lo..hi after a reorder."""
        children = self.tree.get_children()
        for index in range(max(0, lo), min(hi + 1, len(children))):
            self.tree.set(children[index], "no", index + 1)

    def _on_tree_release(self, _event) -> None:
        self._drag_from = None

    def _on_tree_double(self, event) -> None:
        item = self.tree.identify_row(event.y)
        if not item:
            return
        index = self.tree.index(item)
        if 0 <= index < len(self.files):
            reveal_in_explorer(self.files[index].path)

    # -------------------------------------------------------------- close --
    def _on_close(self) -> None:
        if self.busy:
            if not messagebox.askyesno(
                    APP_NAME, "Proses masih berjalan. Batalkan dan keluar?"):
                return
            self.cancel()
            # The worker is a daemon thread, so destroying the window here
            # would kill it mid-flight and leave the .vmerge_tmp_* folder -
            # potentially tens of GB of normalised clips - behind forever.
            # Give it a moment to unwind and clean up after itself.
            self.lbl_status.configure(text="Menghentikan dan membersihkan...")
            self.update_idletasks()
            worker = self.worker
            if worker is not None and worker.is_alive():
                worker.join(timeout=15)
        try:
            self.settings["window_geometry"] = self.geometry()
            self.settings["active_tab"] = self._active_tab()
            self.settings.save()
        except Exception:
            pass
        self.destroy()


def _short_version(text: str) -> str:
    """'8.1.1-full_build-www.gyan.dev' -> '8.1.1'."""
    head = (text or "").strip().split("-", 1)[0]
    return head or (text or "?")


def _fmt_mtime(mtime: float) -> str:
    from datetime import datetime
    try:
        return datetime.fromtimestamp(mtime).strftime("%Y-%m-%d %H:%M")
    except (OSError, OverflowError, ValueError):
        return "-"


def run() -> int:
    # DPI awareness is claimed in app.py before tkinter is imported; calling
    # it here would already be too late to affect Tk's screen metrics.
    app = App()
    app.mainloop()
    return 0
