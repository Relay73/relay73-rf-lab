#!/usr/bin/env python3
"""Create a self-contained Windows FakeRadio FM-only lab candidate on Linux.

The FM RF engine is managed C# and requires no native IONOS DLL. Profiles
starting IONOS_ still require their independent native library and therefore
will fail closed in this package; no silent substitution is permitted.

This script builds and packages but does NOT assert tested Windows audio I/O.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

HERE=Path(__file__).resolve().parent
ROOT=HERE.parent
BRIDGE=ROOT/"src"/"FakeRadio.AudioNode"/"FakeRadio.AudioNode.csproj"
EXAMPLES=ROOT/"samples"/"FakeRadio.Examples"/"FakeRadio.Examples.csproj"
EXTRA=("README.md","Start-FakeRadio.cmd","Show-Devices.cmd",
       "Change-Profile.cmd","Change-Profile.ps1")
WARNINGS="""# FakeRadio VHF/UHF FM RF Windows developer candidate

This ZIP contains a self-contained .NET 10 Windows x64 FakeRadio with the
new managed C# FM modulator / RF propagation / FM-demodulator / squelch
channel engine. No radio hardware is needed for software-only evaluation.

**Native IONOS HF is not bundled** in this FM-only candidate: selecting
IONOS_* will return an error instead of switching to a different algorithm.
The separately documented full Windows build requires Visual Studio C++
Build Tools to compile ionos_channel.dll.

Run Start-FakeRadio.cmd and select profiles FM_RF_REFERENCE,
FM_VHF_FIXED, FM_VHF_MOBILE, FM_UHF_URBAN, FM_UHF_EDGE, FM_BUSY_SHARED,
FM_BAD_AUDIO. Keep PTT physically disconnected and disable external rig
control when using the virtual lab.

The virtual-audio driver is NOT installed by FakeRadio. See README.md for
two-cable wiring. For test results refer to source
native/ionos/run_fm_modem73_lab.py, which does not run on Windows
without the pinned Relay73 Modem73 source tree.

Source FM channel model has been tested on two isolated Windows virtual-audio hosts. Treat it as
a lab candidate, not production gateway software. Do not deploy to a live
Winlink RMS node or attach physical radios.
"""

def exec_checked(args: list[str]):
    subprocess.run(args,check=True)

def package(outdir: Path):
    exec_checked(["dotnet","run","--project",str(EXAMPLES),"-c","Release","--","--test"])
    exec_checked(["dotnet","run","--project",str(EXAMPLES),"-c","Release","--","--test-fm"])
    with tempfile.TemporaryDirectory(prefix="fakeradio-fm-windows-") as temp:
        temp=Path(temp)
        stage=temp/"stage"
        stage.mkdir()
        exec_checked(["dotnet","publish",str(BRIDGE),"-c","Release","-r","win-x64",
                      "--self-contained","true","-p:PublishSingleFile=true",
                      "-p:PublishTrimmed=false","-o",str(stage)])
        if not (stage/"FakeRadio.exe").is_file():
            raise RuntimeError("Published Windows FakeRadio.exe not found")
        exec_checked(["dotnet","run","--project",str(EXAMPLES),"-c","Release","--",
                      "--fm-examples","--output",str(stage/"examples"/"fm"),"--seconds","8"])
        for filename in EXTRA:
            shutil.copy2(HERE/filename,stage/filename)
        for notice in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
            shutil.copy2(ROOT/notice, stage/notice)
        (stage/"FM-ONLY-CANDIDATE.md").write_text(WARNINGS)
        for pdb in stage.glob("*.pdb"):
            pdb.unlink()
        paths=sorted(p for p in stage.rglob("*") if p.is_file())
        manifest={
            "name":"FakeRadio FM RF Windows x64 developer candidate",
            "status":"Compiled on Linux, Windows audio path NOT validated",
            "engine":"384 kHz complex FM model managed C#",
            "native_ionos_library":"NOT INCLUDED — IONOS_* profiles fail closed",
            "audio":"48 kHz float32 inter-node PCM and Windows virtual soundcards",
            "files":{p.relative_to(stage).as_posix():hashlib.sha256(p.read_bytes()).hexdigest()
                     for p in paths},
        }
        (stage/"SHA256SUMS.json").write_text(json.dumps(manifest,indent=2)+"\n")
        paths.append(stage/"SHA256SUMS.json")
        outdir.mkdir(parents=True,exist_ok=True)
        target=outdir/"FakeRadio-FM-RF-Windows-x64-CANDIDATE.zip"
        staged_zip=temp/target.name
        with zipfile.ZipFile(staged_zip,"w",zipfile.ZIP_DEFLATED,compresslevel=7) as z:
            for p in paths:z.write(p,p.relative_to(stage).as_posix())
        shutil.copy2(staged_zip,target)
    print(f"FAKERADIO_FM_WINDOWS_CANDIDATE_OK file={target} bytes={target.stat().st_size} "
          f"sha256={hashlib.sha256(target.read_bytes()).hexdigest()}")

if __name__=="__main__":
    p=argparse.ArgumentParser()
    p.add_argument("--output",type=Path,default=ROOT/"artifacts")
    args=p.parse_args()
    package(args.output.resolve())
