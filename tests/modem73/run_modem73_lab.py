#!/usr/bin/env python3
"""Real Modem73 PHY -> IONOS-derived audio -> real Modem73 RX, all off-air.

Uses the existing pinned, unchanged Relay73 upstream Modem73 source tree for
the genuine encoder and receiver, and the separately maintained FakeRadio
native channel. Does not open audio devices, sockets, PTT, or radio hardware.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path
import os
import re
import shutil
import subprocess
import sys
import wave
import zipfile

HERE = Path(__file__).resolve().parent
IONOS = HERE.parents[1] / 'native' / 'ionos'
DEFAULT_MODEM73 = Path(os.environ.get('MODEM73_ROOT', '../Relay73/native/modem73'))
DEFAULT_OUTPUT = HERE.parents[1] / "artifacts" / "modem73-ionos-offair"

COLUMNS = [
    "channel", "paths", "nominal_snr_db", "seed", "offset_hz",
    "payload_match", "receiver_callbacks", "rx_frame_counter",
    "output_rms", "decoded_snr_db", "decoded_ber_percent",
]
CHANNELS = {"CLEAN", "WGN", "MPG", "MPM", "MPP", "MPD"}


def run(*command: str) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(command, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, check=False)
    if result.returncode:
        raise RuntimeError(f"Command exited {result.returncode}: {' '.join(command)}\n{result.stdout[-3000:]}")
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--modem73-root", type=Path, default=DEFAULT_MODEM73)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    modem = args.modem73_root.resolve()
    src = modem / "upstream"
    for file in ("modem.hh", "phy/common.hh", "deps/aicodix/dsp", "deps/aicodix/code"):
        if not (src / file).exists():
            raise RuntimeError(f"Pinned Modem73 source is missing: {file}")
    if not (modem / "relay73_modem73_rx.cpp").is_file():
        raise RuntimeError("Relay73 native Modem73 RX adapter not found")

    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    binary = output / "modem73-ionos-offair-lab"
    compile_args = [
        "g++", "-std=c++17", "-O1", "-DNDEBUG",
        *[f"-I{p}" for p in (src, src / "phy", src / "deps/aicodix/dsp",
                            src / "deps/aicodix/code", src / "deps/aicodix/modem", modem, HERE, IONOS)],
        str(HERE / "modem73_offair_lab.cpp"),
        str(IONOS / "IonosChannel.cpp"),
        "-o", str(binary),
    ]
    compiled = run(*compile_args)
    if compiled.stdout:
        print("Compiler:", compiled.stdout[-1500:])

    prefix = output / "modem73_"
    result = run(str(binary), str(prefix))
    rows: list[dict[str, str]] = []
    for line in result.stdout.splitlines():
        fields = next(csv.reader([line]))
        if len(fields) != len(COLUMNS) or fields[0] not in CHANNELS:
            continue
        rows.append(dict(zip(COLUMNS, fields)))
    if len(rows) != 22:
        raise RuntimeError(f"Unexpected number of real Modem73 cases: {len(rows)}")
    clean = next(r for r in rows if r["channel"] == "CLEAN")
    if clean["payload_match"] != "PASS":
        raise RuntimeError("Clean real Modem73 baseline failed; refusing result package")

    samples = []
    for wav in sorted(output.glob("modem73_*.wav")):
        with wave.open(str(wav), "rb") as recording:
            if recording.getframerate() != 48000 or recording.getnchannels() != 1                     or recording.getsampwidth() != 2:
                raise RuntimeError(f"Unexpected audio format: {wav}")
            count = recording.getnframes()
        samples.append({"file": wav.name, "samples": count,
                        "seconds": round(count/48000, 5),
                        "sha256": hashlib.sha256(wav.read_bytes()).hexdigest()})

    csv_path = output / "modem73-ionos-22-trials.csv"
    with csv_path.open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=COLUMNS)
        writer.writeheader()
        writer.writerows(rows)

    by_condition = []
    for mode in ("WGN", "MPG", "MPM", "MPP", "MPD"):
        subset = [r for r in rows if r["channel"] == mode and r["paths"] == "2"
                  and r["nominal_snr_db"] == "15.0" and r["offset_hz"] == "0.0"]
        by_condition.append({"mode":mode, "pass":sum(r["payload_match"]=="PASS" for r in subset),
                             "trials":len(subset)})
    summary = {
        "date": "2026-11-12",
        "reference": "real unmodified pinned Modem73 Encoder48k -> FakeRadio IONOS-derived channel -> real Modem73 RX C ABI",
        "upstream_revision": (modem / "upstream-revision.txt").read_text().strip(),
        "modulation": "QPSK 1/2 short frame (mode 16)",
        "payload_bytes": 41,
        "scenarios": len(rows),
        "passes": sum(r["payload_match"]=="PASS" for r in rows),
        "failures": sum(r["payload_match"]!="PASS" for r in rows),
        "standard_15db_two_path_results": by_condition,
        "sample_wavs": samples,
        "not_measured": ["KISS retransmissions", "auto-rate negotiation",
                          "ARQ goodput", "Winlink email delivery", "hardware IONOS parity"],
        "safety": "No soundcards, serial PTT, RF, gateway connection, or network sockets",
    }
    (output / "summary.json").write_text(json.dumps(summary, indent=2)+"\n")
    readme = """# Genuine Modem73 transmit waveforms over experimental IONOS-derived channel

This package was generated by the **actual pinned Modem73 Encoder48k**, passed
through the native FakeRadio IONOS-derived channel, then fed into the actual
Modem73 receive decoder. These are NOT synthetic three-tone demo signals.
No RF or audio devices were involved.

The WAVs are 48000 Hz, mono, PCM16. They contain a single ~1.09 second
Modem73 QPSK 1/2 short-frame burst each. For fading to be audible at slow
MPG rates, longer repeated modem bursts are needed; these clips instead
correspond to the specific one-frame PHY decoding experiments.

See modem73-ionos-22-trials.csv and summary.json. Every CSV 'PASS' means
the exact 41-byte payload was decoded by Modem73; 'FAIL' means no correct
payload callback. Some FEC decoder internal retries occurred, but no RF
retransmissions/ARQ were simulated.

These channel presets are experimental and not calibrated against a real IONOS
board or ITU fading channel. The requested nominal channel SNR is not the
decoder-reported SNR. No Winlink session or CMS delivery was exercised.

Original IONOS Gaussian FIR coefficients: Copyright 2020/2021 ARSFI, MIT.
"""
    (output / "README.md").write_text(readme)
    shutil.copy2(IONOS / "IONOS-LICENSE.txt", output / "IONOS-LICENSE.txt")
    archive = output / "Real-Modem73-IONOS-22-Trial-Audio-And-Results.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=7) as z:
        for path in sorted(output.glob("modem73_*.wav")):
            z.write(path, arcname=path.name)
        for file in (csv_path, output / "summary.json", output / "README.md",
                     output / "IONOS-LICENSE.txt"):
            z.write(file, arcname=file.name)
    print(json.dumps({
        "result": "PASS", "scenario_count": len(rows),
        "passes": summary["passes"], "failures": summary["failures"],
        "by_condition": by_condition, "wav_count": len(samples),
        "archive": str(archive), "bytes": archive.stat().st_size,
        "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
    }, indent=2))


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"MOD73_IONOS_OFFAIR_RUN_FAIL {exc}", file=sys.stderr)
        raise SystemExit(1)
