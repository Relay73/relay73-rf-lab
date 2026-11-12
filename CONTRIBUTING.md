# Contributing

PRs and issues are welcome for virtual-radio usability, reproducible channel
models, offline modem regression fixtures, and testing documentation.

1. Reproduce the problem from a CLEAN baseline.
2. Add or update deterministic self-tests before changing DSP behavior.
3. Capture the exact profile, random seed, frame size, modem version, and
   expected versus observed ACK result.
4. Run managed and FM tests; for native HF changes also run C++ and WAV tests.
5. Preserve published profile semantics or document new profiles explicitly.
6. Document channel model assumptions and avoid claiming calibrated physical
   RF or certified IONOS equivalence without independently measured evidence.

Do **not** submit real user recordings, Winlink passwords, private node IPs,
access tokens, machine credentials, or private RMS logs. The default test
flow must remain software-only and hardware-PTT-disarmed.
