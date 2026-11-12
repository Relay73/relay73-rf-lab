using System.Text;
using System.Text.Json;
using FakeRadio.AudioNode;

internal static class Program
{
    private const int SampleRate = 48_000;
    private const int ChunkSamples = 240;

    private static readonly string[] ExampleProfiles =
    [
        "CLEAN", "VHF_MOBILE_FADING", "VHF_URBAN_MULTIPATH",
        "UHF_WEAK_SIGNAL", "HF_STABLE", "HF_QSB_SLOW",
        "HF_SELECTIVE_FADING", "HF_NOISY_BAND", "HF_BURST_INTERFERENCE"
    ];

    private static readonly string[] IonosExampleProfiles =
    ["IONOS_WGN", "IONOS_MPG", "IONOS_MPM", "IONOS_MPP", "IONOS_MPD"];

    private static readonly string[] FmRfExampleProfiles =
    ["FM_RF_REFERENCE", "FM_VHF_FIXED", "FM_VHF_MOBILE", "FM_UHF_URBAN", "FM_UHF_EDGE", "FM_BUSY_SHARED", "FM_BAD_AUDIO"];

    public static int Main(string[] args)
    {
        if (args.Contains("--test-fm"))
        {
            FmRfSelfTest.Run();
            return 0;
        }
        if (args.Contains("--test-native"))
        {
            TestNative();
            return 0;
        }
        if (args.Contains("--test"))
        {
            ChannelSelfTest.Run();
            return 0;
        }
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("FakeRadio portable WAV examples (no device or network access)");
            Console.WriteLine("  dotnet run --project samples/FakeRadio.Examples -c Release -- --output examples/audio --seconds 8");
            Console.WriteLine("  dotnet run --project samples/FakeRadio.Examples -c Release -- --test-native");
            Console.WriteLine("  dotnet run --project samples/FakeRadio.Examples -c Release -- --ionos-examples --output examples/ionos --seconds 8");
            Console.WriteLine("  dotnet run --project samples/FakeRadio.Examples -c Release -- --test-fm");
            Console.WriteLine("  dotnet run --project samples/FakeRadio.Examples -c Release -- --fm-examples --output examples/fm --seconds 8");
            Console.WriteLine("  dotnet run --project samples/FakeRadio.Examples -c Release -- --fm-input modem73.wav --output examples/fm-modem");
            Console.WriteLine("Synthetic modem-like audio, not actual Modem73 or live RF captures.");
            return 0;
        }

