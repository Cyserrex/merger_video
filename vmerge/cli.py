"""Command-line front end.

Exists so the merge engine can be driven (and tested) without a display, and
so the same .exe can be dropped into a scheduled task or a .bat file.
"""

from __future__ import annotations

import argparse
import os
import sys
import time

from .ffmpeg_locator import locate
from .hardsub import HardsubJob, Hardsubber, collect_sources
from .merger import Cancelled, MergeError, Merger
from .model import (APP_NAME, APP_VERSION, MergeJob, MergeMode, Progress,
                    SortKey, Stage, TargetSpec, human_duration, human_size)
from .probe import can_stream_copy, probe_many
from .model import VideoFile
from .probe import probe_file
from .scanner import scan_folder
from .sorting import sort_files
from .subtitle import SubtitleStyle


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="VideoMerger",
        description=f"{APP_NAME} {APP_VERSION} - gabungkan semua video dalam "
                    f"satu folder menjadi satu file.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="Jalankan tanpa argumen untuk membuka tampilan grafis (GUI).",
    )
    p.add_argument("-i", "--input", metavar="FOLDER",
                   help="folder berisi video yang akan digabung")
    p.add_argument("-o", "--output", metavar="FILE",
                   help="nama file hasil (mis. gabungan.mp4)")
    p.add_argument("-r", "--recursive", action="store_true",
                   help="ikut memindai subfolder")
    p.add_argument("-s", "--sort", default=SortKey.NAME.value,
                   choices=[k.value for k in SortKey],
                   help="dasar pengurutan (default: name)")
    p.add_argument("--desc", action="store_true", help="urutkan menurun")
    p.add_argument("-m", "--mode", default=MergeMode.AUTO.value,
                   choices=[m.value for m in MergeMode],
                   help="strategi penggabungan (default: auto)")
    p.add_argument("--crf", type=int, default=23,
                   help="kualitas encode ulang, makin kecil makin bagus (default 23)")
    p.add_argument("--preset", default="veryfast",
                   help="preset x264 (default veryfast)")
    p.add_argument("--encoder", default="",
                   help="paksa encoder, mis. h264_nvenc")
    p.add_argument("--strict", action="store_true",
                   help="berhenti dengan galat kalau ada file yang dilewati "
                        "(berguna untuk Task Scheduler)")
    sub = p.add_argument_group(
        "subtitle permanen (hardsub)",
        "Membakar subtitle ke dalam gambar. Selalu meng-encode ulang video.")
    sub.add_argument("--hardsub", action="store_true",
                     help="bakar subtitle, bukan menggabungkan video. "
                          "-i boleh berupa folder atau satu berkas video")
    sub.add_argument("--sub-file", metavar="FILE", default="",
                     help="pakai berkas .srt/.ass ini untuk semua video")
    sub.add_argument("--sub-track", metavar="N", type=int, default=-1,
                     help="pakai trek subtitle ke-N di dalam video "
                          "(mulai dari 1)")
    sub.add_argument("--sub-lang", metavar="KODE", default="",
                     help="pilih trek berdasarkan bahasa, mis. ind atau eng")
    sub.add_argument("--sub-style", action="store_true",
                     help="terapkan pengaturan tampilan di bawah ini")
    sub.add_argument("--sub-font", default="Arial", help="nama font")
    sub.add_argument("--sub-size", type=int, default=24, help="ukuran font")
    sub.add_argument("--suffix", default=" - hardsub",
                     help="akhiran nama berkas hasil")
    sub.add_argument("--out-dir", metavar="FOLDER", default="",
                     help="folder hasil (default: di samping video asli)")
    sub.add_argument("--container", default=".mp4",
                     help="format hasil hardsub (default .mp4)")

    p.add_argument("--list", action="store_true",
                   help="hanya tampilkan daftar video dan urutannya, jangan digabung")
    p.add_argument("--no-faststart", action="store_true",
                   help="jangan pindahkan indeks MP4 ke depan")
    p.add_argument("--version", action="version",
                   version=f"{APP_NAME} {APP_VERSION}")
    return p


