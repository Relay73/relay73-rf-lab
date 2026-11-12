# Architecture

FakeRadio runs one Windows process per simulated station. Each reads TX audio
from a Windows WASAPI recording endpoint, converts it to 48-kHz mono float PCM,
and sends 240 samples (5 ms) per TCP frame to the remote FakeRadio process.
On arrival the receiver processes samples using exactly one selected channel
engine and writes the output into its local WASAPI playback device, where
the remote modem reads the impaired soundcard signal.

```text
 Modem TX → WASAPI capture → 5 ms PCM frame → TCP audio 19400
                                                    |
                                                    v
 Modem RX ← WASAPI playback ← selected RX impairment engine
                                    |
                        CLEAN / legacy audio
                        FM RF baseband TX→propagation→RX
                        IONOS-derived native C++ HF
```

Additional control/monitor endpoints: TCP 19401 (control), 19402 (TX
monitor), 19403 (RX monitor); default local control is loopback-only.
Transport TCP is unencrypted and does not enforce authentication. Do not
expose publicly.

The FM engine is managed C# and needs no native extension. The IONOS-derived
engine is an optional C++ shared library accessed via a stable C ABI. Only
its Gaussian FIR coefficients are redistributed from the ARSFI MIT source.
No Teensy/Arduino hardware is required.

The audio wire magic `VAFM` and control banner `VAFM-AUDIO-NODE` are preserved
for existing FakeRadio lab compatibility. This does not imply that FakeRadio
is official VARA software or compatible with its RF waveform protocol.

Modem73, AX.25 TNCs, and other soundcard modems remain **independent
applications**. FakeRadio cannot negotiate a modem rate; that belongs to
the modem/transport itself. For standalone, isolated tests do not attach a
real radio, PTT device or unattended gateway.