        var output = "examples/audio";
        var seconds = 8;
        var ionosExamples = args.Contains("--ionos-examples");
        var fmExamples = args.Contains("--fm-examples");
        string? fmInput = null;
        string? fmProfile = null;
        int? fmSeed = null;
        double? fmSpeed = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--ionos-examples" or "--fm-examples")
                continue;
            if (args[i] == "--fm-input" && i + 1 < args.Length)
            {
                fmInput = args[++i];
                continue;
            }
            if (args[i] == "--fm-profile" && i + 1 < args.Length)
            {
                fmProfile = args[++i].ToUpperInvariant();
                continue;
            }
            if (args[i] == "--fm-seed" && i + 1 < args.Length && int.TryParse(args[++i], out var parsedSeed))
            {
                fmSeed = parsedSeed;
                continue;
            }
            if (args[i] == "--fm-speed-kmh" && i + 1 < args.Length &&
                double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedSpeed))
            {
                fmSpeed = parsedSpeed;
                continue;
            }
            if (args[i] == "--output" && i + 1 < args.Length)
                output = args[++i];
            else if (args[i] == "--seconds" && i + 1 < args.Length &&
                     int.TryParse(args[++i], out var value) && value is >= 2 and <= 30)
                seconds = value;
            else
                throw new ArgumentException($"Unrecognized option/value: {args[i]}");
        }

        Directory.CreateDirectory(output);
        if (ionosExamples && fmExamples) throw new ArgumentException("Select a single channel engine.");
        var input = fmInput is null ? CreateSyntheticModemAudio(seconds) : ReadWave(fmInput);
        var manifest = new List<object>();

        var profiles = ionosExamples ? IonosExampleProfiles : fmExamples || fmInput is not null ? FmRfExampleProfiles : ExampleProfiles;
        if (fmProfile is not null)
        {
            if (!FmRfExampleProfiles.Contains(fmProfile))
                throw new ArgumentException($"Unknown FM profile {fmProfile}");
            if (ionosExamples) throw new ArgumentException("Cannot mix FM profile with IONOS processing.");
            profiles = [fmProfile];
        }
        foreach (var profileName in profiles)
        {
            // Process 5 ms audio frames using the same implementation as
            // FakeRadio's live TCP/WASAPI receiver for the selected engine.
            var profile = ChannelProfiles.Resolve(profileName);
            if (fmSeed.HasValue) profile = profile with { NoiseSeed = fmSeed.Value };
            if (fmSpeed.HasValue) profile = profile with { FmSpeedKmh = fmSpeed.Value };
            ChannelSettings.Validate(profile);
            var processor = new ChannelImpairmentProcessor();
            using var ionos = new IonosNativeProcessor();
            var fm = new FmRfChannelProcessor();
            var rendered = new List<float>(input.Length);
            for (var i = 0; i < input.Length; i += ChunkSamples)
            {
                var window = input[i..Math.Min(i + ChunkSamples, input.Length)];
                var processed = profile.FmMode != "OFF" ? fm.Process(window, profile, transmitterKeyed: true) :
                    ionosExamples ? ionos.Process(window, profile) : processor.Process(window, profile);
                rendered.AddRange(processed);
            }

            var fileName = profileName.ToLowerInvariant() + ".wav";
            var path = Path.Combine(output, fileName);
            WriteWave(path, rendered);
            if (fmInput is not null)
            {
                using var raw = File.Create(Path.Combine(output, profileName.ToLowerInvariant() + ".f32"));
                using var writer = new BinaryWriter(raw);
                foreach (var value in rendered) writer.Write(value);
            }
            manifest.Add(new
            {
                File = fileName,
                Profile = profileName,
                DurationSeconds = rendered.Count / (double)SampleRate,
                SampleRate,
                Audio = "Deterministic synthetic two-tone modem-like illustration, not a real modem recording",
                Settings = ChannelConfigFile.FromSnapshot(profile)
            });
            Console.WriteLine($"EXAMPLE_OK {fileName} samples={rendered.Count}");
        }

        File.WriteAllText(Path.Combine(output, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"FAKERADIO_EXAMPLES_OK count={profiles.Length} directory={Path.GetFullPath(output)}");
        return 0;
    }

    private static void TestNative()
    {
        IonosNativeProcessor.AssertAvailable();
        var samples = CreateSyntheticModemAudio(2);
        var settings = ChannelProfiles.Resolve("IONOS_MPM") with { IonosPaths = 4, IonosOffsetHz = 2.0, NoiseSeed = 42 };
        using var one = new IonosNativeProcessor();
        using var other = new IonosNativeProcessor();
        var a = new List<float>();
        var b = new List<float>();
        for (var i = 0; i < samples.Length; i += ChunkSamples)
            a.AddRange(one.Process(samples[i..Math.Min(i + ChunkSamples, samples.Length)], settings));
        b.AddRange(other.Process(samples, settings));
        if (!a.SequenceEqual(b) || a.Count != samples.Length || a.All(x => x == 0.0f))
            throw new InvalidOperationException("IONOS P/Invoke bridge / chunk invariance failed");
        Console.WriteLine("FAKERADIO_IONOS_INTEROP_PASS 1/1");
    }

    private static float[] ReadWave(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
            throw new InvalidDataException("Expected PCM16 RIFF input");
        _ = reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "WAVEfmt " || reader.ReadInt32() != 16 ||
            reader.ReadInt16() != 1 || reader.ReadInt16() != 1 || reader.ReadInt32() != SampleRate)
            throw new InvalidDataException("Expected 48 kHz mono PCM16 input");
        _ = reader.ReadInt32(); _ = reader.ReadInt16();
        if (reader.ReadInt16() != 16 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "data")
            throw new InvalidDataException("Unsupported PCM16 WAV layout");
        var bytes = reader.ReadInt32();
        if (bytes < 0 || bytes > SampleRate * 2 * 60 || bytes % 2 != 0)
            throw new InvalidDataException("WAV too large or malformed");
        var audio = new float[bytes / 2];
        for (var i = 0; i < audio.Length; i++) audio[i] = reader.ReadInt16() / 32768.0f;
        return audio;
    }

    private static float[] CreateSyntheticModemAudio(int seconds)
    {
        var samples = new float[seconds * SampleRate];
        var phase = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)SampleRate;
            var symbolIndex = (int)(t / 0.093);
            var toneHz = symbolIndex % 3 switch { 0 => 700.0, 1 => 1170.0, _ => 1760.0 };
            phase += 2.0 * Math.PI * toneHz / SampleRate;
            if (phase > 2.0 * Math.PI)
                phase -= 2.0 * Math.PI;
            var active = t % 2.5 < 2.2;
            // 3 ms transitions prevent clicks masking the intentional fading.
            var symbolTime = t % 0.093;
            var attack = Math.Min(1.0, symbolTime / 0.003);
            samples[i] = active ? (float)(0.21 * attack * Math.Sin(phase)) : 0.0f;
        }
        return samples;
    }

    private static void WriteWave(string path, IReadOnlyList<float> samples)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        const short channels = 1;
        const short bitsPerSample = 16;
        var dataSize = checked(samples.Count * sizeof(short));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (var sample in samples)
            writer.Write((short)Math.Round(Math.Clamp(sample, -1.0f, 1.0f) * 32767.0));
    }
}
