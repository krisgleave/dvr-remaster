from pathlib import Path
import hashlib
import json
import sys
import ssl
import re
import platform
import importlib.metadata
import cv2

repo = Path(__file__).resolve().parents[1]
project = repo.parents[1]

expected = json.loads(
    (repo / "scripts/release-dependencies.json").read_text()
)

def check(condition, message):
    if not condition:
        raise SystemExit("BUILD BLOCKED: " + message)

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

check(platform.machine() == "arm64", "Incorrect architecture")
check(platform.python_version() == expected["python"],
      "Incorrect Python runtime")
check(ssl.OPENSSL_VERSION.startswith(expected["openssl"]),
      "Incorrect OpenSSL version")

for name, version in expected["packages"].items():
    check(importlib.metadata.version(name) == version,
          f"Incorrect {name} version")

cv_binary = Path(cv2.__file__).parent / "cv2.abi3.so"

check(digest(cv_binary) == expected["sha256"]["opencv"],
      "Unapproved OpenCV binary")

check(not re.search(r"FFMPEG:\\s+YES", cv2.getBuildInformation()),
      "OpenCV contains FFmpeg integration")

for name in ("ffmpeg", "ffprobe"):
    path = project / "Dependencies/ffmpeg" / name
    check(digest(path) == expected["sha256"][name],
          f"Unapproved {name} binary")

print("Release dependency verification: PASS")
