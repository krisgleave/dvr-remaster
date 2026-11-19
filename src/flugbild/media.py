from __future__ import annotations

from dataclasses import dataclass
from fractions import Fraction
import json
from pathlib import Path
import shutil
import subprocess


PROCESS_FLAGS = getattr(subprocess, "CREATE_NO_WINDOW", 0)


@dataclass(frozen=True)
class VideoKenndaten:
    duration: float
    width: int
    height: int
    has_audio: bool
    fps: float = 0.0
    codec: str = ""
    sar: float = 1.0
    bitrate: int = 0
    audio_codec: str = ""
    size: int = 0


def _rate(text: str | None) -> float:
    """Frame rate from an ffprobe fraction such as "50/1"; 0 if unusable."""
    try:
        rate = float(Fraction(text or "0"))
    except (ValueError, ZeroDivisionError):
        return 0.0
    return rate if 0 < rate <= 240 else 0.0


def tools_available() -> bool:
    return bool(shutil.which("ffmpeg") and shutil.which("ffprobe"))


def inspect_video(path: Path) -> VideoKenndaten:
    if not path.is_file():
        raise FileNotFoundError(path)
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json", str(path)],
        capture_output=True, text=True, check=True, timeout=30,
        creationflags=PROCESS_FLAGS,
    )
    data = json.loads(result.stdout)
    streams = data.get("streams", [])
    video = next((stream for stream in streams if stream.get("codec_type") == "video"), None)
    if video is None:
        raise ValueError("No video stream found")
    duration = float(data.get("format", {}).get("duration") or video.get("duration") or 0)
    if duration <= 0:
        raise ValueError("Could not read video duration")
    # DVR transport streams can report a container average that differs from
    # the real frame spacing; the stream's base rate matches the packets.
    fps = _rate(video.get("r_frame_rate")) or _rate(video.get("avg_frame_rate"))
    try:
        sar = float(Fraction(str(video.get("sample_aspect_ratio") or "1:1").replace(":", "/")))
        if not 0.01 <= sar <= 100: sar = 1.0
    except (ValueError, ZeroDivisionError):
        sar = 1.0
    audio = next((stream for stream in streams if stream.get("codec_type") == "audio"), {})
    bitrate = str(video.get("bit_rate") or "")
    return VideoKenndaten(duration, int(video["width"]), int(video["height"]),
                          any(stream.get("codec_type") == "audio" for stream in streams), fps,
                          str(video.get("codec_name") or ""), sar,
                          int(bitrate) if bitrate.isdigit() else 0,
                          str(audio.get("codec_name") or ""), path.stat().st_size)


def still_frame(path: Path, seconds: float, destination: Path, filters: str = "") -> None:
    args = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", str(max(0, seconds)),
            "-i", str(path), "-frames:v", "1"]
    if filters:
        args.extend(["-vf", filters])
    args.append(str(destination))
    subprocess.run(args, capture_output=True, text=True, check=True, timeout=90,
                   creationflags=PROCESS_FLAGS)


def decode_frames(path: Path, seconds: float, width: int, height: int,
                  count: int = 1, timeout: float = 30):
    """Decode consecutive BGR frames using the standalone FFmpeg."""
    import numpy as np

    args = [
        "ffmpeg", "-hide_banner", "-nostdin", "-loglevel", "error",
        "-ss", f"{max(0, seconds):.9f}", "-noautorotate",
        "-i", str(path), "-map", "0:v:0",
        "-an", "-sn", "-dn",
        "-frames:v", str(count),
        "-fps_mode", "passthrough",
        "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1",
    ]

    result = subprocess.run(
        args, capture_output=True, timeout=timeout,
        creationflags=PROCESS_FLAGS,
    )

    if result.returncode:
        return []

    size = width * height * 3

    return [
        np.frombuffer(result.stdout, dtype=np.uint8,
                      count=size, offset=index)
        .reshape(height, width, 3).copy()
        for index in range(0, len(result.stdout) - size + 1, size)
    ]


def video_sampling_info(path: Path):
    """Metadata for sampling video without OpenCV VideoCapture."""
    info = inspect_video(path)
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=nb_frames,duration,avg_frame_rate",
         "-of", "json", str(path)],
        capture_output=True, text=True, check=True, timeout=30,
        creationflags=PROCESS_FLAGS,
    )
    stream = json.loads(result.stdout).get("streams", [{}])[0]
    fps = info.fps or _rate(stream.get("avg_frame_rate"))
    if not fps:
        raise ValueError("Could not read video frame rate")
    try:
        total = int(stream.get("nb_frames") or 0)
    except (ValueError, TypeError):
        total = 0
    if total <= 0:
        try:
            duration = float(stream.get("duration") or info.duration)
        except (ValueError, TypeError):
            duration = info.duration
        total = max(1, round(duration * fps))
    return info.width, info.height, fps, total
