"""Builds the install folder and zip in ..\\..\\build\\package from a finished build. Run with Python 3.

    make_package.py <BepInEx x86 folder> <openxr loader licence> <BepInEx licence text>

The zip stays local. It is not to be published until the mod is cleared for sharing.
"""
import os
import shutil
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
BUILD = os.path.normpath(os.path.join(HERE, "..", "..", "build"))
PKG = os.path.join(BUILD, "package", "OriVR")
bep, xr_licence, bep_licence = sys.argv[1], sys.argv[2], sys.argv[3]

if os.path.isdir(PKG):
    shutil.rmtree(PKG)
files = os.path.join(PKG, "files")
os.makedirs(files)
for name in ("winhttp.dll", "doorstop_config.ini", ".doorstop_version"):
    shutil.copy2(os.path.join(bep, name), files)
shutil.copytree(os.path.join(bep, "BepInEx"), os.path.join(files, "BepInEx"))

plugin = os.path.join(files, "BepInEx", "plugins", "OriVR")
os.makedirs(plugin)
for name in ("OriVR.dll", "OriVRNative.dll", "openxr_loader.dll"):
    shutil.copy2(os.path.join(BUILD, name), plugin)

config = os.path.join(files, "BepInEx", "config")
os.makedirs(config)
# Only the setting that differs from BepInEx's default. BepInEx fills in the rest on first start.
with open(os.path.join(config, "BepInEx.cfg"), "w", encoding="ascii", newline="\r\n") as f:
    f.write("[Preloader.Entrypoint]\n\nType = Camera\n")

for name in ("Install OriVR.bat", "README.txt", "THIRD-PARTY-NOTICES.txt"):
    with open(os.path.join(HERE, name), "rb") as src:
        text = src.read().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")  # Windows line endings, whatever git did
    with open(os.path.join(PKG, name), "wb") as dst:
        dst.write(text)
licences = os.path.join(PKG, "licences")
os.makedirs(licences)
shutil.copy2(xr_licence, os.path.join(licences, "OpenXR-loader-LICENSE.txt"))
shutil.copy2(bep_licence, os.path.join(licences, "BepInEx-LICENSE.txt"))
shutil.copy2(os.path.join(HERE, "..", "LICENSE"), os.path.join(licences, "OriVR-LICENSE.txt"))

zip_path = os.path.join(BUILD, "package", "OriVR-0.8.0.zip")
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, names in os.walk(PKG):
        for n in names:
            full = os.path.join(root, n)
            z.write(full, os.path.join("OriVR", os.path.relpath(full, PKG)))
print(zip_path, os.path.getsize(zip_path) // 1024, "KB")
