#!/usr/bin/env python3
"""End-to-end checks against numbered (machine-readable) frames. Python stdlib only.

Requires dotnet 8, ffmpeg and ffprobe. The original is losslessly FFV1-encoded.
Every frame has a 16-bit black/white barcode so dropped/duplicated frames can
be detected after lossy H.264 re-encoding without relying only on frame counts.
"""
from __future__ import annotations

import json
import subprocess
import tempfile
from pathlib import Path

WIDTH = 160
HEIGHT = 90
FPS = 24
FRAME_COUNT = 73
ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / "src" / "VideoFrameCut.Cli" / "VideoFrameCut.Cli.csproj"


def run(*argv: str) -> bytes:
    process = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if process.returncode:
        raise AssertionError(
            f"Command failed (exit={process.returncode}): {' '.join(argv)}\n"
            f"stdout:\n{process.stdout.decode(errors='replace')}\n"
            f"stderr:\n{process.stderr.decode(errors='replace')}"
        )
    return process.stdout


def make_frame(path: Path, number: int) -> None:
    # Encode frame number directly in 16 thick vertical cells; no OCR required.
    data = bytearray(WIDTH * HEIGHT * 3)
    for y in range(HEIGHT):
        for x in range(WIDTH):
            shade = 110
            if 12 <= y < 78 and 16 <= x < 144:
                bit = (x - 16) // 8
                shade = 235 if (number >> bit) & 1 else 15
            i = (y * WIDTH + x) * 3
            data[i:i + 3] = bytes((shade, shade, shade))
    with path.open("wb") as stream:
        stream.write(f"P6\n{WIDTH} {HEIGHT}\n255\n".encode("ascii"))
        stream.write(data)


def decode_frame_numbers(path: Path) -> list[int]:
    raw = run(
        "ffmpeg", "-v", "error", "-i", str(path),
        "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", "gray", "-"
    )
    frame_size = WIDTH * HEIGHT
    assert len(raw) % frame_size == 0, f"Unexpected raw size: {path}"
    decoded = []
    for offset in range(0, len(raw), frame_size):
        number = 0
        for bit in range(16):
            x = 16 + bit * 8 + 4
            y = 40
            shade = raw[offset + y * WIDTH + x]
            if shade > 128:
                number |= 1 << bit
        decoded.append(number)
    return decoded


def assert_frames(path: Path, expected: list[int]) -> None:
    actual = decode_frame_numbers(path)
    assert actual == expected, (
        f"Incorrect source frame ownership for {path}: expected={expected}, "
        f"actual={actual}"
    )


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="framecut-integration-") as temp:
        home = Path(temp)
        pictures = home / "frames"
        pictures.mkdir()
        for frame in range(FRAME_COUNT):
            make_frame(pictures / f"frame_{frame:04}.ppm", frame)
        source = home / "source.mkv"
        run(
            "ffmpeg", "-nostdin", "-v", "error",
            "-framerate", str(FPS), "-start_number", "0",
            "-i", str(pictures / "frame_%04d.ppm"),
            "-c:v", "ffv1", "-pix_fmt", "yuv420p", "-y", str(source)
        )
        assert_frames(source, list(range(FRAME_COUNT)))

        info = run(
            "dotnet", "run", "--configuration", "Release",
            "--project", str(CLI), "--", "info", str(source)
        ).decode(errors="replace")
        assert "Display frames: 73" in info, info

        segments_dir = home / "segments"
        run(
            "dotnet", "run", "--no-build", "--configuration", "Release",
            "--project", str(CLI), "--", "split", str(source),
            "--seconds", "1", "--out", str(segments_dir),
            "--cpu", "--no-audio"
        )
        for number, expected in enumerate(
            [list(range(0, 24)), list(range(24, 48)),
             list(range(48, 72)), [72]], start=1
        ):
            assert_frames(segments_dir / f"source_{number:03}.mp4", expected)
        report = json.loads((segments_dir / "framecut_manifest.json").read_text())
        assert len(report["Clips"]) == 4
        assert all(row["Succeeded"] for row in report["Clips"])

        extract_dir = home / "extract"
        run(
            "dotnet", "run", "--no-build", "--configuration", "Release",
            "--project", str(CLI), "--", "extract", str(source),
            "--start-frame", "7", "--end-frame", "51", "--out", str(extract_dir),
            "--cpu", "--no-audio"
        )
        assert_frames(extract_dir / "source_001.mp4", list(range(7, 51)))

        selection_dir = home / "selection"
        run(
            "dotnet", "run", "--no-build", "--configuration", "Release",
            "--project", str(CLI), "--", "split", str(source),
            "--seconds", "1", "--out", str(selection_dir),
            "--segments", "2-3", "--cpu", "--no-audio"
        )
        assert_frames(selection_dir / "source_001.mp4", list(range(24, 48)))
        assert_frames(selection_dir / "source_002.mp4", list(range(48, 72)))
        assert not (selection_dir / "source_003.mp4").exists()

    print("PASS CFR numbered-frame export: split 4 clips + non-keyframe extract + selected clips")
    print("PASS frame identity: no missing, repeated or reordered source frames")


if __name__ == "__main__":
    main()
