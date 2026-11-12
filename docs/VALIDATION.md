# What has actually been validated

| Layer | Evidence | Status |
| --- | --- | --- |
| Managed audio-channel code | 15 self-tests | Passed |
| FM RF model code | 6 self-tests including seeded 5-ms chunks, squelch, PTT and capture | Passed |
| Portable C++ HF channel | 5 native tests; 4 WAV tests | Passed |
| Mono WAV sample generation | Seven FM previews at 48 kHz | Passed |
| Upstream Modem73 offline FM frame decode | 18/21 exact 41-byte payloads (3 seeds × 7 profiles) | Mixed by design |
| Upstream Modem73 2.4.2 KISS, virtual audio, two Windows machines | 9/11 short/normal-frame two-packet sessions | 9 passed; 2 weak-UHF failures |
| Real-RF hardware propagation calibration | No measurements | Not done |
| Physical PTT key-up and TX/RX turnaround | Dummy PTT only | Not done |
| Modem73 automatic rate negotiation | Fixed QPSK 1/2 only | Not done |
| Full Winlink/Pat/RMS/B2F email delivery through this channel | No full email tests | Not done |

## Windows full-session findings

| Scenario | Frame size | App data ACKed | Time to final ACK | Retries |
| --- | --- | ---: | ---: | ---: |
| Reference FM | Short | 128 B | 23.49 s | 0 |
| VHF fixed | Short | 128 B | 19.56 s | 0 |
| VHF mobile | Short | 128 B | 18.47 s | 0 |
| UHF urban | Short | 128 B | 19.78 s | 0 |
| UHF edge (default seed) | Short | 128 B | 20.22 s | 0 |
| Shared-frequency interference | Short | 128 B | 26.02 s | 0 |
| Bad audio drive | Short | 128 B | 17.28 s | 0 |
| UHF edge, seed 2026 | Short | 0 B ACKed | 3 ACK timeouts | 2 additional attempts |
| UHF edge, seed 2027 | Short | 0 B ACKed | 3 ACK timeouts | 2 additional attempts |
| Reference FM | Normal | 908 B | 24.91 s | 0 |
| VHF mobile | Normal | 908 B | 26.82 s | 0 |

In the seed-2027 failure, the receiver logged duplicate reception of the first application packet and transmitted ACKs, but the sender did not receive them. This is an *end-to-end acknowledgment failure*, not proof that the forward Modem73 decoder always failed.

These runs are not enough to choose production rate, frame length or FEC automatically. The Windows virtual-audio lab uses real WASAPI, native Modem73 KISS, and dummy PTT; real RF/physical PTT behavior is outside the tested scope.

The original complete per-run machine logs and checksummed evidence remain in the private Relay73 development workspace and are **not redistributed by this public repository**.
