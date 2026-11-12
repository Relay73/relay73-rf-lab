param([string]$OutputDirectory = "")
# Run from "x64 Native Tools Command Prompt for VS 2022" (with pwsh installed).
# Native IONOS engine is required; refuse to ship a binary with broken IONOS modes.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$publishDir = Join-Path ([IO.Path]::GetTempPath()) ('fakeradio-build-' + [Guid]::NewGuid().ToString('N'))
$archive = Join-Path $OutputDirectory 'FakeRadio-Windows-x64.zip'
$project = [IO.Path]::Combine($root, 'src', 'FakeRadio.AudioNode', 'FakeRadio.AudioNode.csproj')
$examples = [IO.Path]::Combine($root, 'samples', 'FakeRadio.Examples', 'FakeRadio.Examples.csproj')
$nativeDir = [IO.Path]::Combine($root, 'native', 'ionos')
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    throw 'MSVC cl.exe not available. Use the VS 2022 x64 Native Tools command prompt with C++ build tools installed.'
}
try {
    dotnet run --project $examples -c Release -- --test
    if ($LASTEXITCODE -ne 0) { throw 'Channel tests failed; refusing package' }
    dotnet run --project $examples -c Release -- --test-fm
    if ($LASTEXITCODE -ne 0) { throw 'FM RF tests failed; refusing package' }
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'FakeRadio Windows publish failed' }

    Push-Location $publishDir
    try {
        $core = Join-Path $nativeDir 'IonosChannel.cpp'
        $cli = Join-Path $nativeDir 'wav_cli.cpp'
        & cl.exe /nologo /std:c++17 /EHsc /O2 /MT /LD "/Fe:ionos_channel.dll" $core
        if ($LASTEXITCODE -ne 0) { throw 'IONOS native DLL build failed' }
        & cl.exe /nologo /std:c++17 /EHsc /O2 /MT "/Fe:ionos-wav.exe" $core $cli
        if ($LASTEXITCODE -ne 0) { throw 'IONOS native WAV CLI build failed' }
    }
    finally {
        Pop-Location
    }
    $dll = Join-Path $publishDir 'ionos_channel.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw 'IONOS native library was not produced' }
    dotnet build $examples -c Release -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Interop test host did not compile' }
    $interopDir = [IO.Path]::Combine($root, 'samples', 'FakeRadio.Examples', 'bin', 'Release', 'net10.0')
    Copy-Item -LiteralPath $dll -Destination (Join-Path $interopDir 'ionos_channel.dll') -Force
    dotnet run --no-build --project $examples -c Release -- --test-native
    if ($LASTEXITCODE -ne 0) { throw 'Windows IONOS native/.NET interoperability test failed' }
    dotnet run --project $examples -c Release -- --output (Join-Path $publishDir 'examples/audio') --seconds 8
    if ($LASTEXITCODE -ne 0) { throw 'Audio demo render failed' }
    dotnet run --project $examples -c Release -- --fm-examples --output (Join-Path $publishDir 'examples/fm') --seconds 8
    if ($LASTEXITCODE -ne 0) { throw 'FM RF audio preview render failed' }
    $sourceAudio = Join-Path $publishDir 'examples/audio/clean.wav'
    $ionosAudio = Join-Path $publishDir 'examples/ionos'
    New-Item -ItemType Directory -Path $ionosAudio -Force | Out-Null
    foreach ($mode in @('WGN','MPG','MPM','MPP','MPD')) {
        $target = Join-Path $ionosAudio ('ionos_' + $mode.ToLowerInvariant() + '.wav')
        & (Join-Path $publishDir 'ionos-wav.exe') --input $sourceAudio --output $target --mode $mode --paths 2 --snr-db 15 --seed 2026
        if ($LASTEXITCODE -ne 0) { throw "IONOS WAV render failed for $mode" }
    }
    Copy-Item (Join-Path $nativeDir 'IONOS-LICENSE.txt') (Join-Path $publishDir 'IONOS-LICENSE.txt') -Force
    Copy-Item (Join-Path $nativeDir 'LICENSE') (Join-Path $publishDir 'FakeRadio-IONOS-LICENSE.txt') -Force
    Copy-Item (Join-Path $PSScriptRoot 'README.md') (Join-Path $publishDir 'README.md') -Force
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publishDir 'LICENSE') -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $publishDir 'THIRD-PARTY-NOTICES.md') -Force
    foreach ($file in @('Start-FakeRadio.cmd', 'Show-Devices.cmd', 'Change-Profile.cmd', 'Change-Profile.ps1')) {
        Copy-Item (Join-Path $PSScriptRoot $file) (Join-Path $publishDir $file) -Force
    }
    foreach ($suffix in @('*.pdb','*.obj','*.lib','*.exp','*.ilk')) {
        Get-ChildItem -LiteralPath $publishDir -Filter $suffix -File | Remove-Item -Force
    }
    $stagedArchive = Join-Path $publishDir 'FakeRadio-Windows-x64.zip'
    Compress-Archive -Path (Get-ChildItem -LiteralPath $publishDir | Where-Object { $_.Name -ne 'FakeRadio-Windows-x64.zip' } | ForEach-Object FullName) -DestinationPath $stagedArchive -CompressionLevel Optimal
    Copy-Item -LiteralPath $stagedArchive -Destination $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    Write-Output "FAKERADIO_IONOS_PACKAGE_OK archive=$archive sha256=$hash"
}
finally {
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
}
