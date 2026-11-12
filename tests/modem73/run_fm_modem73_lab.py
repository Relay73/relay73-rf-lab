#!/usr/bin/env python3
"""Reproducible off-air VHF/UHF FM channel benchmark against real Modem73.

- Source WAV is emitted by the pinned upstream Modem73 Encoder48k.
- FakeRadio's live managed FM RF processor produces impaired 48k audio.
- The unmodified pinned Modem73 decoder attempts exact payload recovery.
- No actual RF, PTT, KISS ports, radio drivers, or gateways are accessed.
"""
from __future__ import annotations
import argparse
import os
import csv
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import time
import wave
import zipfile

HERE = Path(__file__).resolve().parent
IONOS = HERE.parents[1] / 'native' / 'ionos'
ROOT = HERE.parents[1]
MODEM = Path(os.environ.get('MODEM73_ROOT', '../Relay73/native/modem73')).resolve()
EXAMPLES = ROOT / "samples" / "FakeRadio.Examples" / "FakeRadio.Examples.csproj"
EXE_DLL = ROOT / "samples" / "FakeRadio.Examples" / "bin" / "Release" / "net10.0" / "FakeRadio.Examples.dll"
BASELINE = ROOT / "artifacts" / "modem73-ionos-offair" / "modem73_CLEAN_reference.wav"
PROFILES = (
    "FM_RF_REFERENCE", "FM_VHF_FIXED", "FM_VHF_MOBILE",
    "FM_UHF_URBAN", "FM_UHF_EDGE", "FM_BUSY_SHARED", "FM_BAD_AUDIO",
)
SEEDS = (2026, 2027, 2028)
HEADER = ["profile", "seed", "payload_decoded", "rx_frames", "rx_callbacks",
          "decoder_snr_db", "decoder_ber_pct", "render_wall_ms", "decode_wall_ms"]

def command(args: list[str], accepted=(0,)):
    started = time.perf_counter()
    p = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    elapsed = round((time.perf_counter() - started)*1000, 2)
    if p.returncode not in accepted:
        raise RuntimeError(f"Command exit {p.returncode}: {' '.join(args)}\n{p.stdout[-4000:]}")
    return p, elapsed

