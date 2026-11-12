#!/usr/bin/env python3
"""End-to-end WAV CLI regression tests: no dependencies beyond Python 3."""
from __future__ import annotations

import hashlib
from pathlib import Path
import math
import struct
import subprocess
import sys
import tempfile
import wave

ROOT = Path(__file__).resolve().parent
CLI = ROOT / "ionos-wav"


def run(*args: str, success: bool = True):
    p = subprocess.run([str(CLI), *map(str, args)], capture_output=True, text=True)
    if (p.returncode == 0) != success:
        raise AssertionError(f"Unexpected exit={p.returncode} stderr={p.stderr} stdout={p.stdout}")
    return p


def read_wav(path: Path):
    with wave.open(str(path), "rb") as stream:
        if stream.getframerate() != 48000 or stream.getnchannels() != 1 or stream.getsampwidth() != 2:
            raise AssertionError("Wrong WAV sample format")
        samples = list(struct.unpack("<" + "h" * stream.getnframes(),
                                     stream.readframes(stream.getnframes())))
    return samples


def main():
    with tempfile.TemporaryDirectory(prefix="ionos-wav-") as tmp:
        path = Path(tmp)
        src = path / "input.wav"
        rate = 48000
        with wave.open(str(src), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(rate)
            # Two unrelated tones exercise frequency-selective multipath.
            blocks = bytearray()
            for i in range(rate * 3):
                t = i / rate
                sample = .16 * math.sin(2 * math.pi * 1100 * t) + .10 * math.sin(2 * math.pi * 1760 * t)
                blocks += struct.pack("<h", int(sample * 32767))
            w.writeframes(blocks)

        original = read_wav(src)
        clean = path / "clean.wav"
        run("--input", src, "--output", clean, "--mode", "CLEAN")
        if read_wav(clean) != original:
            raise AssertionError("Clean WAV roundtrip changed PCM16 values")
        print("WAV_TEST clean pass")

        a, b, c = [path / f"{name}.wav" for name in ("a", "b", "c")]
        for target, seed in ((a, 2026), (b, 2026), (c, 2027)):
            run("--input", src, "--output", target, "--mode", "MPM",
                "--paths", "4", "--snr-db", "12", "--seed", str(seed), "--offset-hz", "4")
        da, db, dc = [hashlib.sha256(p.read_bytes()).hexdigest() for p in (a, b, c)]
        if da != db or da == dc:
            raise AssertionError("Seeded WAV files not deterministic / distinguishable")
        if len(read_wav(a)) != len(original) or read_wav(a) == original:
            raise AssertionError("IONOS WAV was not transformed or has wrong length")
        print("WAV_TEST seeded_multipath pass")

        wgn = path / "wgn.wav"
        run("--input", src, "--output", wgn, "--mode", "WGN", "--snr-db", "12")
        noisy = read_wav(wgn)
        start = rate
        signal_power = sum(x*x for x in original[start:])
        noise_power = sum((x-y)**2 for x, y in zip(original[start:], noisy[start:]))
        measured = 10 * math.log10(signal_power / noise_power)
        if not (11.5 < measured < 12.5):
            raise AssertionError(f"WAV pipeline SNR {measured:.3f} outside tolerance")
        print(f"WAV_TEST calibrated_snr pass measured={measured:.3f}dB")

        run("--input", src, "--output", path / "invalid.wav", "--mode", "INVALID", success=False)
        run("--input", src, "--output", src, "--mode", "MPM", success=False)
        print("WAV_TEST invalid_arguments pass")
        print("IONOS_WAV_TEST_PASS 4/4")


if __name__ == "__main__":
    main()
