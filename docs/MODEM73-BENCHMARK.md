# Modem73 test recipes (off-air only)

FakeRadio is a channel layer, not the modem. It can host an unmodified Modem73 binary as a *normal soundcard application*, without source-level integration.

**Live virtual two-PC test:** Use the Windows setup in [Windows setup](WINDOWS-SETUP.md), run Modem73 on each PC with `--ptt none` and virtual audio Cable A/B, and connect a KISS client to its local port. Use a test application that verifies a hash/ACK per KISS payload, then run CLEAN, FM_RF_REFERENCE, FM_VHF_MOBILE and FM_UHF_EDGE, recording forward and reverse delivery, duplicate frames, ACK timings, timeouts, and frame size.

**Optional offline tests:** The scripts under `tests/modem73/` compile true pinned upstream Modem73 encoder/decoder code from an existing Relay73 checkout, requiring a local C++17 toolchain and .NET 10 SDK. Source code is not copied into this repository.

```bash
export MODEM73_ROOT=/path/to/Relay73/native/modem73
python3 tests/modem73/run_modem73_lab.py --modem73-root "$MODEM73_ROOT"
python3 tests/modem73/run_fm_modem73_lab.py
```

These exercises send a known short QPSK 1/2 frame through the HF or FM engine and verify exact decoder payload recovery. They are not Winlink B2F, ARQ negotiations or complete sessions. The source decoder adapter is pinned by the Relay73 project; if its location or API changes, the optional harness may need maintenance.

## Prior engineering evidence (November 2026)

One short-frame FM model matrix recovered 18/21 exact 41-byte Modem73 payloads across seven profiles and three seeds. In a separate two-Windows-PC virtual-audio lab, native Modem73 2.4.2 KISS runs completed 9/11 two-packet sessions. Two seeded UHF coverage-edge sessions timed out at the application ACK stage, including one case where the receiving modem recovered data but the reply ACK path failed.

Normal frames delivered **908 acknowledged bytes in 24.905 s** (291.7 bit/s) on the FM reference channel versus short frames' **128 bytes in 23.489 s** (43.6 bit/s). These are small engineering trials, not statistically significant throughput or error-rate curves.

The public repository intentionally excludes machine identifiers, private endpoint settings, actual user messages, original lab host logs, and any real RMS/CMS credentials.
