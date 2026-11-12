# Third-party notices and attribution

- **ARSFI HFSimulator (IONOS)**: this project includes the 128-tap Gaussian
  FIR coefficient data derived from the upstream firmware file
  `src/HFSim_BFD_2_06/HFSim_BFD_2_06.ino` (v2.06).
  © 2020/2021 Amateur Radio Safety Foundation, Inc., **MIT license**.
  See `native/ionos/IONOS-LICENSE.txt` and
  https://github.com/ARSFI/HFSimulator.
- **NAudio**: managed Windows WASAPI/audio APIs, obtained separately as a
  NuGet dependency. Review its license on the upstream project and NuGet.
- **System.IO.Ports**: Microsoft .NET NuGet runtime package; review the
  package license as part of distribution preparation.
- **Modem73**: not bundled or forked here. Optional offline tests reference
  a separately obtained upstream/Relay73 checkout; user-supplied binaries
  and source must follow their own distribution licenses.

Do not conflate this separately authored experimental software channel with
the original IONOS hardware, ARSFI's official simulator or any proprietary
amateur-radio modem. The use of HFSimulator Gaussian taps does not establish
equivalence with the firmware or hardware.
