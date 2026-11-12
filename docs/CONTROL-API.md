# TCP control reference

FakeRadio defaults to local control TCP `127.0.0.1:19401` with newline-terminated ASCII commands and a greeting beginning `OK VAFM-AUDIO-NODE`. The legacy greeting and `VAFM` audio frame magic are preserved intentionally for compatibility, even though the public source and project are called FakeRadio.

Use `Change-Profile.cmd` for a menu-based profile switch. For advanced use, connect with a local TCP terminal such as PowerShell or netcat, read the greeting, and send one command per line.

## Common commands

| Command | Effect |
| --- | --- |
| `STATUS` | Current audio channel, PTT/gate, FM parameters, seed and revision |
| `PROFILE CLEAN` | Reset to clean baseline |
| `PROFILE FM_VHF_MOBILE` | FM VHF mobile profile |
| `PROFILE FM_UHF_URBAN` | FM UHF urban multipath |
| `PROFILE IONOS_MPM` | Optional native HF mode (native DLL required) |
| `FM SPEED 65` | Synthetic mobile speed km/h |
| `FM SNR 38` | FM baseband signal to noise target in dB |
| `FM SNR OFF` | Disable FM thermal-noise injection |
| `FM SQUELCH -30` | FM threshold (dB relative to normalized signal power) |
| `FM PTT 25` | FM transmitter key-up delay in milliseconds |
| `FM SHADOW 12` | Slow shadowing depth |
| `FM INTERFERER -1` | Synthetic co-channel FM interferer level |
| `SEED 2026` | Change deterministic seed (channel becomes `CUSTOM`) |
| `CHANNEL JSON` | Return serialized channel parameter snapshot |
| `GATE OFF` | Forward virtual audio without PTT gating (virtual test only) |
| `IONOS PATHS 4` | Four native HF multipath components |
| `IONOS OFFSET 10` | Add native channel audio-frequency offset in Hz |
| `RECORD START demo` / `RECORD STOP` | Record local TX-side audio |
| `RECORDRX START demo` / `RECORDRX STOP` | Record post-impairment RX audio |

Commands return `OK` or an `ERR` response (or structured status text). New channel configuration is applied to subsequent samples without restarting the process. Each receive TCP session keeps its own channel state.

**Do not publish captures** without permission. The simulator may record user messages or sensitive real traffic during testing.

## Network safety

TCP 19400 (audio) is **unencrypted and unauthenticated**. The control, TX and RX monitor ports default to localhost, but an explicit remote-control CLI setting can open them to the LAN. Keep control bound to loopback and use a host firewall/private test network for audio. This protocol is not intended for Internet exposure.

## Known limitation

If the modem is run with dummy PTT and `GATE OFF`, FM PTT attack occurs at stream startup instead of each individual transmit burst. This is suitable for KISS throughput baselines but not for physical PTT-keying accuracy or squelch attack calibration. A future gated virtual-PTT integration should validate actual transitions independently.