class _Reporter:
    """Single-line console progress that does not spam a redirected log."""

    def __init__(self) -> None:
        self.tty = sys.stdout.isatty()
        self.last = 0.0

    def __call__(self, prog: Progress) -> None:
        now = time.time()
        if prog.stage not in (Stage.DONE, Stage.FAILED) and now - self.last < 0.5:
            return
        self.last = now
        bar_len = 28
        filled = int(bar_len * prog.fraction)
        bar = "#" * filled + "-" * (bar_len - filled)
        text = f"[{bar}] {prog.percent:5.1f}%  {prog.message}"
        if self.tty:
            sys.stdout.write("\r" + text.ljust(110)[:110])
            sys.stdout.flush()
        else:
            print(text)


def _pick_track(item, args):
    """Apply --sub-track / --sub-lang to one file, or keep the auto choice."""
    if args.sub_track > 0:
        wanted = args.sub_track - 1
        match = next((t for t in item.tracks
                      if t.stream_index == wanted and t.burnable), None)
        if match is None:
            return f"tidak ada trek subtitle #{args.sub_track}"
        item.track, item.external_path = match, ""
        return ""
    if args.sub_lang:
        tag = args.sub_lang.strip().lower()
        match = next((t for t in item.tracks
                      if t.burnable and t.language.lower() == tag), None)
        if match is None:
            return f"tidak ada trek subtitle berbahasa '{args.sub_lang}'"
        item.track, item.external_path = match, ""
        return ""
    return ""


