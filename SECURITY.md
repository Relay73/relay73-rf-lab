# Security and lab safety

FakeRadio's audio transport TCP 19400 is unencrypted, unauthenticated and
intended only for trusted, isolated laboratory networks. Its control and
monitor endpoints are not an Internet-facing administrative API.

- Bind control/monitor to 127.0.0.1 when possible.
- Firewall audio traffic to the specific test peer; do not port-forward.
- Never connect a real transmitter or enable serial/PTT without a separately
  reviewed, attended hardware safety procedure.
- Do not run experiments against an active RMS gateway or Winlink production
  node by default.
- Captured WAV files can contain private messages and should be treated as
  sensitive data, not uploaded to GitHub.
- A decoded frame or application ACK does not prove delivered Winlink email.

For private vulnerability reports, open a private GitHub security advisory
in this repository if enabled. Do not paste credentials into public issues.
