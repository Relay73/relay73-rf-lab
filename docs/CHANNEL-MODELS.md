# Channel models and profiles

FakeRadio has **three distinct impairment engines**: fast audio-domain effects, experimental FM RF baseband processing, and experimental IONOS-derived HF audio channel processing. They are not interchangeable and are not regulatory, propagation or equipment-calibration models.

## FM VHF/UHF engine

Select one of these presets on **the receiver side**:

| Profile | Behavior |
| --- | --- |
| `FM_RF_REFERENCE` | Very strong software FM reference, near-ideal limiting/discriminator |
| `FM_VHF_FIXED` | 145 MHz reference, LOS Rician multipath, two paths, minimal fading |
| `FM_VHF_MOBILE` | 145 MHz, 50 km/h, Rician fading, 12 dB illustrative shadow depth |
| `FM_UHF_URBAN` | 435 MHz, 30 km/h, Rayleigh fading, ~0.6/2.2 µs reflections |
| `FM_UHF_EDGE` | Low-margin 435 MHz weak-signal case, deep shadowing and squelch loss |
| `FM_BUSY_SHARED` | Synthetic co-channel FM interferer to exercise receiver capture |
| `FM_BAD_AUDIO` | Overdrive, voice-frequency filtering, wrong deviation and startup delay |

The FM model converts 48 kHz PCM audio to 384 kHz complex baseband, applies short fractional-sample multipath delays, seeded Rayleigh/Rician Doppler fading, slow shadowing and RF thermal noise, then an idealized FM limiter and discriminator, low-pass receiver filtering and squelch. This is a **simplified, experimental** radio-link model, not I/Q emulation of any specific radio, IF passband or discriminator.

The `BUSY` profile uses a second synthesized tone-modulated FM carrier. It does **not** contain a third live Modem73 station and cannot faithfully reproduce real hidden-node CSMA/collisions.

## Control parameters

```text
PROFILE FM_UHF_URBAN
FM SPEED 30
FM SNR 38
FM SQUELCH -32
FM PTT 30
FM SHADOW 8
FM INTERFERER -120
SEED 2026
STATUS
```

- `FM SPEED`: mobile speed in km/h (0–250).
- `FM SNR`: approximate pre-fading complex-carrier to thermal-noise ratio in dB (not reported modem decoder SNR).
- `FM SQUELCH`: desired receiver squelch threshold relative to normalized channel power, dB.
- `FM PTT`: simulated transmitter startup delay in milliseconds (only applies to actual PTT transitions or first audio TCP stream start in GATE OFF mode).
- `FM SHADOW`: maximum synthetic slow shadow fade in dB.
- `FM INTERFERER`: relative amplitude of a synthetic co-channel FM carrier; `-120` disables it in ordinary configurations.
- `SEED`: deterministic random seed. It changes the channel revision and profile label to `CUSTOM`, retaining the FM mode.

Both peers should use `CLEAN` for baseline first, then test one impaired direction and then both. Different profiles or seeds on two receivers give asymmetric duplex conditions. One successful frame does **not** establish real link reliability.

## HF presets

Legacy deterministic audio profiles include `HF_STABLE`, `HF_QSB_SLOW`, `HF_SELECTIVE_FADING`, `HF_NOISY_BAND` and `HF_BURST_INTERFERENCE`. These apply effects *after* idealized demodulation.

The optional `IONOS_*` engine has WGN/MPG/MPM/MPP/MPD (0 to 2 Hz nominal spread, 0 to 4 ms multipath delay), with 2 or 4 fading paths. It needs a native `ionos_channel.dll` next to FakeRadio.exe (Windows) or `libionos_channel.so`/`.dylib` for portable example processing. **Missing library fails closed**. ARSFI FIR coefficients are MIT licensed, but the new host engine is **not bit-identical to hardware IONOS**, nor calibrated to ITU channel requirements.

Neither HF channel model should be reused as a VHF/UHF propagation approximation: HF millisecond multipath is not terrestrial FM microsecond multipath.
