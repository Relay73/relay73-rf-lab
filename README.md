# FakeRadio · Relay73 RF Lab

**Software-only HF, VHF and UHF channel simulation for soundcard digital modems.**

FakeRadio bridges two Windows computers' virtual audio devices across a trusted LAN and modifies each received audio stream to emulate selected propagation and radio-configuration problems. It does **not** transmit RF, decode Winlink messages, replace Modem73 or operate a radio.

The FM RF engine has been exercised in a two-Windows-VM virtual-audio lab with real upstream Modem73 KISS transfers. This is **experimental test software**, not a calibrated channel simulator or a production gateway component.

## Get started

**Requirements:** Windows 10/11 x64 (the live bridge uses WASAPI), two independent virtual audio cable pairs per machine (for example VB-Audio Cable A/B), a trusted private LAN, and a compatible soundcard modem such as Modem73. To build from source, install the **.NET 10 SDK**. A self-contained Windows build does not require the .NET runtime to be preinstalled on the target machine.

1. Build the Windows program from source using the instructions in [Building](docs/BUILD-AND-TEST.md). The FM-only package is easiest to build from Linux; the optional IONOS-enabled package needs a matching C++ Windows DLL.
2. Extract the generated ZIP into its own folder on **both** Windows PCs.
3. Connect virtual cables as described in [Windows setup](docs/WINDOWS-SETUP.md); select audio devices and the **other computer's LAN IP** on each machine.
4. Run `Start-FakeRadio.cmd`, complete setup, and verify with `FakeRadio.exe --list-devices`.
5. Set `PROFILE CLEAN` to establish a baseline. Then try `FM_VHF_MOBILE`, `FM_UHF_URBAN`, `FM_UHF_EDGE`, or an HF preset through `Change-Profile.cmd`.

```text
 Modem A TX → Cable A → FakeRadio A == private TCP 19400 == FakeRadio B → Cable B → Modem B RX
 Modem A RX ← Cable B ← FakeRadio A == private TCP 19400 == FakeRadio B ← Cable A ← Modem B TX
```

**Start entirely virtual.** Keep serial PTT disconnected, do not connect a physical radio, and do not use this software as a safety interlock for real RF. For always-forward virtual audio use GATE OFF with no real PTT. This simplifies testing but does **not** reproduce every physical key-up delay.

## Channel families

| Engine | Profiles | Primary use |
| --- | --- | --- |
| Clean | `CLEAN` | A/B baseline, confirm routing |
| FM RF (VHF/UHF) | `FM_RF_REFERENCE`, `FM_VHF_FIXED`, `FM_VHF_MOBILE`, `FM_UHF_URBAN`, `FM_UHF_EDGE`, `FM_BUSY_SHARED`, `FM_BAD_AUDIO` | Experimental FM modulation, short-delay multipath, Doppler, shadowing, RF noise, squelch and co-channel capture |
| Legacy audio | `VHF_MOBILE_FADING`, `UHF_WEAK_SIGNAL`, `HF_QSB_SLOW`, `HF_NOISY_BAND` and others | Lightweight deterministic post-demodulation impairments |
| Native IONOS-derived HF | `IONOS_WGN`, `IONOS_MPG`, `IONOS_MPM`, `IONOS_MPP`, `IONOS_MPD` | Experimental HF fading with Gaussian Doppler filtering; needs optional `ionos_channel.dll` |

The IONOS-derived mode is based on a separately implemented portable C++ channel and **MIT-licensed Gaussian FIR coefficients** from [ARSFI HFSimulator](https://github.com/ARSFI/HFSimulator). It is **not** hardware-identical or calibrated to IONOS. FM propagation is also not calibrated against any particular radio.

## Control examples

With FakeRadio running, execute `Change-Profile.cmd` or connect to the **local** ASCII control port (default `127.0.0.1:19401`):

```text
STATUS
PROFILE FM_VHF_MOBILE
FM SPEED 65
FM SNR 38
FM SHADOW 12
FM SQUELCH -30
PROFILE FM_UHF_URBAN
PROFILE CLEAN
```

[Control protocol](docs/CONTROL-API.md) · [FM and HF models](docs/CHANNEL-MODELS.md) · [Windows setup](docs/WINDOWS-SETUP.md) · [Troubleshooting](docs/TROUBLESHOOTING.md)

## Build and test quickly

```bash
dotnet run --project samples/FakeRadio.Examples -c Release -- --test
dotnet run --project samples/FakeRadio.Examples -c Release -- --test-fm
dotnet build src/FakeRadio.AudioNode/FakeRadio.AudioNode.csproj -c Release
dotnet run --project samples/FakeRadio.Examples -c Release -- --fm-examples --output examples/fm --seconds 8
```

To build the native IONOS engine on Linux/macOS, run `make -C native/ionos all test` and `python3 native/ionos/check_wav.py`. To build the Windows FM-only package from Linux or macOS with the .NET SDK: `python3 packaging/build-fm-windows.py`. The self-contained Windows ZIP appears under `artifacts/`.

The standalone program uses **48 kHz mono audio** and **5 ms TCP audio frames**. The audio port is **unencrypted and unauthenticated**; keep both endpoints on a trusted LAN/VPN, restrict firewall access, and do not expose any control or monitor ports publicly.

## Validation status

- Managed channel regression tests: **15/15 passed**.
- FM RF regression tests: **6/6 passed**.
- Native C++ IONOS engine: **5/5 passed**, WAV CLI **4/4 passed**.
- Real Modem73 individual PHY decodes through FM simulation: **18/21** exact 41-byte packets.
- Two Windows hosts, upstream Modem73 2.4.2 KISS over virtual soundcards: **9/11 sessions passed**. Two weak-UHF seeded cases timed out at the application ACK stage; both hosts cleaned up without touching physical PTT.
- This **does not** verify auto-rate negotiation, complete Winlink email delivery, field packet error rates or actual FM/IONOS hardware equivalence.

See [Validation](docs/VALIDATION.md), [Modem73 off-air benchmarks](docs/MODEM73-BENCHMARK.md), and [Build and test](docs/BUILD-AND-TEST.md). Results are from small engineering samples and should not be treated as production reliability estimates.

## Project structure

- `src/FakeRadio.AudioNode/` — Windows WASAPI audio bridge and three channel engines.
- `native/ionos/` — optional MIT-licensed portable C++ HF engine and WAV processor.
- `samples/FakeRadio.Examples/` — waveform previews and channel self-tests.
- `tests/modem73/` — optional offline Modem73 PHY harness; upstream source is supplied separately.
- `packaging/` — self-contained Windows ZIP builders, menu and setup scripts.
- `docs/` — setup, channel and control reference, limitations, results.

## Licensing and safety

FakeRadio's separately authored code is distributed under [MIT](LICENSE). The IONOS Gaussian FIR coefficient data retains the original ARSFI [MIT license](native/ionos/IONOS-LICENSE.txt). Refer to [third-party notices](THIRD-PARTY-NOTICES.md).

Never commit recordings with private message contents, station passwords, API tokens or lab-specific network configuration. An application ACK is not proof of Winlink/CMS delivery. This repository is for **virtual/off-air testing by default**, not unattended transmission or deployment to an RMS gateway.

Related: [Relay73](https://github.com/Relay73/Relay73) · [Gateway](https://github.com/Relay73/relay73-gateway) · [Modem adapters](https://github.com/Relay73/relay73-modem-adapters).
