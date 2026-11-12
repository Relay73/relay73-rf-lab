#!/usr/bin/env python3
"""Build Windows FakeRadio package from the source checkout (no RF or modem needed)."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
HERE = Path(__file__).resolve().parent
BRIDGE = ROOT / "src" / "FakeRadio.AudioNode" / "FakeRadio.AudioNode.csproj"
EXAMPLES = ROOT / "samples" / "FakeRadio.Examples" / "FakeRadio.Examples.csproj"
IONOS = ROOT / "native" / "ionos"
EXTRA = ("README.md", "Start-FakeRadio.cmd", "Show-Devices.cmd",
         "Change-Profile.cmd", "Change-Profile.ps1")


def package(stage: Path, archive: Path) -> None:
    # Native cross-compiler is mandatory: otherwise a Windows release could show
    # IONOS options that silently fail on user machines.
    compiler = shutil.which("x86_64-w64-mingw32-g++")
    if not compiler:
        raise RuntimeError("MinGW-w64 cross-compiler missing. Install x86_64-w64-mingw32-g++ "
                           "or run Build-Windows.ps1 from the MSVC x64 Native Tools prompt.")
    subprocess.run(["make", "-C", str(IONOS), "all", "test"], check=True)
    subprocess.run(["dotnet", "run", "--project", str(EXAMPLES), "-c", "Release",
                    "--", "--test-fm"], check=True)
    subprocess.run([compiler, "-std=c++17", "-O2", "-static-libgcc", "-static-libstdc++", "-shared",
                    str(IONOS / "IonosChannel.cpp"), "-o", str(stage / "ionos_channel.dll")], check=True)
    subprocess.run([compiler, "-std=c++17", "-O2", "-static-libgcc", "-static-libstdc++",
                    str(IONOS / "IonosChannel.cpp"), str(IONOS / "wav_cli.cpp"),
                    "-o", str(stage / "ionos-wav.exe")], check=True)
    shutil.copy2(IONOS / "IONOS-LICENSE.txt", stage / "IONOS-LICENSE.txt")
    shutil.copy2(IONOS / "LICENSE", stage / "FakeRadio-IONOS-LICENSE.txt")
    subprocess.run(["dotnet", "run", "--project", str(EXAMPLES), "-c", "Release",
                    "--", "--test"], check=True)
    subprocess.run(["dotnet", "publish", str(BRIDGE), "-c", "Release",
                    "-r", "win-x64", "--self-contained", "true",
                    "-p:PublishSingleFile=true", "-p:PublishTrimmed=false",
                    "-o", str(stage)], check=True)
    subprocess.run(["dotnet", "run", "--project", str(EXAMPLES), "-c", "Release",
                    "--", "--output", str(stage / "examples" / "audio"),
                    "--seconds", "8"], check=True)

    subprocess.run(["dotnet", "run", "--project", str(EXAMPLES), "-c", "Release",
                    "--", "--ionos-examples", "--output", str(stage / "examples" / "ionos"),
                    "--seconds", "8"], check=True)

    subprocess.run(["dotnet", "run", "--project", str(EXAMPLES), "-c", "Release",
                    "--", "--fm-examples", "--output", str(stage / "examples" / "fm"),
                    "--seconds", "8"], check=True)

    if not (stage / "FakeRadio.exe").is_file() or not (stage / "ionos_channel.dll").is_file():
        raise RuntimeError("Publish reported success but FakeRadio.exe is missing")
    # Debug symbols may contain absolute development paths, omit from distributions.
    for symbol in stage.glob("*.pdb"):
        symbol.unlink()
    for filename in EXTRA:
        shutil.copy2(HERE / filename, stage / filename)
    for notice in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        shutil.copy2(ROOT / notice, stage / notice)

    # A fresh temporary stage ensures that captures, configuration files, and
    # old build outputs can never leak into an end-user archive.
    entries = sorted(p for p in stage.rglob("*") if p.is_file())
    manifest = {
        "name": "FakeRadio",
        "platform": "Windows x64",
        "channel_audio": "48 kHz mono float32 over private TCP",
        "profile_demos": "original synthetic tones, not real modem waveforms",
        "native_channel": "IONOS-derived C++ MIT (not hardware bit-identical)", 
        "files": {
            p.relative_to(stage).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in entries
        },
    }
    manifest_path = stage / "SHA256SUMS.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    entries.append(manifest_path)
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=7) as bundle:
        for p in entries:
            bundle.write(p, arcname=p.relative_to(stage).as_posix())
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    print(f"FAKERADIO_PACKAGE_OK archive={archive} files={len(entries)} "
          f"bytes={archive.stat().st_size} sha256={digest}", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Build self-contained Windows FakeRadio ZIP with WAV demos")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    archive = output / "FakeRadio-Windows-x64.zip"
    # Zip output is not overwritten unless all publish and sample steps pass.
    with tempfile.TemporaryDirectory(prefix="fakeradio-pack-") as folder:
        temp_archive = Path(folder) / archive.name
        stage = Path(folder) / "staging"
        stage.mkdir()
        package(stage, temp_archive)
        shutil.copy2(temp_archive, archive)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"FAKERADIO_PACKAGE_FAIL {error}", file=sys.stderr, flush=True)
        sys.exit(1)
