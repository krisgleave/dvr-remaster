"""Generate desktop icons from the DVR Remaster branding assets."""

from pathlib import Path
from PIL import Image
import shutil

root = Path(__file__).resolve().parents[1]
assets = root / "assets"
target = root / "desktop/FpvDvrCleaner.Desktop/Assets"

target.mkdir(parents=True, exist_ok=True)

image = Image.open(assets / "AppIcon.png").convert("RGBA")

image.save(target / "app.png")
image.save(
    target / "app.ico",
    format="ICO",
    sizes=[(16, 16), (32, 32), (48, 48),
           (64, 64), (128, 128), (256, 256)]
)

shutil.copy2(
    assets / "AppIcon.icns",
    target / "Remaster.icns"
)

print("DVR Remaster application icons generated.")
