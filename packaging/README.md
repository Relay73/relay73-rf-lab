# FakeRadio — software-only HF/VHF/UHF modem audio lab

FakeRadio connects two soundcard modems across a LAN using their normal **audio** inputs and outputs. It is a virtual radio/audio channel, **not a transceiver, a digital modem, an RF transmitter, or a radio controller**. The first Windows release is a portable x64 executable; no Python or .NET installation is needed on the target PC when self-contained.

## 5-minute Windows setup (one copy on each of two PCs)

1. Install two independent virtual audio cable paths on **each PC** (for example VB-Audio Cable A and B, or equivalent software). FakeRadio doesn't install or redistribute a virtual cable driver.
2. Wire the modem and FakeRadio as follows **on each PC**:

   | Application | Windows sound device |
   | --- | --- |
   | Modem **TX / speaker** | **Cable A Input** (a playback endpoint) |
   | FakeRadio **TX capture** | **Cable A Output** (a recording endpoint) |
   | FakeRadio **RX playback** | **Cable B Input** (a playback endpoint) |
   | Modem **RX / microphone** | **Cable B Output** (a recording endpoint) |

   Disable automatic audio enhancements, AGC, and exclusive-use conflicts. Use 48 kHz or allow Windows to resample. The modem does not need to know that FakeRadio exists.
3. Extract the same ZIP on both PCs. Double-click **Start-FakeRadio.cmd**. On first launch it asks for the TX/RX devices, remote peer IP, ports and an initial channel preset, and writes `fakeradio.json` beside the executable.
4. On PC A enter PC B's IP as the peer; on PC B enter PC A's IP. Permit **TCP 19400 inbound** between these two machines in Windows Firewall (or your chosen audio port). Do not port-forward this service to the Internet.
5. Keep **local control only** at the default unless you run a trusted, isolated lab. Use the **Change-Profile.cmd** menu on either PC while the simulator is running. Each PC sets the impairment affecting **audio arriving at its local modem**, allowing asymmetric tests.

**Virtual-only safety:** Do not attach or enable real radios, serial hardware PTT, or gateway services during setup. For software-only baseline with always-forward PCM, answer **N** to the setup's TNC-controlled PTT question, and leave the serial port blank. For realistic transmit gating, choose external TNC-controlled PTT and provide the local PTT ON/OFF control flow; see `FakeRadio.exe --help`. A virtual PTT gate is not a physical transmitter interlock.

## Presets

| Profile | Intended illustration |
| --- | --- |
| `CLEAN` | Bit-exact signal path with no impairments |
| `VHF_MOBILE_FADING` | Mobile-level signal fades, noise |
| `VHF_URBAN_MULTIPATH` | Variable signal and one delayed echo |
| `UHF_WEAK_SIGNAL` | Weak and fading received signal |
| `UHF_INTERFERENCE_BURSTS` | Noise, impulse bursts, intermittent dropouts |
| `HF_STABLE` | Quiet ~250–2900 Hz audio passband |
| `HF_QSB_SLOW` | Slow, deep envelope fading (QSB) |
| `HF_SELECTIVE_FADING` | Changing direct/delayed-path ratio, audio cancellations |
| `HF_NOISY_BAND` | Low SNR plus impulsive interference |
| `HF_BURST_INTERFERENCE` | Intermittent burst noise and interruptions |
| `HF_MULTIPATH_1` | Legacy static delayed-path echo |
| `IONOS_WGN` | Native IONOS-derived white-noise channel |
| `IONOS_MPG` | IONOS-derived good: 0.1 Hz / 0.5 ms two-path fading |
| `IONOS_MPM` | IONOS-derived moderate: 0.5 Hz / 1 ms |
| `IONOS_MPP` | IONOS-derived poor: 1 Hz / 2 ms |
| `IONOS_MPD` | IONOS-derived disturbed: 2 Hz / 4 ms |
| `FM_RF_REFERENCE` | Software FM modulate/demodulate reference, negligible propagation damage |
| `FM_VHF_FIXED` | 2m fixed station, LOS-dominant microsecond reflections and FM radio key-up |
| `FM_VHF_MOBILE` | 2m mobile, seeded Doppler/Rician fading, shadowing and squelch |
| `FM_UHF_URBAN` | 70cm mobile, Rayleigh fading with short urban reflections |
| `FM_UHF_EDGE` | 70cm weak coverage with shadowing, RF noise and squelch loss |
| `FM_BUSY_SHARED` | Overlapping synthetic co-channel FM transmitter/capture scenario |
| `FM_BAD_AUDIO` | Overdriven/filter-limited transmitter audio and PTT startup delay |
| `FM_AWGN_12DB`, `FM_LOW_GAIN`, `FM_CLIP`, `FM_CLOCK_100PPM` | Regression/stress profiles |

The `FM_*` RF profiles are a separate **software FM signal model**, not simple post-demodulation audio noise. They oversample 48 kHz modem audio to 384 kHz complex baseband, apply microsecond path delays and seeded Doppler fading, then perform FM limiting/demodulation and receiver squelch. They are experimental and **not calibrated against physical FM radios**; the busy profile contains a synthetic competing FM carrier rather than a real independently transmitted Modem73 station. The legacy HF/VHF/UHF presets are **illustrative deterministic audio-domain impairments**. The new optional `IONOS_*` presets instead use a separate IONOS-derived native engine with Gaussian Doppler filtering, independently varying complex paths and delay. These are **not yet ITU/Watterson calibrated or verified against actual IONOS hardware**, and they do not predict real link success rates. See `native/ionos/README.md` in the source checkout. HF and VHF/UHF require different modem TX/RX audio settings; FakeRadio does not change modem bandwidth or on-air legality.

