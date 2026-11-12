# Build, run tests and create Windows packages

## Portable prerequisites

- .NET **10 SDK** for managed code and offline samples.
- C++17 compiler and Make (Linux/macOS) for the optional native IONOS engine.
- Python 3 for the build and WAV test scripts.
- Windows 10/11 x64 for the **live audio bridge** (NAudio/WASAPI). Windows .NET targeting packs can be built on Linux/macOS, but this does not test audio hardware.

## Source-only build and tests

```bash
dotnet build src/FakeRadio.AudioNode/FakeRadio.AudioNode.csproj -c Release
dotnet run --project samples/FakeRadio.Examples -c Release -- --test
dotnet run --project samples/FakeRadio.Examples -c Release -- --test-fm
dotnet run --project samples/FakeRadio.Examples -c Release -- --fm-examples --output examples/fm --seconds 8
```

To test native HF on Linux/macOS:

```bash
make -C native/ionos all test
python3 native/ionos/check_wav.py
dotnet run --project samples/FakeRadio.Examples -c Release -- --test-native
dotnet run --project samples/FakeRadio.Examples -c Release -- --ionos-examples --output examples/ionos --seconds 8
```

## Windows self-contained FM-only package

On Linux with a .NET 10 SDK and Python 3:

```bash
python3 packaging/build-fm-windows.py
```

Produces `artifacts/FakeRadio-FM-RF-Windows-x64-CANDIDATE.zip` containing `FakeRadio.exe`, helper scripts and generated WAV examples. It does **not** contain native `ionos_channel.dll`; attempting to select IONOS modes on this build reports an error rather than silently changing algorithms. The resulting ZIP can be run on Windows without installing the .NET runtime.

## Full Windows package (FM + IONOS)

On a Windows 2022+ Visual C++ x64 Native Tools command prompt with PowerShell 7:

```powershell
pwsh -File packaging/Build-Windows.ps1
```

Or on Linux with .NET 10, Python 3, Make, GCC **and MinGW-w64 x64 cross-compiler**:

```bash
python3 packaging/build.py
```

Full-package build refuses to ship if the Windows IONOS DLL cannot be produced. The native HF DLL is required for `IONOS_*` modes.

## Optional Modem73 offline PHY test

See [Modem73 benchmark](MODEM73-BENCHMARK.md); it requires upstream Modem73 source in a separate clone and a locally available Relay73 adapter. Normal FakeRadio tests do *not* depend on that checkout.

## Distribution readiness

The software has passed isolated Windows virtual-audio/KISS integration testing. It has **not** been calibrated against radios, tested with physical PTT or approved for unattended gateway use. A GitHub source commit is not a signed release, and Windows may show SmartScreen warnings for unsigned new executables. Publish releases only after reviewing licenses, recording a release SHA and executing a versioned Windows validation checklist.
