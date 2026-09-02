"""The "Subtitle Permanen" tab.

Owns its own widgets and file list, but borrows the window's busy flag, work
queue, progress bar and Cancel button - so a merge and a burn can never run at
the same time and fight over the same folder.

Threading rule is the window's: nothing here touches a widget from a worker
thread. Workers push onto `app.queue`; `on_scan_done` / `on_done` are called
back on the Tk thread by the window's pump.
"""

from __future__ import annotations

import os
import threading
import tkinter as tk
from tkinter import colorchooser, filedialog, messagebox, ttk
from typing import Optional

from .hardsub import (OUTPUT_EXTENSIONS, HardsubItem, HardsubJob, Hardsubber,
                      collect_sources)
from .model import (APP_NAME, MergeJob, Progress, SortKey, Stage, TargetSpec,
                    VideoFile, human_duration, human_size)
from .probe import probe_many
from .runner import Cancelled, MergeError
from .scanner import scan_folder
from .sorting import sort_files
from .subtitle import SubtitleStyle
from .util import reveal_in_explorer

CHECKED = "☑"
UNCHECKED = "☐"

COLUMNS = (
    ("check", "", 32, "center"),
    ("no", "#", 38, "center"),
    ("name", "Nama File", 210, "w"),
    ("duration", "Durasi", 70, "center"),
    ("resolution", "Resolusi", 80, "center"),
    ("source", "Subtitle yang dipakai", 210, "w"),
    ("status", "Keterangan", 140, "w"),
)

SUB_FILETYPES = [
    ("Berkas subtitle", "*.srt *.ass *.ssa *.vtt *.sub *.smi *.ttml"),
    ("SubRip (.srt)", "*.srt"),
    ("Advanced SubStation (.ass)", "*.ass"),
    ("Semua file", "*.*"),
]

FONT_CHOICES = ("Arial", "Segoe UI", "Tahoma", "Verdana", "Times New Roman",
                "Calibri", "Roboto", "Noto Sans")


