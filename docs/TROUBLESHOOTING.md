# Troubleshooting

**Can't start FakeRadio** — On Windows use `Show-Devices.cmd` or `FakeRadio.exe --list-devices` to find exact playback and recording endpoints. Confirm two virtual cable drivers are installed and no exclusive-mode program is holding either endpoint.

**No packets cross** — Test `CLEAN` first. On each machine, the modem TX playback must feed FakeRadio capture through Cable A, and FakeRadio playback must feed modem RX capture through Cable B. Confirm both peer IP addresses, and allow only peer-host TCP 19400 in Windows Firewall. Do not assume that a successful local KISS TCP connection means the remote modem decoded anything.

**PTT disabled, no audio** — For a completely off-air dummy-PTT test, `GATE OFF` lets audio flow despite PTT being off. A physically keyed setup requires an entirely separate attended safety and timing check.

**Modem decoder sees some data but no ACK** — Test return audio path. FakeRadio impairment applies **per receiving station**. Distinct forward and reverse channel seeds/settings can cause one-sided communication.

**Strange audio / poor decoder SNR** — Check Windows audio enhancements, AGC and virtual cable sample formats. Use clean baseline and `FM_RF_REFERENCE` before enabling shadowing, squelch or narrow audio filtering.

**IONOS profiles fail** — Full native Windows build must provide `ionos_channel.dll` beside `FakeRadio.exe`. FM-only developer candidate intentionally omits native HF library and fails closed.

**Need audio to analyze** — Use `RECORD START tag` / `RECORDRX START tag` and stop at the end. WAV captures can contain private payloads; keep them out of Git.

**Need a full Winlink delivery verdict** — The bundled regression tests do **not** run Pat, RMS Packet, a CMS session or user email. A KISS ACK is not Winlink delivery confirmation.
