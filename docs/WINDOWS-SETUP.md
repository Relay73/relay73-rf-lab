# Windows two-computer setup (virtual audio only)

## Requirements

- Two Windows x64 PCs or isolated Windows VMs on the same trusted LAN, each running FakeRadio.
- Two virtual audio cables **per PC**. VB-Audio Cable A/B are examples; FakeRadio does not distribute these drivers.
- .NET 10 SDK **only on the build computer** (self-contained ZIP includes runtime).
- Compatible soundcard modem (Modem73 is tested) installed on each host.
- Firewall allows TCP **19400 from the other test host only**; leave TCP 19401/19402/19403 loopback-only.

## Audio routing (do this on both machines)

| Application | Audio endpoint |
| --- | --- |
| Modem TX output / speaker | `CABLE-A Input` (playback) |
| FakeRadio capture / input | `CABLE-A Output` (recording) |
| FakeRadio playback / output | `CABLE-B Input` (playback) |
| Modem RX input / microphone | `CABLE-B Output` (recording) |

The Windows audio device labels may vary, but the *modem TX output → simulator capture* and *simulator playback → modem RX input* paths must remain separate.

1. Extract the ZIP into a **different directory** from any existing FakeRadio or gateway install.
2. Run `Show-Devices.cmd` to list device names.
3. Run `Start-FakeRadio.cmd` on each host. The first launch offers a setup wizard; enter capture/playback devices, the **other host IP**, and default ports 19400 (audio), 19401 (control), 19402/19403 (monitors).
4. For an **off-air virtual-only** lab choose no serial PTT port and do not attach Digirig/radios. The dummy/modem `--ptt none` mode is appropriate for this test.
5. Permit inbound TCP 19400 between the two private test PCs (Windows Defender Firewall); do not open it to the Internet.
6. Select `CLEAN` on both computers, then confirm the modem can transmit and receive in both directions.
7. Open `Change-Profile.cmd` or use the control port. Each simulator changes the audio **received locally**, so direction A→B and B→A may use different profiles.
8. Test `FM_RF_REFERENCE`, then move to `FM_VHF_MOBILE` or `FM_UHF_URBAN`. Record the random seed, profile, packet size, modem build, ACK time and retry count.

For unkeyed always-forward virtual audio, `GATE OFF` prevents the software from dropping PCM based on the modem's dummy PTT state. The FM PTT attack setting then applies at **audio stream startup** rather than every later burst; it is *not* a real PTT/key-up hardware test.

To reset setup, run `FakeRadio.exe --setup`. A `fakeradio.json` file is created beside the executable; keep it **local** and out of Git. If device selection fails, check 48 kHz shared-mode compatibility and disable Windows noise reduction/AGC, exclusive-use conflicts, and sound enhancements.

## Command-line quick reference

```powershell
.\FakeRadio.exe --list-devices
.\FakeRadio.exe --profiles
.\FakeRadio.exe --channel-self-test
.\FakeRadio.exe --setup
```

`FakeRadio.exe --help` shows every CLI override. Run in a terminal to view startup diagnostics. Ctrl+C stops the process.

**Security:** TCP 19400 is plain, unauthenticated audio. Never expose it or control port 19401 to the public Internet. Do not upload capture WAVs containing real traffic without checking their content.