class HardsubPanel:
    """Builds and drives the hardsub tab. `app` is the App window."""

    def __init__(self, page: ttk.Frame, app) -> None:
        self.app = app
        self.settings = app.settings
        self.items: list[HardsubItem] = []
        self._drag_guard = False

        page.columnconfigure(0, weight=1)
        page.rowconfigure(3, weight=1)
        self._build_source(page, 0)
        ttk.Separator(page).grid(row=1, column=0, sticky="ew", padx=16,
                                 pady=(4, 0))
        self._build_toolbar(page, 2)
        self._build_list(page, 3)
        self._build_picker(page, 4)
        self._build_output(page, 5)

    # ------------------------------------------------------------- build --
    def _build_source(self, page: ttk.Frame, row: int) -> None:
        frame = ttk.Frame(page, style="CardBody.TFrame")
        frame.grid(row=row, column=0, sticky="ew", padx=16, pady=(14, 8))
        frame.columnconfigure(1, weight=1)

        ttk.Label(frame, text="Folder video", style="Card.TLabel").grid(
            row=0, column=0, padx=(0, 10), sticky="w")
        self.var_folder = tk.StringVar(
            value=self.settings["hardsub_input_dir"])
        entry = ttk.Entry(frame, textvariable=self.var_folder)
        entry.grid(row=0, column=1, sticky="ew")
        entry.bind("<Return>", lambda _e: self.rescan())

        ttk.Button(frame, text="Pilih Folder", command=self.choose_folder
                   ).grid(row=0, column=2, padx=(8, 0))
        ttk.Button(frame, text="Pilih Berkas", command=self.choose_files
                   ).grid(row=0, column=3, padx=(6, 0))
        ttk.Button(frame, text="Muat Ulang", command=self.rescan
                   ).grid(row=0, column=4, padx=(6, 0))

        ttk.Label(
            frame,
            text="Subtitle diambil otomatis dari dalam video, atau dari "
                 "berkas .srt/.ass yang senama di folder yang sama.",
            style="Hint.TLabel").grid(row=1, column=1, columnspan=4,
                                      sticky="w", pady=(6, 0))

    def _build_toolbar(self, page: ttk.Frame, row: int) -> None:
        bar = ttk.Frame(page, style="Toolbar.TFrame")
        bar.grid(row=row, column=0, sticky="ew", padx=16, pady=(10, 6))

        for text, cmd in (("Centang Semua", lambda: self.set_all(True)),
                          ("Lepas Semua", lambda: self.set_all(False)),
                          ("Hapus", self.remove_selected)):
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
                                 # Tinggi MINIMUM, bukan tinggi tampil - baris
                                 # melar mengikuti jendela. Lebih pendek dari
                                 # tab gabung karena halaman ini punya dua
                                 # baris kontrol tambahan, dan halaman
                                 # tertinggilah yang menentukan apakah
                                 # jendela muat di layar 1366x768.
                                 height=3)
        for key, title, width, anchor in COLUMNS:
            self.tree.heading(key, text=title)
            self.tree.column(key, width=width, anchor=anchor,
                             stretch=(key in ("name", "source", "status")))
        self.tree.grid(row=0, column=0, sticky="nsew")
        c = self.app.theme.c
        self.tree.tag_configure("bad", foreground=c["danger"])
        self.tree.tag_configure("off", foreground=c["muted"])
        self.tree.tag_configure("odd", background=c["row_alt"])

        scroll = ttk.Scrollbar(frame, orient="vertical",
                               command=self.tree.yview)
        scroll.grid(row=0, column=1, sticky="ns")
        self.tree.configure(yscrollcommand=scroll.set)

        self.tree.bind("<Button-1>", self._on_press)
        self.tree.bind("<<TreeviewSelect>>", lambda _e: self._sync_picker())
        self.tree.bind("<Double-1>", self._on_double)
        self.tree.bind("<Delete>", lambda _e: self.remove_selected())

    def _build_picker(self, page: ttk.Frame, row: int) -> None:
        frame = ttk.Frame(page, style="CardBody.TFrame")
        frame.grid(row=row, column=0, sticky="ew", padx=16, pady=(12, 0))
        frame.columnconfigure(1, weight=1)

        ttk.Label(frame, text="Subtitle untuk baris terpilih",
                  style="Card.TLabel").grid(row=0, column=0, padx=(0, 10),
                                            sticky="w")
        self.var_source = tk.StringVar()
        self.box_source = ttk.Combobox(frame, textvariable=self.var_source,
                                       state="readonly", values=[])
        self.box_source.grid(row=0, column=1, sticky="ew")
        self.box_source.bind("<<ComboboxSelected>>",
                             lambda _e: self._apply_choice())

        ttk.Button(frame, text="Ambil dari Berkas",
                   command=self.choose_subtitle_file).grid(
            row=0, column=2, padx=(8, 0))

        self._build_style(frame, row=1)

    def _build_style(self, parent: ttk.Frame, row: int) -> None:
        box = ttk.Frame(parent, style="CardBody.TFrame")
        box.grid(row=row, column=0, columnspan=3, sticky="ew", pady=(10, 0))

        self.var_style = tk.BooleanVar(
            value=bool(self.settings["sub_style_enabled"]))
        ttk.Checkbutton(box, text="Atur tampilan subtitle",
                        variable=self.var_style,
                        command=self._toggle_style).pack(side="left",
                                                         padx=(0, 14))

        self.style_widgets: list[tk.Widget] = []

        def add(label: str, widget: tk.Widget, pad: int = 14) -> None:
            lbl = ttk.Label(box, text=label, style="Card.TLabel")
            lbl.pack(side="left", padx=(0, 6))
            widget.pack(side="left", padx=(0, pad))
            self.style_widgets.extend((lbl, widget))

        self.var_font = tk.StringVar(value=str(self.settings["sub_font"]))
        add("Font", ttk.Combobox(box, textvariable=self.var_font, width=15,
                                 state="readonly", values=list(FONT_CHOICES)))

        self.var_size = tk.StringVar(value=str(self.settings["sub_size"]))
        add("Ukuran", ttk.Spinbox(box, from_=10, to=96, width=4,
                                  textvariable=self.var_size))

        self.btn_colour = tk.Button(
            box, text="  ", width=3, relief="solid", borderwidth=1,
            background=str(self.settings["sub_primary"]),
            command=lambda: self._pick_colour("sub_primary",
                                              self.btn_colour))
        add("Warna", self.btn_colour)

        self.btn_outline = tk.Button(
            box, text="  ", width=3, relief="solid", borderwidth=1,
            background=str(self.settings["sub_outline_color"]),
            command=lambda: self._pick_colour("sub_outline_color",
                                              self.btn_outline))
        add("Garis tepi", self.btn_outline)

        self.var_bold = tk.BooleanVar(value=bool(self.settings["sub_bold"]))
        chk = ttk.Checkbutton(box, text="Tebal", variable=self.var_bold)
        chk.pack(side="left")
        self.style_widgets.append(chk)

        self._toggle_style()

    def _build_output(self, page: ttk.Frame, row: int) -> None:
        frame = ttk.Frame(page, style="CardBody.TFrame")
        frame.grid(row=row, column=0, sticky="ew", padx=16, pady=(12, 14))
        frame.columnconfigure(1, weight=1)

        ttk.Label(frame, text="Simpan hasil ke",
                  style="Card.TLabel").grid(row=0, column=0, padx=(0, 10),
                                            sticky="w")
        # Placeholder text instead of a separate hint row: on a 1366x768
        # laptop every 22px of fixed height is a row of the file list.
        self.var_outdir = tk.StringVar(
            value=self.settings["hardsub_output_dir"])
        ttk.Entry(frame, textvariable=self.var_outdir).grid(
            row=0, column=1, sticky="ew")
        ttk.Button(frame, text="Pilih", command=self.choose_output_dir).grid(
            row=0, column=2, padx=(8, 0))

        opts = ttk.Frame(frame, style="CardBody.TFrame")
        opts.grid(row=1, column=0, columnspan=3, sticky="ew", pady=(10, 0))

        ttk.Label(opts, text="Akhiran nama", style="Card.TLabel").pack(
            side="left")
        self.var_suffix = tk.StringVar(
            value=str(self.settings["hardsub_suffix"]))
        ttk.Entry(opts, textvariable=self.var_suffix, width=14).pack(
            side="left", padx=(8, 18))

        ttk.Label(opts, text="Format", style="Card.TLabel").pack(side="left")
        self.var_container = tk.StringVar(
            value=str(self.settings["hardsub_container"]))
        ttk.Combobox(opts, textvariable=self.var_container, state="readonly",
                     width=6, values=list(OUTPUT_EXTENSIONS)).pack(
            side="left", padx=(8, 18))

        ttk.Label(opts, text="Kualitas (CRF)", style="Card.TLabel").pack(
            side="left")
        self.var_crf = tk.StringVar(value=str(self.settings["hardsub_crf"]))
        ttk.Spinbox(opts, from_=14, to=32, width=4,
                    textvariable=self.var_crf).pack(side="left", padx=(8, 18))

        self.var_copy_audio = tk.BooleanVar(
            value=bool(self.settings["hardsub_copy_audio"]))
        ttk.Checkbutton(opts, text="Salin audio tanpa encode ulang",
                        variable=self.var_copy_audio).pack(side="left")

    # ------------------------------------------------------------ helpers --
    def _busy_guard(self) -> bool:
        if self.app.busy:
            self.app.bell()
        return self.app.busy

    def _toggle_style(self) -> None:
        state = "normal" if self.var_style.get() else "disabled"
        for widget in self.style_widgets:
            try:
                widget.configure(state=state)
            except tk.TclError:
                pass

    def _pick_colour(self, key: str, button: tk.Button) -> None:
        current = str(self.settings[key])
        chosen = colorchooser.askcolor(color=current, title="Pilih warna")
        if chosen and chosen[1]:
            self.settings[key] = chosen[1]
            button.configure(background=chosen[1])

    def _style(self) -> SubtitleStyle:
        return SubtitleStyle(
            enabled=self.var_style.get(),
            font=self.var_font.get() or "Arial",
            size=_int_or(self.var_size.get(), self.settings["sub_size"],
                         10, 96),
            primary=str(self.settings["sub_primary"]),
            outline_color=str(self.settings["sub_outline_color"]),
            outline=float(self.settings["sub_outline"] or 2),
            bold=self.var_bold.get(),
            margin_v=int(self.settings["sub_margin_v"] or 20),
        )

    # ------------------------------------------------------------ actions --
    def choose_folder(self) -> None:
        if self._busy_guard():
            return
        folder = filedialog.askdirectory(
            title="Pilih folder berisi video",
            initialdir=self.var_folder.get() or None)
        if folder:
            self.var_folder.set(folder)
            self.rescan()

    def choose_files(self) -> None:
        if self._busy_guard():
            return
        paths = filedialog.askopenfilenames(
            title="Pilih video",
            initialdir=self.var_folder.get() or None,
            filetypes=[("Video", "*.mp4 *.mkv *.avi *.mov *.ts *.m4v *.webm"),
                       ("Semua file", "*.*")])
        if paths:
            self._load([VideoFile(path=p, size=_size_of(p)) for p in paths])

    def rescan(self) -> None:
        folder = self.var_folder.get().strip()
        if not folder or not os.path.isdir(folder):
            return
        if self._busy_guard():
            return
        if not self.app.tools:
            self.app._detect_ffmpeg()
            if not self.app.tools:
                return
        self.settings["hardsub_input_dir"] = folder
        self.settings.save()
        found = scan_folder(folder, recursive=False)
        if not found:
            self.items = []
            self._refresh()
            self.app.lbl_status.configure(
                text="Tidak ada file video di folder ini.")
            return
        self._load(sort_files(found, SortKey.NAME))

    def _load(self, videos: list[VideoFile]) -> None:
        """Probe the videos and look up their subtitles, off the Tk thread."""
        self.items = []
        self._refresh()
        tools = self.app.tools
        if not tools:
            return
        self.app._cancel_scan.clear()
        self.app._set_busy(True, f"Memeriksa {len(videos)} video...")

        def work() -> None:
            try:
                probe_many(tools, videos,
                           cancel=self.app._cancel_scan.is_set)
                usable = [v for v in videos if v.valid]
                items = collect_sources(tools, usable,
                                        cancel=self.app._cancel_scan.is_set)
                self.app.queue.put(("hardsub_scan_done", items))
            except Exception as exc:                  # never kill the app
                self.app.queue.put(("error", f"Gagal memeriksa video:\n{exc}"))

        self.app.worker = threading.Thread(target=work, daemon=True)
        self.app.worker.start()

    def on_scan_done(self, items: list[HardsubItem]) -> None:
        self.app._set_busy(False, "")
        self.app.progress["value"] = 0
        self.items = items
        self._refresh()
        ready = sum(1 for i in items if i.has_source)
        without = len(items) - ready
        parts = [f"{ready} video siap dibakar subtitle"]
        if without:
            parts.append(f"{without} tanpa subtitle")
        self.app.lbl_status.configure(text=", ".join(parts) + ".")
        self._sync_picker()

    def set_all(self, value: bool) -> None:
        if self._busy_guard():
            return
        for item in self.items:
            if item.has_source or not value:
                item.selected = value
        self._refresh()

    def remove_selected(self) -> None:
        if self._busy_guard():
            return
        indices = {self.tree.index(i) for i in self.tree.selection()}
        if not indices:
            return
        # Same trap as the merge list: _refresh restores the selection by row
        # index, and those indices point past the end once the list shrinks.
        self.tree.selection_remove(*self.tree.selection())
        self.items = [i for n, i in enumerate(self.items) if n not in indices]
        self._refresh()

    def choose_output_dir(self) -> None:
        if self._busy_guard():
            return
        folder = filedialog.askdirectory(
            title="Folder untuk menyimpan hasil",
            initialdir=self.var_outdir.get() or self.var_folder.get() or None)
        if folder:
            self.var_outdir.set(folder)

    def choose_subtitle_file(self) -> None:
        if self._busy_guard():
            return
        targets = self._selected_items()
        if not targets:
            messagebox.showinfo(APP_NAME,
                                "Pilih dulu baris video di daftar di atas.")
            return
        path = filedialog.askopenfilename(
            title="Pilih berkas subtitle", filetypes=SUB_FILETYPES,
            initialdir=os.path.dirname(targets[0].video.path))
        if not path:
            return
        for item in targets:
            item.external_path = path
            item.track = None
            item.error = ""
            item.selected = True
        self._refresh()
        self._sync_picker()

    # ------------------------------------------------------------ picker --
    def _selected_items(self) -> list[HardsubItem]:
        out = []
        for row in self.tree.selection():
            index = self.tree.index(row)
            if 0 <= index < len(self.items):
                out.append(self.items[index])
        return out

    def _options(self, item: HardsubItem) -> list[tuple[str, str, object]]:
        """Every subtitle this one video could use, as (label, kind, value)."""
        options: list[tuple[str, str, object]] = []
        for track in item.tracks:
            if track.burnable:
                options.append((f"Di dalam video: {track.label}",
                                "track", track))
        for path in item.sidecars:
            options.append((f"Berkas: {os.path.basename(path)}",
                            "file", path))
        if item.external_path and all(
                item.external_path != value
                for _l, kind, value in options if kind == "file"):
            options.append((f"Berkas: {os.path.basename(item.external_path)}",
                            "file", item.external_path))
        return options

    def _sync_picker(self) -> None:
        """Show the options of the focused row; blank when rows disagree."""
        targets = self._selected_items()
        if not targets:
            self.box_source.configure(values=[], state="disabled")
            self.var_source.set("")
            return
        self._options_cache = self._options(targets[0])
        labels = [label for label, _k, _v in self._options_cache]
        self.box_source.configure(values=labels,
                                  state="readonly" if labels else "disabled")
        current = ""
        item = targets[0]
        for label, kind, value in self._options_cache:
            if kind == "track" and item.track is value:
                current = label
            elif kind == "file" and item.external_path == value:
                current = label
        self.var_source.set(current)

    def _apply_choice(self) -> None:
        """Assign the picked subtitle to every selected row.

        With several rows selected the *kind* is copied, not the value: each
        episode gets its own track #2, not episode one's track object. Doing
        it the other way round burned the first file's subtitle onto all of
        them.
        """
        chosen = self.var_source.get()
        entry = next((o for o in getattr(self, "_options_cache", [])
                      if o[0] == chosen), None)
        if entry is None:
            return
        _label, kind, value = entry
        targets = self._selected_items()
        for index, item in enumerate(targets):
            if kind == "track":
                wanted = value.stream_index          # type: ignore[union-attr]
                match = next((t for t in item.tracks
                              if t.stream_index == wanted and t.burnable),
                             None)
                if match is None and index > 0:
                    continue                          # that row has no such track
                item.track = match or value           # type: ignore[assignment]
                item.external_path = ""
            else:
                if index == 0:
                    item.external_path = str(value)
                else:
                    # For the other rows, prefer their own same-named sidecar.
                    item.external_path = (item.sidecars[0] if item.sidecars
                                          else str(value))
                item.track = None
            item.error = ""
            item.selected = True
        self._refresh()

    # -------------------------------------------------------------- start --
    def start(self) -> None:
        if self.app.busy or not self.app.tools:
            return
        chosen = [i for i in self.items if i.selected]
        if not chosen:
            messagebox.showwarning(
                APP_NAME, "Pilih minimal satu video untuk diberi subtitle.")
            return
        missing = [i for i in chosen if not i.has_source]
        if missing:
            messagebox.showwarning(
                APP_NAME,
                f"{len(missing)} video belum punya subtitle.\n\n"
                "Pilih barisnya lalu tentukan subtitle di kotak "
                "\"Subtitle untuk baris terpilih\", atau lepas centangnya.")
            return

        suffix = self.var_suffix.get()
        outdir = self.var_outdir.get().strip()
        if not suffix.strip() and not outdir:
            messagebox.showwarning(
                APP_NAME,
                "Tanpa akhiran nama dan tanpa folder tujuan, hasilnya akan "
                "menimpa video aslinya.\n\nIsi salah satu dari keduanya.")
            return

        crf = _int_or(self.var_crf.get(), self.settings["hardsub_crf"], 0, 51)
        self.var_crf.set(str(crf))
        style = self._style()
        self.settings.update(
            hardsub_output_dir=outdir, hardsub_suffix=suffix,
            hardsub_container=self.var_container.get(), hardsub_crf=crf,
            hardsub_copy_audio=self.var_copy_audio.get(),
            sub_style_enabled=style.enabled, sub_font=style.font,
            sub_size=style.size, sub_bold=style.bold)
        self.settings.save()

        total = sum(i.video.duration for i in chosen)
        if total > 1800:
            if not messagebox.askyesno(
                    APP_NAME,
                    f"Membakar subtitle selalu meng-encode ulang gambar.\n\n"
                    f"{len(chosen)} video, total {human_duration(total)} - "
                    f"proses ini bisa memakan waktu lama.\n\nLanjutkan?"):
                return

        encoder = next((v for v, label in _hw_encoders(self.app)
                        if label == self.app.var_encoder.get()), "")
        job = HardsubJob(
            items=chosen, output_dir=outdir, suffix=suffix,
            container=self.var_container.get(), style=style,
            target=TargetSpec(crf=crf, preset=self.settings["preset"]),
            hwaccel_encoder=encoder,
            copy_audio=self.var_copy_audio.get(),
            faststart=bool(self.settings["faststart"]))

        self.app.task = Hardsubber(
            self.app.tools, job,
            on_progress=lambda p: self.app.queue.put(("progress", p)),
            on_log=lambda line: self.app.queue.put(("log", line)))
        self.app._set_busy(True, "Memulai...")
        self.app._clear_log()
        self.app.btn_open.configure(state="disabled")

        task = self.app.task

        def work() -> None:
            try:
                self.app.queue.put(("hardsub_done", task.run()))
            except Cancelled:
                self.app.queue.put(("cancelled", None))
            except MergeError as exc:
                self.app.queue.put(("error", str(exc)))
            except Exception as exc:
                self.app.queue.put(("error", f"Kesalahan tak terduga:\n{exc}"))

        self.app.worker = threading.Thread(target=work, daemon=True)
        self.app.worker.start()

    def on_done(self, result) -> None:
        self.app._set_busy(False, "Selesai.")
        self.app.progress["value"] = 1000
        self._refresh()
        if result.done:
            self.app._result_path = result.done[0]
            self.app.btn_open.configure(state="normal")

        lines = [f"{len(result.done)} video selesai dibakar subtitle."]
        if result.failed:
            lines.append(f"\n{len(result.failed)} gagal:")
            lines += [f"  - {name}: {msg.splitlines()[0]}"
                      for name, msg in result.failed[:6]]
        lines.append("\nBuka folder hasil sekarang?")
        if messagebox.askyesno(APP_NAME, "\n".join(lines)) and result.done:
            reveal_in_explorer(result.done[0])

    # --------------------------------------------------------------- view --
    def _refresh(self) -> None:
        keep = {self.items[self.tree.index(i)].video.path
                for i in self.tree.selection()
                if self.tree.index(i) < len(self.items)}
        self.tree.delete(*self.tree.get_children())
        for index, item in enumerate(self.items, start=1):
            tags = ["odd"] if index % 2 else []
            if item.error and not item.has_source:
                tags.append("bad")
            elif not item.selected:
                tags.append("off")
            video = item.video
            self.tree.insert(
                "", "end",
                values=(CHECKED if item.selected else UNCHECKED, index,
                        video.name, human_duration(video.duration),
                        video.resolution, item.source_label,
                        item.error or ("selesai" if item.result_path else "")),
                tags=tags)
        children = self.tree.get_children()
        restore = [children[n] for n, i in enumerate(self.items)
                   if i.video.path in keep and n < len(children)]
        if restore:
            self.tree.selection_set(restore)
        self._update_summary()

    def _update_summary(self) -> None:
        chosen = [i for i in self.items if i.selected]
        total = sum(i.video.duration for i in chosen)
        size = sum(i.video.size for i in chosen)
        self.lbl_summary.configure(
            text=f"{len(chosen)} video dipilih  |  {human_duration(total)}"
                 f"  |  {human_size(size)}  |  selalu encode ulang")

    def _on_press(self, event):
        if self.tree.identify_region(event.x, event.y) != "cell":
            return
        row = self.tree.identify_row(event.y)
        if not row or self.tree.identify_column(event.x) != "#1":
            return
        index = self.tree.index(row)
        if self.app.busy or not (0 <= index < len(self.items)):
            return "break"
        item = self.items[index]
        if not item.has_source and not item.selected:
            self.app.bell()
            return "break"
        item.selected = not item.selected
        self.tree.set(row, "check", CHECKED if item.selected else UNCHECKED)
        stripe = ("odd",) if (index + 1) % 2 else ()
        self.tree.item(row, tags=stripe + (() if item.selected else ("off",)))
        self._update_summary()
        return "break"

    def _on_double(self, event) -> None:
        row = self.tree.identify_row(event.y)
        if not row:
            return
        index = self.tree.index(row)
        if 0 <= index < len(self.items):
            reveal_in_explorer(self.items[index].video.path)


def _int_or(text: str, fallback, low: int, high: int) -> int:
    """A Spinbox can be emptied by hand, and reading it then raises."""
    try:
        value = int(str(text).strip())
    except (ValueError, tk.TclError):
        value = int(fallback)
    return max(low, min(high, value))


def _size_of(path: str) -> int:
    try:
        return os.path.getsize(path)
    except OSError:
        return 0


def _hw_encoders(app):
    from .gui import HW_ENCODERS
    return HW_ENCODERS