## Controls and audio capture

With the simulator running, double-click `Change-Profile.cmd`, or connect on localhost TCP **19401** using a TCP terminal to send ASCII lines:

```text
STATUS
PROFILE HF_QSB_SLOW
PROFILE IONOS_MPM
PROFILE FM_VHF_MOBILE
FM SPEED 65
FM SNR 38
FM SQUELCH -30
FM PTT 25
FM SHADOW 12
PROFILE FM_UHF_URBAN
IONOS PATHS 4
IONOS OFFSET 10
SNR 15
FADE 20 0.4
FADE OFF
CHANNEL JSON
RECORDRX START hf-test
RECORDRX STOP
```

`RECORDRX` writes received audio WAV files for the currently selected channel. `RECORD START` captures the local transmit-side audio. Generated WAV captures and accompanying channel metadata appear in the executable directory. Treat captured user audio and message payloads as private.

**Network caution:** Audio port 19400 uses unencrypted TCP, without authentication. Do not use it across the public Internet. Control port 19401 and monitor ports 19402/19403 default to **localhost-only** for new configurations. `--control-remote` makes them reachable on the LAN and should only be used on isolated/test networks with restrictive host firewall rules. Existing older `fakeradio.json` configurations preserve their legacy remote control listener until reconfigured.

## Portable audio examples (works on Linux/macOS/Windows)

From the **source checkout**, with .NET 10 SDK installed:

```bash
dotnet run --project samples/FakeRadio.Examples/FakeRadio.Examples.csproj -c Release -- --test
dotnet run --project samples/FakeRadio.Examples/FakeRadio.Examples.csproj -c Release -- --output examples/audio --seconds 8
```

This produces nine 48 kHz 16-bit mono `.wav` files and `manifest.json`. For the optional IONOS-derived engine (after compiling its native library):

```bash
make -C native/ionos all test
dotnet run --project samples/FakeRadio.Examples -c Release -- --test-native
dotnet run --project samples/FakeRadio.Examples -c Release -- --ionos-examples --output examples/ionos --seconds 8
dotnet run --project samples/FakeRadio.Examples -c Release -- --test-fm
dotnet run --project samples/FakeRadio.Examples -c Release -- --fm-examples --output examples/fm --seconds 8
python3 native/ionos/run_fm_modem73_lab.py
```

The last command uses the pinned, unmodified Modem73 source in the separately maintained Relay73 checkout to test actual Modem73 PCM through every FM model. It creates 21 WAVs, 21 rows of exact-payload results, and a reproducible ZIP in `artifacts/fakeradio-fm-modem73-matrix`. It does **not** test Winlink/Pat/KISS/ARQ or transmit over RF. Each file uses the **same C# channel DSP source** as FakeRadio's Windows bridge, with fixed seeded, synthetic modem-like tones. They are not real Modem73, VARA, amateur voice, or OTA recordings. Changing `--seconds` from 2 to 30 can make QSB more audible.

## Windows distribution build

From a source checkout with the .NET 10 SDK and Visual Studio 2022 C++ Build Tools, run from an **x64 Native Tools Command Prompt** on Windows (PowerShell 7):

```powershell
pwsh -File packaging/Build-Windows.ps1
```

Or from Linux with Python 3, the .NET 10 SDK, GCC/Make and **MinGW-w64 x64 cross-compiler** (the build refuses to ship the IONOS-enabled program without its matching Windows DLL):

```bash
python3 packaging/build.py
```

Result: `artifacts/FakeRadio-Windows-x64.zip`. It includes the self-contained executable, `ionos_channel.dll`, the offline `ionos-wav.exe` program, MIT license notice, setup/menu scripts, nine legacy WAV examples, and five IONOS-derived WAV examples, and seven FM RF previews. Before a public release, confirm all relevant third-party redistribution licenses, complete third-party dependency/license review, malware-signing/SmartScreen expectations and conduct actual Windows audio-loopback testing. **Source builds are provided; verify prerequisites and use the GitHub Releases page for any future signed distributions.**

## Troubleshooting

- **No received audio:** Check both cable directions, the peer address, Windows inbound TCP 19400 rules, and whether the modem is actually generating TX samples. Do not connect FakeRadio TX capture to the modem's receive cable.
- **Wrong audio selection:** Run `FakeRadio.exe --list-devices`, then double-click Start-FakeRadio after using `FakeRadio.exe --setup` to replace your config.
- **Modem starts transmitting but no audio arrives:** Verify external-PTT gating; begin with always-forward audio in a fully software-only lab.
- **Wrong remote control / port error:** Standard new setups listen on `127.0.0.1:19401`; run the menu on the same computer as FakeRadio, not the peer.
- **Hard to hear QSB:** Use longer (8–15 second) synthetic test samples; the peak-to-trough depth repeats at the preset fade rate.
- **Two instances on one computer:** Give each instance its own executable/config directory, virtual cable pair, audio port, control port, and monitor ports. The interactive defaults are for one instance per machine.