def _run_hardsub(args, tools, source: str) -> int:
    """Burn subtitles into every selected video. Returns a process exit code."""
    if os.path.isfile(source):
        videos = [probe_file(tools, VideoFile(
            path=source, size=os.path.getsize(source)))]
    else:
        found = scan_folder(source, recursive=args.recursive)
        if not found:
            print("Tidak ada file video di folder tersebut.", file=sys.stderr)
            return 4
        print(f"Memeriksa {len(found)} file...")
        probe_many(tools, found)
        videos = sort_files([f for f in found if f.valid],
                            SortKey(args.sort), descending=args.desc)

    videos = [v for v in videos if v.valid]
    if not videos:
        print("Tidak ada video yang bisa dibaca.", file=sys.stderr)
        return 4

    items = collect_sources(tools, videos)
    problems: list[str] = []
    for item in items:
        if args.sub_file:
            item.external_path = os.path.abspath(args.sub_file)
            item.track = None
            item.error = ""
            item.selected = True
        else:
            why = _pick_track(item, args)
            if why:
                item.selected, item.error = False, why
                problems.append(f"{item.video.name}: {why}")
        if not item.has_source:
            # collect_sources already unticks a video with no subtitle at all,
            # so testing `selected` here hid exactly the files the user most
            # needs told about - they simply vanished from the report.
            item.selected = False
            problems.append(
                f"{item.video.name}: {item.error or 'tanpa subtitle'}")

    chosen = [i for i in items if i.selected]
    print(chr(10) + f"{len(chosen)} video akan dibakar subtitle:")
    for index, item in enumerate(chosen, 1):
        print(f"  {index:3d}. {item.video.name:40.40s} <- {item.source_label}")
    for line in problems:
        print(f"  SKIP {line}")

    if problems and args.strict:
        print(chr(10) + f"GAGAL (--strict): {len(problems)} file dilewati.",
              file=sys.stderr)
        return 5
    if args.list:
        return 0
    if not chosen:
        print(chr(10) + "Tidak ada video yang punya subtitle.",
              file=sys.stderr)
        return 4

    job = HardsubJob(
        items=chosen,
        output_dir=os.path.abspath(args.out_dir) if args.out_dir else "",
        suffix=args.suffix,
        container=args.container if args.container.startswith(".")
        else "." + args.container,
        style=SubtitleStyle(enabled=args.sub_style, font=args.sub_font,
                            size=args.sub_size),
        target=TargetSpec(crf=args.crf, preset=args.preset),
        hwaccel_encoder=args.encoder,
        faststart=not args.no_faststart)

    reporter = _Reporter()
    task = Hardsubber(tools, job, on_progress=reporter,
                      on_log=lambda line: None)
    started = time.time()
    try:
        result = task.run()
    except Cancelled:
        print(chr(10) + "Dibatalkan.", file=sys.stderr)
        return 130
    except MergeError as exc:
        print(chr(10) + chr(10) + f"GAGAL: {exc}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        task.cancel()
        print(chr(10) + "Dibatalkan.", file=sys.stderr)
        return 130
    except OSError as exc:
        print(chr(10) + chr(10) + f"GAGAL: {exc.strerror or exc}",
              file=sys.stderr)
        return 1

    print(chr(10) + chr(10)
          + f"Selesai dalam {human_duration(time.time() - started)}")
    for path in result.done:
        print(f"  {path} ({human_size(os.path.getsize(path))})")
    for name, msg in result.failed:
        print(f"  GAGAL {name}: {msg.splitlines()[0]}", file=sys.stderr)
    # A batch where some files failed is not a success, even though the rest
    # were written - a scheduled task has to be able to tell.
    return 1 if result.failed else 0


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    if not args.input:
        build_parser().print_help()
        return 2

    source = os.path.abspath(args.input)
    # Hardsub is the one mode where pointing at a single file is the common
    # case ("this one episode has soft subs"), so it accepts either.
    if not os.path.isdir(source) and not (args.hardsub
                                          and os.path.isfile(source)):
        print(f"Folder tidak ditemukan: {source}", file=sys.stderr)
        return 2
    folder = source

    tools = locate()
    if not tools:
        print("FFmpeg tidak ditemukan. Pasang FFmpeg atau letakkan ffmpeg.exe "
              "di folder aplikasi.", file=sys.stderr)
        return 3
    print(f"FFmpeg {tools.version} ({tools.source})")

    if args.hardsub:
        return _run_hardsub(args, tools, source)

    files = scan_folder(folder, recursive=args.recursive)
    if args.output:
        # Saving into the folder being merged is normal, so the previous run's
        # output must not be swept back in as an input on the next one.
        target = os.path.normcase(os.path.abspath(args.output))
        files = [f for f in files
                 if os.path.normcase(os.path.abspath(f.path)) != target]
    if not files:
        print("Tidak ada file video di folder tersebut.", file=sys.stderr)
        return 4
    print(f"Memeriksa {len(files)} file...")
    probe_many(tools, files)

    valid = [f for f in files if f.valid]
    bad = [f for f in files if not f.valid]
    valid = sort_files(valid, SortKey(args.sort), descending=args.desc)

    total = sum(f.duration for f in valid)
    print(f"\n{len(valid)} video valid, total durasi {human_duration(total)}"
          f", ukuran {human_size(sum(f.size for f in valid))}")
    for i, f in enumerate(valid, 1):
        print(f"  {i:3d}. {f.name:44.44s} {human_duration(f.duration)} "
              f"{f.resolution:>10s} {f.v_codec}")
    for f in bad:
        print(f"  SKIP {f.name}: {f.error}")

    if len(valid) >= 2:
        ok, reasons = can_stream_copy(valid)
        print(chr(10) + "Bisa digabung tanpa encode ulang: "
              + ("YA" if ok else "TIDAK"))
        for r in reasons[:5]:
            print("  - " + r)

    if bad and args.strict:
        # Unattended runs (a .bat, Task Scheduler) must not quietly merge 97
        # of 100 recordings because three were still locked by the DVR.
        print(chr(10) + f"GAGAL (--strict): {len(bad)} file dilewati.",
              file=sys.stderr)
        return 5

    if args.list:
        return 0
    if not args.output:
        print("\n--output wajib diisi (atau pakai --list).", file=sys.stderr)
        return 2
    if len(valid) < 2:
        print("\nButuh minimal 2 video valid.", file=sys.stderr)
        return 4

    job = MergeJob(
        files=valid,
        output_path=os.path.abspath(args.output),
        mode=MergeMode(args.mode),
        target=TargetSpec(crf=args.crf, preset=args.preset),
        hwaccel_encoder=args.encoder,
        faststart=not args.no_faststart,
    )

    reporter = _Reporter()
    merger = Merger(tools, job, on_progress=reporter,
                    on_log=lambda line: None)
    started = time.time()
    try:
        out = merger.run()
    except Cancelled:
        print("\nDibatalkan.", file=sys.stderr)
        return 130
    except MergeError as exc:
        print(f"\n\nGAGAL: {exc}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        merger.cancel()
        print(chr(10) + "Dibatalkan.", file=sys.stderr)
        return 130
    except OSError as exc:
        # Unwritable destination, disconnected share, disk full: report it
        # plainly instead of dumping a traceback (or, in the windowed .exe,
        # popping a dialog nobody can click).
        print(chr(10) + chr(10) + f"GAGAL: {exc.strerror or exc}",
              file=sys.stderr)
        return 1

    print(f"\n\nSelesai dalam {human_duration(time.time() - started)}")
    print(f"Hasil: {out} ({human_size(os.path.getsize(out))})")
    return 0
