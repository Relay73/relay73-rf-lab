#!/usr/bin/env python3
"""Assemble a small, reproducible IONOS-derived Linux developer preview.

Only the portable channel and freely generated audio fixtures are distributed;
the old research tree and third-party Teensy/VARA artifacts are not included.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
PROJECT = ROOT / "samples" / "FakeRadio.Examples" / "FakeRadio.Examples.csproj"

SOURCES = (
    "IonosChannel.h", "IonosChannel.cpp", "IonosGaussianTaps.h",
    "wav_cli.cpp", "tests.cpp", "check_wav.py",
    "Makefile", "README.md", "IONOS-LICENSE.txt", "LICENSE", "build_bundle.py",
)


def build(destination: Path) -> Path:
    if platform.system() != "Linux" or platform.machine() not in ("x86_64", "AMD64"):
        raise RuntimeError("This developer-preview packager supports Linux x86_64 only")
    subprocess.run(["make", "-C", str(HERE), "all", "test"], check=True)
    subprocess.run(["python3", str(HERE / "check_wav.py")], check=True)
    subprocess.run(["dotnet", "run", "--project", str(PROJECT), "-c", "Release",
                    "--", "--test-native"], check=True)
    destination.mkdir(parents=True, exist_ok=True)
    final = destination / "FakeRadio-IONOS-Linux-x64-DeveloperPreview.zip"
    with tempfile.TemporaryDirectory(prefix="ionos-pack-") as temporary:
        temp = Path(temporary)
        stage = temp / "FakeRadio-IONOS-Linux-x64"
        stage.mkdir()
        for entry in SOURCES:
            shutil.copy2(HERE / entry, stage / entry)
        for entry in ("ionos-wav", "libionos_channel.so", "ionos-tests"):
            shutil.copy2(HERE / entry, stage / entry)

        subprocess.run(["dotnet", "run", "--project", str(PROJECT), "-c", "Release", "--",
                        "--ionos-examples", "--output", str(stage / "examples"),
                        "--seconds", "8"], check=True)
        entries = sorted(p for p in stage.rglob("*") if p.is_file())
        manifest = {
            "name": "FakeRadio IONOS-derived portable channel developer preview",
            "platform": "Linux x86_64",
            "upstream": "https://github.com/ARSFI/HFSimulator",
            "provenance": "IONOS 2.06 Gaussian FIR taps; new portable C++ audio engine",
            "hardware_equivalence": "not verified",
            "tests": "native 5/5; WAV 4/4; .NET interop 1/1",
            "files": {
                str(f.relative_to(stage)): hashlib.sha256(f.read_bytes()).hexdigest()
                for f in entries
            },
        }
        (stage / "SHA256SUMS.json").write_text(json.dumps(manifest, indent=2) + "\n")
        entries.append(stage / "SHA256SUMS.json")
        staged_zip = temp / final.name
        with zipfile.ZipFile(staged_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=8) as archive:
            for p in entries:
                archive.write(p, (stage.name + "/" + str(p.relative_to(stage))))
        shutil.copy2(staged_zip, final)
    print(f"IONOS_PREVIEW_PASS file={final} bytes={final.stat().st_size} "
          f"sha256={hashlib.sha256(final.read_bytes()).hexdigest()}")
    return final


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--output", type=Path, default=ROOT / "artifacts")
    args = p.parse_args()
    build(args.output.resolve())


if __name__ == "__main__":
    main()
