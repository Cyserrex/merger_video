"""Regression tests for the stream-copy safety check.

Every case here was first observed as a *silent* corruption: ffmpeg exits 0,
prints no warning, and produces a file that is wrong. The point of these tests
is that `can_stream_copy` must refuse the merge before that happens.

Run:  python -m tests.test_compat      (from the project root)
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from vmerge.ffmpeg_locator import locate                      # noqa: E402
from vmerge.merger import escape_concat_path, write_concat_list  # noqa: E402
from vmerge.model import VideoFile                            # noqa: E402
from vmerge.probe import can_stream_copy, probe_file          # noqa: E402
from vmerge.merger import MergeError, Merger  # noqa: E402
from vmerge.model import MergeJob, MergeMode, TargetSpec  # noqa: E402
from vmerge.scanner import scan_folder  # noqa: E402
from vmerge.sorting import sort_files  # noqa: E402
from vmerge.model import SortKey, Stage  # noqa: E402
from vmerge.probe import probe_many  # noqa: E402
from vmerge.sorting import (explorer_sort_key, natural_key,  # noqa: E402
                            parse_timestamp_from_name)

TOOLS = locate()
FAILURES: list[str] = []
PASSES = 0


def check(name: str, condition: bool, detail: str = "") -> None:
    global PASSES
    if condition:
        PASSES += 1
        print(f"  PASS  {name}")
    else:
        FAILURES.append(f"{name}: {detail}")
        print(f"  FAIL  {name}  {detail}")


def make(path: str, *args: str) -> str:
    subprocess.run([TOOLS.ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
                    *args, path], check=True)
    return path


def probed(path: str) -> VideoFile:
    return probe_file(TOOLS, VideoFile(path=path, size=os.path.getsize(path)))


def real_duration(path: str) -> float:
    out = subprocess.run(
        [TOOLS.ffprobe, "-v", "error", "-show_entries", "format=duration",
         "-of", "default=nk=1:nw=1", path],
        capture_output=True, text=True, check=True).stdout.strip()
    return float(out)


def concat_to(files: list[str], out: str, workdir: str) -> float:
    listing = write_concat_list(files, os.path.join(workdir, "l.txt"))
    subprocess.run([TOOLS.ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "concat", "-safe", "0", "-i", listing,
                    "-c", "copy", out], check=True)
    return real_duration(out)


def test_compatibility(work: str) -> None:
    print("\n[1] Deteksi kompatibilitas stream copy")
    src = ["-f", "lavfi", "-i", "testsrc=size=640x480:rate=30:duration=5",
           "-f", "lavfi", "-i", "sine=frequency=440:duration=5",
           "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
           "-c:a", "aac", "-ar", "48000", "-ac", "2", "-shortest"]
    base = make(os.path.join(work, "base.mp4"), *src)

    # Same encode, remuxed with two different MP4 timescales. Everything else
    # is byte-identical, yet copy-joining them nearly doubles the duration.
    tb15 = make(os.path.join(work, "tb15360.mp4"), "-i", base, "-c", "copy",
                "-video_track_timescale", "15360")
    tb30 = make(os.path.join(work, "tb30000.mp4"), "-i", base, "-c", "copy",
                "-video_track_timescale", "30000")

    a, b = probed(tb15), probed(tb30)
    ok, reasons = can_stream_copy([a, b])
    check("time_base berbeda ditolak", not ok,
          "signature gagal menangkap perbedaan timescale")
    check("alasan menyebut time base",
          any("time base" in r for r in reasons), str(reasons)[:120])

    # Prove the corruption is real, so the test is not guarding a phantom.
    merged = concat_to([tb15, tb30], os.path.join(work, "bad.mp4"), work)
    check("bukti: durasi memang rusak (>1.5x)", merged > 15.0,
          f"durasi gabungan {merged:.2f}s dari 2x5s")
    print(f"        (gabungan 2 klip 5 detik menghasilkan {merged:.2f} detik)")

    # 30 vs 29.97 fps: the other silent duration-stretcher.
    f30 = make(os.path.join(work, "f30.mp4"),
               "-f", "lavfi", "-i", "testsrc=size=640x480:rate=30:duration=5",
               "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p")
    f2997 = make(os.path.join(work, "f2997.mp4"),
                 "-f", "lavfi", "-i",
                 "testsrc=size=640x480:rate=30000/1001:duration=5",
                 "-c:v", "libx264", "-preset", "ultrafast",
                 "-pix_fmt", "yuv420p")
    ok, _ = can_stream_copy([probed(f30), probed(f2997)])
    check("30fps vs 29.97fps ditolak", not ok)

    # H.264 level differences DO join cleanly; flagging them would force
    # pointless hours of re-encoding.
    lvl30 = make(os.path.join(work, "lvl30.mp4"), *src[:-1], "-level", "3.0",
                 "-shortest")
    lvl41 = make(os.path.join(work, "lvl41.mp4"), *src[:-1], "-level", "4.1",
                 "-shortest")
    p30, p41 = probed(lvl30), probed(lvl41)
    ok, reasons = can_stream_copy([p30, p41])
    check("beda level H.264 TIDAK ditolak (bukan false positive)", ok,
          f"level {p30.v_level} vs {p41.v_level}: {reasons}")

    # A clip with no audio loses its audio track after the first segment.
    noaudio = make(os.path.join(work, "noaudio.mp4"),
                   "-f", "lavfi", "-i",
                   "testsrc=size=640x480:rate=30:duration=5",
                   "-c:v", "libx264", "-preset", "ultrafast",
                   "-pix_fmt", "yuv420p")
    ok, _ = can_stream_copy([probed(base), probed(noaudio)])
    check("klip tanpa audio ditolak", not ok)

    # A phone shooting some clips upright and some sideways stores the
    # difference as container metadata, not in the frames, so a copy-join
    # keeps only the first clip's orientation.
    upright = make(os.path.join(work, "upright.mp4"),
                   "-f", "lavfi", "-i",
                   "testsrc=size=320x240:rate=25:duration=2",
                   "-c:v", "libx264", "-preset", "ultrafast",
                   "-pix_fmt", "yuv420p")
    turned = make(os.path.join(work, "turned.mp4"),
                  "-display_rotation", "90", "-i", upright, "-c", "copy")
    pu, pt = probed(upright), probed(turned)
    ok, reasons = can_stream_copy([pu, pt])
    check("beda rotasi ditolak", not ok,
          f"rotasi {pu.rotation} vs {pt.rotation}")
    check("alasan menyebut rotasi", any("rotasi" in r for r in reasons),
          str(reasons)[:120])

    # Identical clips must still be accepted, or the fast path never fires.
    copy_a = make(os.path.join(work, "same_a.mp4"), *src)
    copy_b = make(os.path.join(work, "same_b.mp4"), *src)
    ok, reasons = can_stream_copy([probed(copy_a), probed(copy_b)])
    check("klip identik diterima", ok, str(reasons)[:150])


def test_escaping(work: str) -> None:
    print("\n[2] Escaping nama file di concat list")
    weird = os.path.join(work, "sub dir")
    os.makedirs(weird, exist_ok=True)
    names = ["vidéo ' satu.mp4", "aneh [1] & 100% #tag.mp4", "a'b'c.mp4"]
    src = ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2",
           "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]
    made = [make(os.path.join(weird, n), *src) for n in names]

    duration = concat_to(made, os.path.join(work, "weird.mp4"), work)
    check("nama file dengan kutip/unicode/simbol berhasil digabung",
          duration > 5.5, f"durasi {duration:.2f} (harus ~6.0)")

    check("kutip tunggal di-escape",
          escape_concat_path(r"C:\a'b\c.mp4") == r"C:\a'\''b\c.mp4",
          escape_concat_path(r"C:\a'b\c.mp4"))
    check("backslash dipertahankan (aman untuk UNC)",
          "\\" in escape_concat_path(r"C:\x\y.mp4"))

    listing = os.path.join(work, "bom_check.txt")
    write_concat_list([made[0]], listing)
    with open(listing, "rb") as fh:
        head = fh.read(3)
    check("list file ditulis tanpa BOM", head != b"\xef\xbb\xbf", repr(head))


def test_sorting() -> None:
    print("\n[3] Pengurutan")
    names = ["v1.mp4", "v10.mp4", "v2.mp4", "v100.mp4", "v9.mp4"]
    got = sorted(names, key=natural_key)
    check("natural sort v1<v2<v9<v10<v100",
          got == ["v1.mp4", "v2.mp4", "v9.mp4", "v10.mp4", "v100.mp4"], str(got))

    cases = {
        "CH01_20240105080000.mp4": (2024, 1, 5, 8, 0, 0),
        "rekaman 2024-01-05 08.00.00.mp4": (2024, 1, 5, 8, 0, 0),
        "20240105_080000.avi": (2024, 1, 5, 8, 0, 0),
    }
    for name, expect in cases.items():
        dt = parse_timestamp_from_name(name)
        check(f"tanggal dari '{name[:26]}'",
              dt is not None and (dt.year, dt.month, dt.day, dt.hour,
                                  dt.minute, dt.second) == expect, str(dt))
    check("nama tanpa tanggal -> None",
          parse_timestamp_from_name("IMG_1234.mp4") is None)
    check("tanggal mustahil ditolak",
          parse_timestamp_from_name("X_20241332990000.mp4") is None)


def test_truncation_guard(work: str) -> None:
    print("\n[4] Penjagaan hasil terpotong (ffmpeg rc=0 walau gagal)")
    src = ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2",
           "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]
    real = [make(os.path.join(work, f"t{i}.mp4"), *src) for i in range(1, 4)]
    ghost = os.path.join(work, "hilang.mp4")

    # Reproduce the trap first: a list naming a file that is not there still
    # exits 0, and quietly yields a fraction of the expected video.
    listing = write_concat_list(real[:1] + [ghost] + real[1:],
                                os.path.join(work, "gap.txt"))
    out = os.path.join(work, "gap.mp4")
    rc = subprocess.run([TOOLS.ffmpeg, "-hide_banner", "-loglevel", "error",
                         "-y", "-f", "concat", "-safe", "0", "-i", listing,
                         "-c", "copy", out],
                        capture_output=True).returncode
    got = real_duration(out) if os.path.exists(out) else 0.0
    check("bukti: ffmpeg lapor sukses walau file hilang", rc == 0, f"rc={rc}")
    check("bukti: output memang terpotong", got < 4.0,
          f"durasi {got:.2f} dari 6.0 yang diharapkan")
    print(f"        (rc={rc} tapi hanya {got:.2f} detik dari 6.00 detik)")

    files = [probed(x) for x in real]
    job = MergeJob(files=files, output_path=out)
    merger = Merger(TOOLS, job)

    # 1. The pre-flight check must catch an input that vanished.
    gone = probed(real[0])
    gone.path = ghost
    try:
        merger._check_inputs_readable([gone])
        check("pra-cek menolak file yang hilang", False, "tidak melempar error")
    except MergeError as exc:
        check("pra-cek menolak file yang hilang", "hilang.mp4" in str(exc))

    # 2. The post-merge check must reject the short file above.
    try:
        merger._verify_output(out, 6.0, files, MergeMode.COPY)
        check("verifikasi durasi menolak hasil terpotong", False,
              "hasil terpotong lolos sebagai sukses")
    except MergeError as exc:
        check("verifikasi durasi menolak hasil terpotong",
              "TERPOTONG" in str(exc))

    # 3. A genuine result must still pass.
    good = os.path.join(work, "good.mp4")
    total = concat_to(real, good, work)
    try:
        merger._verify_output(good, 6.0, files, MergeMode.COPY)
        check("hasil utuh lolos verifikasi", True)
    except MergeError as exc:
        check("hasil utuh lolos verifikasi", False, str(exc)[:120])
    print(f"        (hasil utuh {total:.2f} detik diterima)")


def test_tolerance() -> None:
    print("[4b] Toleransi durasi tidak boleh menelan satu klip pun")
    for count, dur, mode, label in (
            (100, 300.0, MergeMode.COPY, "100 x 5 menit, cepat"),
            (100, 300.0, MergeMode.REENCODE, "100 x 5 menit, encode ulang"),
            (100, 3.0, MergeMode.COPY, "100 x 3 detik, cepat"),
            (2, 300.0, MergeMode.COPY, "2 x 5 menit, cepat")):
        files = [VideoFile(path=f"{i}.mp4", duration=dur) for i in range(count)]
        tol = Merger._duration_tolerance(files, mode)
        check(f"{label}: kehilangan 1 klip terdeteksi", tol < dur,
              f"toleransi {tol:.1f}s >= durasi klip {dur}s")


def test_windows_traps(work: str) -> None:
    print("\n[5] Jebakan Windows")
    # Explorer puts the more padded spelling first.
    got = sorted(["video5.mp4", "video05.mp4"], key=explorer_sort_key)
    check("video05 sebelum video5 (aturan Explorer)",
          got[0] == "video05.mp4", str(got))
    got = sorted(["cam_1.mp4", "cam_001.mp4"], key=explorer_sort_key)
    check("cam_001 sebelum cam_1", got[0] == "cam_001.mp4", str(got))

    # StrCmpLogicalW calls these equal; the tiebreak must make it repeatable.
    a = sorted(["a.mp4", "A.mp4"], key=explorer_sort_key)
    b = sorted(["A.mp4", "a.mp4"], key=explorer_sort_key)
    check("urutan beda-huruf-besar deterministik", a == b, f"{a} vs {b}")

    # Hikvision appends milliseconds, making 17 digits.
    dt = parse_timestamp_from_name("192.168.1.64_01_20240105080000123.mp4")
    check("pola Hikvision 17 digit (dengan milidetik) terbaca",
          dt is not None and dt.hour == 8 and dt.day == 5, str(dt))

    # A junction pointing at its own parent must not be walked.
    root = os.path.join(work, "junc_root")
    inner = os.path.join(root, "inner")
    os.makedirs(inner, exist_ok=True)
    src = ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25:duration=1",
           "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]
    make(os.path.join(root, "one.mp4"), *src)
    make(os.path.join(inner, "two.mp4"), *src)
    link = os.path.join(inner, "loop")
    created = subprocess.run(["cmd", "/c", "mklink", "/J", link, root],
                             capture_output=True).returncode == 0
    if created:
        found = scan_folder(root, recursive=True)
        check("junction melingkar tidak ditelusuri", len(found) == 2,
              f"menemukan {len(found)} file, seharusnya 2")
    else:
        print("  SKIP  junction (mklink tidak tersedia)")


def test_progress(work: str) -> None:
    print("\n[6] Progres tidak mundur dan berakhir tepat di 100%")
    src = ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2",
           "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]
    wide = ["-f", "lavfi", "-i", "testsrc=size=640x480:rate=25:duration=2",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]
    a = make(os.path.join(work, "p1.mp4"), *src)
    b = make(os.path.join(work, "p2.mp4"), *src)
    c = make(os.path.join(work, "p3.mp4"), *wide)   # forces the SMART path

    for mode, label in ((MergeMode.COPY, "cepat"),
                        (MergeMode.SMART, "hemat"),
                        (MergeMode.REENCODE, "encode ulang")):
        files = [probed(a), probed(b)] if mode is MergeMode.COPY else                 [probed(a), probed(b), probed(c)]
        seen: list[float] = []
        out = os.path.join(work, f"prog_{mode.value}.mp4")
        job = MergeJob(files=files, output_path=out, mode=mode,
                       target=TargetSpec(preset="ultrafast"))
        Merger(TOOLS, job, on_progress=lambda p: seen.append(p.fraction),
               on_log=lambda line: None).run()
        backwards = [i for i in range(1, len(seen))
                     if seen[i] < seen[i - 1] - 1e-9]
        check(f"progres {label}: tidak pernah mundur", not backwards,
              f"mundur di {[(round(seen[i-1],3), round(seen[i],3)) for i in backwards[:3]]}")
        check(f"progres {label}: tidak melewati 100%",
              all(v <= 1.0001 for v in seen), f"maks {max(seen):.3f}")
        check(f"progres {label}: berakhir di 100%",
              abs(seen[-1] - 1.0) < 1e-6, f"berakhir {seen[-1]:.4f}")


def test_smart_selection(work: str) -> None:
    print("[7] Mode hemat hanya meng-encode ulang yang berbeda")
    folder = os.path.join(work, "hemat")
    os.makedirs(folder, exist_ok=True)
    total, odd = 8, 2
    for i in range(1, total + 1):
        size = "320x180" if i > total - odd else "640x360"
        make(os.path.join(folder, f"cam_{i:03d}.mp4"),
             "-f", "lavfi", "-i", f"testsrc=size={size}:rate=25:duration=1",
             "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
             "-c:v", "libx264", "-preset", "ultrafast", "-profile:v", "main",
             "-pix_fmt", "yuv420p", "-c:a", "aac", "-ar", "48000", "-ac", "2",
             "-shortest")

    files = sort_files(scan_folder(folder), SortKey.NAME)
    probe_many(TOOLS, files)
    valid = [f for f in files if f.valid]
    check("8 klip terbaca", len(valid) == total, str(len(valid)))

    planned: list[int] = []
    logs: list[str] = []
    out = os.path.join(work, "hemat.mp4")
    job = MergeJob(files=valid, output_path=out, mode=MergeMode.SMART,
                   target=TargetSpec(preset="ultrafast"))

    def on_progress(p):
        if p.stage is Stage.NORMALIZING and p.total_items:
            planned.append(p.total_items)

    Merger(TOOLS, job, on_progress=on_progress, on_log=logs.append).run()

    # libx264 writes High unless told otherwise, so before -profile:v was
    # passed through, none of the re-encoded clips matched the untouched ones
    # and every single file got re-encoded.
    count = max(planned) if planned else 0
    check(f"hanya {odd} dari {total} file di-encode ulang", count == odd,
          f"{count} file di-encode ulang")
    check("tidak perlu penyamaan ulang",
          not any("masih berbeda" in line for line in logs))
    check("durasi hasil benar",
          abs(real_duration(out) - float(total)) < 1.0,
          f"{real_duration(out):.2f} vs {total}.0")


def decode_warnings(path: str) -> list[str]:
    """Lines ffmpeg emits when re-reading the file. Should be none."""
    res = subprocess.run(
        [TOOLS.ffmpeg, "-v", "error", "-i", path, "-f", "null", "-"],
        capture_output=True, text=True)
    return [ln for ln in (res.stderr or "").splitlines() if ln.strip()]


def test_clean_output(work: str) -> None:
    print("[8] Hasil tiap mode bebas peringatan timestamp")
    folder = os.path.join(work, "bersih")
    os.makedirs(folder, exist_ok=True)
    # One clip differs, so AUTO takes the mode where normalised segments are
    # joined to untouched originals - the combination that used to emit
    # "non monotonically increasing dts" at every seam.
    for i, size in enumerate(["640x360"] * 4 + ["320x180"], start=1):
        make(os.path.join(folder, f"v{i}.mp4"),
             "-f", "lavfi", "-i", f"testsrc=size={size}:rate=25:duration=1",
             "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
             "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-ar", "48000", "-ac", "2", "-shortest")

    files = sort_files(scan_folder(folder), SortKey.NAME)
    probe_many(TOOLS, files)
    valid = [f for f in files if f.valid]

    for mode in (MergeMode.AUTO, MergeMode.SMART, MergeMode.REENCODE):
        out = os.path.join(work, f"bersih_{mode.value}.mp4")
        Merger(TOOLS, MergeJob(files=valid, output_path=out, mode=mode,
                               target=TargetSpec(preset="ultrafast")),
               on_progress=lambda p: None, on_log=lambda line: None).run()
        problems = decode_warnings(out)
        check(f"mode {mode.value}: tidak ada peringatan saat dibaca ulang",
              not problems, "; ".join(problems[:2])[:150])
        check(f"mode {mode.value}: durasi mendekati {len(valid)}.0 detik",
              abs(real_duration(out) - float(len(valid))) < 0.6,
              f"{real_duration(out):.3f}")


def main() -> int:
    if not TOOLS:
        print("FFmpeg tidak ditemukan - tes dilewati.")
        return 2
    print(f"FFmpeg {TOOLS.version}")
    work = tempfile.mkdtemp(prefix="vmerge_test_")
    sections = [
        ("kompatibilitas", lambda: test_compatibility(work)),
        ("escaping", lambda: test_escaping(work)),
        ("pengurutan", test_sorting),
        ("hasil terpotong", lambda: test_truncation_guard(work)),
        ("toleransi", test_tolerance),
        ("jebakan windows", lambda: test_windows_traps(work)),
        ("progres", lambda: test_progress(work)),
        ("mode hemat", lambda: test_smart_selection(work)),
        ("keluaran bersih", lambda: test_clean_output(work)),
    ]
    try:
        # Each section is isolated: an exception in one used to abort the run
        # after printing only passes, which reads exactly like a clean pass
        # unless you check the exit code.
        for name, run in sections:
            try:
                run()
            except Exception as exc:
                import traceback
                FAILURES.append(f"bagian '{name}' menabrak error: {exc}")
                print(f"  ERROR bagian '{name}': {exc}")
                traceback.print_exc()
    finally:
        shutil.rmtree(work, ignore_errors=True)

    print(f"\n{'=' * 58}")
    if FAILURES:
        print(f"{PASSES} lulus, {len(FAILURES)} GAGAL:")
        for f in FAILURES:
            print("  - " + f)
        return 1
    print(f"SEMUA {PASSES} TES LULUS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