def compile_decoder(target: Path):
    upstream = MODEM / "upstream"
    for name in ("modem.hh", "phy/common.hh", "deps/aicodix/dsp"):
        if not (upstream / name).exists():
            raise RuntimeError("Pinned Modem73 upstream files missing: " + name)
    dirs = [upstream, upstream / "phy", upstream / "deps/aicodix/dsp",
            upstream / "deps/aicodix/code", upstream / "deps/aicodix/modem", MODEM]
    command(["g++", "-std=c++17", "-O1", "-DNDEBUG",
             *(f"-I{d}" for d in dirs), str(HERE / "modem73_decode_f32.cpp"),
             "-o", str(target)])

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, default=BASELINE)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "fakeradio-fm-modem73-matrix")
    args = parser.parse_args()
    source=args.input.resolve()
    if not source.is_file():
        raise RuntimeError(f"Expected real Modem73 TX WAV not found: {source}; run run_modem73_lab.py first")
    with wave.open(str(source)) as w:
        if (w.getnchannels(),w.getsampwidth(),w.getframerate()) != (1,2,48000):
            raise RuntimeError("Real Modem73 PCM source format is not PCM16 mono 48k")
        samples=w.getnframes()
        if samples < 24000: raise RuntimeError("Real Modem73 source is too short")

    output=args.output.resolve()
    output.mkdir(parents=True,exist_ok=True)
    binary=output / "modem73-rx-offair"
    compile_decoder(binary)
    command(["dotnet","build",str(EXAMPLES),"-c","Release","-v:q"])
    rows=[]
    for seed in SEEDS:
        for profile in PROFILES:
            dest=output / f"seed_{seed}" / profile.lower()
            render, render_ms = command([
                "dotnet", str(EXE_DLL), "--fm-input", str(source), "--fm-profile", profile,
                "--fm-seed", str(seed), "--output", str(dest),
            ])
            waveform=dest / (profile.lower()+".f32")
            if not waveform.is_file() or waveform.stat().st_size != samples*4:
                raise RuntimeError(f"PCM output length mismatch for {profile} seed={seed}")
            decode, decode_ms = command([str(binary),str(waveform)],accepted=(0,10))
            match=re.search(r"FM_MODEM73_PHY_RESULT matched=(YES|NO) callbacks=(\d+) frames=(\d+)(?: snr_db=(-?[\d.]+) ber_percent=(-?[\d.]+))?",decode.stdout)
            if not match or (decode.returncode==0) != (match[1]=="YES"):
                raise RuntimeError(f"Modem73 RX did not return a trustworthy verdict: {profile} seed={seed}")
            rows.append(dict(zip(HEADER,[
                profile,seed,match[1],int(match[3]),int(match[2]),
                match[4] if match[4] is not None else "", match[5] if match[5] is not None else "",
                render_ms,decode_ms
            ])))
            # Keep exact stdout for troubleshooting receiver CRC failures.
            (dest / "receiver.log").write_text(decode.stdout)
            print(f"FM_TRIAL seed={seed} profile={profile} match={match[1]} decoded_snr={match[4] or 'NA'}",flush=True)

    reference=[r for r in rows if r["profile"]=="FM_RF_REFERENCE"]
    if any(r["payload_decoded"]!="YES" for r in reference):
        raise RuntimeError("Reference Modem73 PHY failed in one or more seeds; refusing misleading report")
    table=[]
    for profile in PROFILES:
        values=[r for r in rows if r["profile"]==profile]
        table.append({"profile":profile,"passed":sum(r["payload_decoded"]=="YES" for r in values),"trials":len(values)})
    path=output/"fm-modem73-results.csv"
    with path.open("w",newline="") as stream:
        csvout=csv.DictWriter(stream,fieldnames=HEADER)
        csvout.writeheader()
        csvout.writerows(rows)
    summary={
        "lab":"FakeRadio FM RF / genuine pinned Modem73 PHY test",
        "date":"2026-11-12",
        "input_wav":source.name,
        "input_sha256":hashlib.sha256(source.read_bytes()).hexdigest(),
        "pinned_upstream_modem73_revision":(MODEM/"upstream-revision.txt").read_text().strip(),
        "mode":"QPSK 1/2 short (mode16), 41 byte original application payload",
        "seeds":SEEDS,
        "total_tests":len(rows),
        "exact_decodes":sum(r["payload_decoded"]=="YES" for r in rows),
        "by_profile":table,
        "software_validation":"No physical soundcard/RF, not a validated propagation model",
        "not_measured":["KISS and ARQ","end-to-end Winlink email","auto-rate negotiation","FM radio hardware equivalence"],
    }
    (output/"summary.json").write_text(json.dumps(summary,indent=2)+"\n")
    readme="""# Actual Modem73 waveforms through FakeRadio's FM RF channel

Source: actual pinned upstream Modem73 QPSK 1/2 short-frame TX waveforms.
Each test is one real Modem73 frame encoded to 48-kHz mono audio, processed
through the software FM RF modulator/channel/demodulator, and decoded using
the actual pinned Modem73 RX implementation.
The WAV files are PCM16 audio suitable for listening or feeding an offline RX.

The channel parameters are engineering illustrations, NOT calibrated
over-the-air propagation estimates. Receiver SNR is not the channel's
configured complex-baseband SNR. This tests individual PHY payloads, not
KISS/ARQ, negotiation, or email transfer. A three-seed sample is not a
packet-error-rate estimate for a general deployment.

The 'busy' profile uses a synthetic competing FM transmitter, not another
real Modem73 transmitter and not a full multi-station collision topology.
No radios, virtual audio drivers, TCP listeners, or PTT are touched.
See summary.json and fm-modem73-results.csv.
"""
    (output/"README.md").write_text(readme)
    shutil.copy2(IONOS/"IONOS-LICENSE.txt",output/"IONOS-LICENSE.txt")
    bundle=output/"FakeRadio-FM-VHF-UHF-Real-Modem73-3Seed-Lab.zip"
    with zipfile.ZipFile(bundle,"w",zipfile.ZIP_DEFLATED,compresslevel=8) as z:
        for file in (path,output/"summary.json",output/"README.md",output/"IONOS-LICENSE.txt"):
            z.write(file,file.name)
        for wav in sorted(output.glob("seed_*/*/*.wav")):
            z.write(wav,str(wav.relative_to(output)))
    print(json.dumps({"result":"OK","archive":str(bundle),"bytes":bundle.stat().st_size,
       "sha256":hashlib.sha256(bundle.read_bytes()).hexdigest(),
       "summary":summary},indent=2),flush=True)

if __name__=="__main__":
    main()
