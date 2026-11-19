from pathlib import Path
import shutil, plistlib, subprocess, tomllib
root=Path(__file__).resolve().parents[1]
source=root/'Github/dvr-remaster'
folder=root/'local-build/Test Release'
app=folder/'DVR Remaster.app'
mac=app/'Contents/MacOS'
res=app/'Contents/Resources'
if app.exists():
 shutil.rmtree(app)
mac.mkdir(parents=True,exist_ok=True)
res.mkdir(parents=True,exist_ok=True)
payload=mac
shutil.copytree(root/'local-build/desktop',payload,dirs_exist_ok=True,symlinks=True)
shutil.copytree(root/'local-build/engine-dist/flugbild-engine',payload/'engine',dirs_exist_ok=True,symlinks=True)
shutil.copytree(root/'Dependencies/ffmpeg',payload/'ffmpeg',dirs_exist_ok=True,symlinks=True)
for name in ['README.md','LICENSE','THIRD-PARTY-NOTICES.md']:
 shutil.copy2(source/name,payload/name)
shutil.copy2(source/'packaging/START-HERE.txt',payload/'START-HERE.txt')
shutil.copy2(source/'desktop/FpvDvrCleaner.Desktop/Assets/app.png',res/'app.png')
info=plistlib.loads((source/'packaging/Info.plist').read_bytes().replace(b'@VERSION@',tomllib.loads((source/'pyproject.toml').read_text())['project']['version'].encode()))
shutil.copy2(source/'assets/AppIcon.icns', res/'Remaster.icns')
info.update(CFBundleIconFile='Remaster.icns', CFBundleName='DVR Remaster',CFBundleDisplayName='DVR Remaster',CFBundleIdentifier='tech.kristalks.dvrremaster',CFBundleExecutable='FpvDvrCleaner.Desktop')
(app/'Contents/Info.plist').write_bytes(plistlib.dumps(info))

for name in ['COPYRIGHT.md', 'BRANDING.md']:
    shutil.copy2(source/name, payload/name)

shutil.copytree(
    source/'licenses',
    payload/'Third-Party-Licences',
    dirs_exist_ok=True
)

print(app)
