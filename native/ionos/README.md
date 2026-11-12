# FakeRadio portable IONOS-derived audio channel (experimental)

This is an independently structured, pure C++17 host implementation based on the **MIT-licensed ARSFI IONOS HFSimulator** project's DSP approach. It runs entirely on CPUs on Linux, Windows, or macOS; **no Teensy board is required**. It uses only the 128-tap Gaussian FIR data directly from the ARSFI firmware (see `IonosGaussianTaps.h`, `IONOS-LICENSE.txt`). The standalone host library itself uses no Arduino or Teensy dependencies.

Upstream: https://github.com/ARSFI/HFSimulator, file `src/HFSim_BFD_2_06/HFSim_BFD_2_06.ino` version 2.06 as inspected November 12, 2026.

**Important:** This is an *IONOS-derived statistical channel prototype*, **not a bit-for-bit port** of the Teensy implementation, a certified ITU channel emulator, an RF propagation forecast, or a validated equivalent to the IONOS hardware. It does not implement the original hardware's radio front end, SNR calibration, fixed-point gains, random-number sequence, busy detector, user interface, or timing quirks. A hardware comparison is required before making parity claims.

## Implemented

- WGN, MPG, MPM, MPP, and MPD modes. The latter four use the same profile pairs listed by IONOS:
  - MPG: 0.1 Hz Doppler spread, 0.5 ms path difference
  - MPM: 0.5 Hz, 1.0 ms
  - MPP: 1.0 Hz, 2.0 ms
  - MPD: 2.0 Hz, 4.0 ms
- 2-path and 4-path complex Gaussian fading coefficient generators.
- The IONOS 128-tap Gaussian FIR coefficients (each coefficient process updated at 64 times the preset spread).
- Audio quadrature via a windowed Hilbert FIR (129 taps, 64-sample group delay), variable-delay I/Q paths, interpolation between coefficient updates, coherent recombination.
- AWGN with rolling input-signal-power reference; configurable SNR and output clipping.
- Adjustable baseband audio frequency offset in the range -200 to +200 Hz.
- Seeded reproducibility within the same compiler/platform; audio output is invariant to packet chunking.
- Portable C ABI (`ionos_create/ionos_process/ionos_destroy`) plus a WAV processing CLI.
- Live FakeRadio integration through `IonosNativeProcessor.cs` with fail-closed native dependency checks.

## Build and test on Linux

```bash
make -C native/ionos all test
dotnet run --project samples/FakeRadio.Examples/FakeRadio.Examples.csproj -c Release -- --test-native
```

On macOS, `make` builds `libionos_channel.dylib`. A recent C++17 compiler is required. On Windows, build from the Visual Studio 2022 x64 Native Tools command prompt using `packaging/Build-Windows.ps1` or compile IonosChannel.cpp with `cl /std:c++17 /EHsc /O2 /MT /LD /Fe:ionos_channel.dll`.

## Offline WAV processing

From the source checkout:

```bash
dotnet run --project samples/FakeRadio.Examples -c Release -- --output /tmp/fakeradio-source --seconds 8
native/ionos/ionos-wav --input /tmp/fakeradio-source/clean.wav --output /tmp/ionos-mpm.wav --mode MPM --paths 2 --snr-db 15 --seed 2026
dotnet run --project samples/FakeRadio.Examples -c Release -- --ionos-examples --output /tmp/ionos-samples --seconds 8
```

The WAV CLI consumes and generates **16-bit mono PCM WAV**, with the audio sample rate taken from the file (8–192 kHz); no physical audio device is used. It can process real modem audio recorded through lawful normal operation.

## Live FakeRadio channel profiles

With the optional native library installed beside the Windows FakeRadio executable, select:

```text
PROFILE IONOS_WGN
PROFILE IONOS_MPG
PROFILE IONOS_MPM
PROFILE IONOS_MPP
PROFILE IONOS_MPD
SNR 15
IONOS PATHS 4
IONOS OFFSET 10
CHANNEL JSON
```

The library is initialized per receiver at 48 kHz. Selecting IONOS profiles without a compatible native library returns an error rather than silently substituting the legacy fading processor. The legacy `CLEAN` and other presets remain available and unchanged.

The engine is an audio-channel simulation and has **no way to key a real radio by itself**. FakeRadio's existing virtual-gate/physical-PTT safety rules still apply.

## Real Modem73 decoding lab (off-air, no radio)

With a separately downloaded Relay73 checkout containing the pinned, unchanged upstream Modem73 source:

```bash
python3 native/ionos/run_modem73_lab.py
# Or with an explicit native adapter/source location:
python3 native/ionos/run_modem73_lab.py --modem73-root /path/to/Relay73/native/modem73
```

The harness compiles the actual pinned Modem73 QPSK 1/2 short-frame encoder/receiver alongside the portable native IONOS channel, sends a 41-byte payload through 22 clean/noise/multipath seeded trials, checks exact decoded payloads, and records the modem's SNR and BER reports. It writes eight **actual Modem73** WAV bursts, a full 22-row CSV, a JSON summary, and a source-attributed ZIP under `artifacts/modem73-ionos-offair/`.

On November 12, 2026 the pinned revision `de7bfde4f5852c2c6bb8d14615f7b7fd040123c8` produced **17/22 exact payload matches**. At nominal 15 dB, two paths, and seeds 2026/2027/2028, the results were: WGN 3/3, MPG 3/3, MPM 3/3, MPP 2/3, MPD 1/3. Four paths, weak 5 dB noise, and +20 Hz offset make up the remaining trials. The channel's requested audio-domain SNR is **not** equivalent to the modem's reported post-demodulation SNR. Three seeds are not a calibrated packet-error-rate estimate.

This is a PHY frame decoding test, **not** native KISS/CSMA/ARQ or Pat/Winlink, not full-session goodput or negotiated-rate validation, and not an ITU-certified/IONOS hardware-equivalent model. There are no sound card, serial, network or RF actions.

## Test coverage and known gaps

Automated C++ tests cover identity pass-through, seeded reproducibility, chunk-size invariance, 2/4 path mode validity, noise SNR measurement, invalid configuration and C ABI. A portable .NET interoperability test exercises the native library from C#.

The current version does NOT independently verify Gaussian Doppler spectrum shape against physical IONOS, frequency-selective fading statistics against standards, BER/FER against Codec 2, or full Modem73/Winlink email performance. The first claim of **hardware equivalence or ITU-calibrated performance** must wait for those measurements. Random sampling uses the host C++ standard library (not the Teensy RNG), and the windowed Hilbert implementation and floating-point gain calibration deliberately differ from the original hardware implementation.

## Licensing

Original Gaussian FIR coefficients: MIT license, © 2020/2021 Amateur Radio Safety Foundation, Inc. The MIT license appears in `IONOS-LICENSE.txt`. The new code is separately authored for the FakeRadio project, with references to the upstream method and independently validated operating behavior.

No Teensy Audio, Arduino framework, TFT, EEPROM, or embedded board support code is incorporated. The standalone FakeRadio project retains the required ARSFI MIT notice.
