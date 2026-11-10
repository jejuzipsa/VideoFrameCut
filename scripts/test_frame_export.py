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


def frame_pts(path: Path) -> list[float]:
    text = run(
        "ffprobe", "-v", "error", "-select_streams", "v:0",
        "-show_frames", "-show_entries", "frame=best_effort_timestamp_time",
        "-of", "compact=p=0:nk=0", str(path)
    ).decode()
    result = []
    for line in text.splitlines():
        for field in line.split("|"):
            if field.startswith("best_effort_timestamp_time="):
                result.append(float(field.split("=", 1)[1]))
                break
    return result


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

        # Real H.264 GOP with B-frames; extract begins between keyframes.
        # Includes audio to exercise the optional audio trim/transcode path.
        h264_source = home / "h264_source.mp4"
        run(
            "ffmpeg", "-nostdin", "-v", "error",
            "-framerate", str(FPS), "-start_number", "0",
            "-i", str(pictures / "frame_%04d.ppm"),
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3.2",
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "libx264", "-g", "48", "-keyint_min", "48", "-bf", "2",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", str(h264_source)
        )
        assert_frames(h264_source, list(range(FRAME_COUNT)))
        h264_out = home / "h264_out"
        run(
            "dotnet", "run", "--no-build", "--configuration", "Release",
            "--project", str(CLI), "--", "extract", str(h264_source),
            "--start-frame", "7", "--end-frame", "51",
            "--out", str(h264_out), "--cpu"
        )
        result_h264 = h264_out / "h264_source_001.mp4"
        assert_frames(result_h264, list(range(7, 51)))
        audio_info = run(
            "ffprobe", "-v", "error", "-select_streams", "a",
            "-show_entries", "stream=codec_type", "-of", "csv=p=0",
            str(result_h264)
        ).decode().strip()
        assert "audio" in audio_info, f"Audio was not retained: {audio_info}"

        # Uneven 40/120 ms source-frame intervals: output must preserve
        # original PTS differences, without frame drops or duplicates.
        vfr_numbers = list(range(16))
        vfr_source = home / "vfr_source.mkv"
        # Set irregular PTS directly on the 16 unique source frames. The concat
        # demuxer can repeat still images on some FFmpeg versions, so do not use
        # its still-image durations as a VFR fixture.
        ffmpeg_banner = run("ffmpeg", "-version").decode(errors="replace").splitlines()[0]
        import re
        match = re.search(r"ffmpeg version (?:n)?(\d+)", ffmpeg_banner)
        ffmpeg_major = int(match.group(1)) if match else 0
        setpts = "setpts=expr='(N*0.04+floor(N/2)*0.08)/TB'"
        # FFmpeg 8 preserves CFR metadata through setpts by default; strip it
        # when assigning variable timestamps to avoid downstream duplication.
        if ffmpeg_major >= 8:
            setpts += ":strip_fps=1"
        run(
            "ffmpeg", "-nostdin", "-v", "error",
            "-i", str(source),
            "-vf", setpts,
            "-frames:v", "16", "-fps_mode:v", "passthrough",
            "-c:v", "ffv1", "-pix_fmt", "yuv420p",
            "-y", str(vfr_source)
        )
        assert_frames(vfr_source, vfr_numbers)
        vfr_out = home / "vfr_out"
        run(
            "dotnet", "run", "--no-build", "--configuration", "Release",
            "--project", str(CLI), "--", "extract", str(vfr_source),
            "--start-frame", "2", "--end-frame", "13",
            "--out", str(vfr_out), "--cpu", "--no-audio"
        )
        vfr_result = vfr_out / "vfr_source_001.mp4"
        assert_frames(vfr_result, list(range(2, 13)))
        source_pts = frame_pts(vfr_source)[2:13]
        output_pts = frame_pts(vfr_result)
        assert len(source_pts) == len(output_pts)
        for i, (original_pts, exported_pts) in enumerate(zip(source_pts, output_pts)):
            difference = abs(
                (original_pts - source_pts[0]) -
                (exported_pts - output_pts[0])
            )
            assert difference < 0.004, f"VFR timestamp mismatch at frame {i}: {difference}"

    print("PASS CFR: 4 segments, exact nonkeyframe extraction and selection")
    print("PASS H.264 B-frames: nonkeyframe 44-frame output, original audio retained")
    print("PASS VFR: source PTS spacing preserved, frame identity validated")
    print("PASS unique frame identity: no missing, repeated or reordered source frames")


if __name__ == "__main__":
    main()
