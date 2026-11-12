using System.Text.Json;
using FakeRadio.AudioNode;

internal static class FmRfSelfTest
{
    private static int _passed;
    private const int SampleRate = 48_000;
    public static void Run()
    {
        _passed = 0;
        CheckProfiles();
        CheckSettingsSerialization();
        CheckDeterministicChunks();
        CheckDemodulation();
        CheckPttAndSquelch();
        CheckCoChannelInterference();
        Console.WriteLine($"FAKERADIO_FM_RF_TEST_PASS {_passed}/6");
    }

    private static void Pass(bool condition, string test)
    {
        if (!condition) throw new InvalidOperationException($"FM RF test failed: {test}");
        _passed++;
        Console.WriteLine($"FM_RF_PASS {test}");
    }

    private static float[] Tone(int length, double hz = 1000, double gain = 0.25)
    {
        var result = new float[length];
        for (var i = 0; i < length; i++)
            result[i] = (float)(gain * Math.Sin(2.0 * Math.PI * hz * i / SampleRate));
        return result;
    }

    private static void CheckProfiles()
    {
        foreach (var profile in new[] { "FM_RF_REFERENCE", "FM_VHF_FIXED", "FM_VHF_MOBILE",
            "FM_UHF_URBAN", "FM_UHF_EDGE", "FM_BUSY_SHARED", "FM_BAD_AUDIO" })
        {
            var cfg = ChannelProfiles.Resolve(profile);
            ChannelSettings.Validate(cfg);
            if (cfg.IsIdentity || cfg.FmMode == "OFF" || cfg.IonosMode != "OFF")
                throw new InvalidOperationException("Malformed FM profile: " + profile);
        }
        var rejected = false;
        try { ChannelSettings.Validate(ChannelProfiles.Resolve("FM_VHF_FIXED") with { IonosMode = "MPP" }); }
        catch (ArgumentException) { rejected = true; }
        Pass(rejected, "presets_and_mutually_exclusive_engine");
    }

    private static void CheckSettingsSerialization()
    {
        var fm = ChannelProfiles.Resolve("FM_UHF_URBAN") with { FmSpeedKmh = 74, FmSquelchDb = -23.0 };
        var json = JsonSerializer.Serialize(ChannelConfigFile.FromSnapshot(fm));
        var restored = JsonSerializer.Deserialize<ChannelConfigFile>(json)!.ToSnapshot();
        var cleanJson = JsonSerializer.Serialize(ChannelConfigFile.FromSnapshot(ChannelSnapshot.Clean()));
        Pass(restored.FmSpeedKmh == 74 && restored.FmSquelchDb == -23.0 &&
            restored.FmMode == "UHF_URBAN" && cleanJson.Contains("\"FmRfSnrDb\":null"),
            "backward_compatible_json_roundtrip");
    }

    private static void CheckDeterministicChunks()
    {
        var config = ChannelProfiles.Resolve("FM_VHF_MOBILE") with
        {
            FmPttAttackMs = 0.0,
            FmShadowDb = 3.0,
            NoiseSeed = 7070
        };
        var source = Tone(19_200);
        var one = new FmRfChannelProcessor().Process(source, config);
        var two = new List<float>();
        var chunked = new FmRfChannelProcessor();
        for (var i = 0; i < source.Length; i += 240)
            two.AddRange(chunked.Process(source[i..Math.Min(i + 240, source.Length)], config));
        var changed = new FmRfChannelProcessor().Process(source, config with { NoiseSeed = 7071 });
        Pass(one.SequenceEqual(two) && !one.SequenceEqual(changed) &&
            one.All(float.IsFinite), "seed_determinism_and_5ms_chunk_invariance");
    }

    private static void CheckDemodulation()
    {
        var cfg = ChannelProfiles.Resolve("FM_RF_REFERENCE") with
        {
            FmRfSnrDb = double.PositiveInfinity,
            FmRiceKDb = 50,
            FmSquelchDb = -70,
            FmRxAudioLowPassHz = 5000.0
        };
        var source = Tone(24_000, hz: 980);
        var decoded = new FmRfChannelProcessor().Process(source, cfg);
        var delay = 1500;
        double cross = 0, sx = 0, sy = 0;
        // Allow a few samples of filtering delay; the signal should
        // remain a ~980 Hz tone with comparable nonzero power.
        double totalPower = 0;
        for (var i = delay; i < source.Length; i++)
        {
            totalPower += decoded[i] * decoded[i];
            sx += source[i] * source[i];
            sy += decoded[i] * decoded[i];
            cross += source[i] * decoded[i];
        }
        var correlation = Math.Abs(cross) / Math.Sqrt(sx * sy);
        var ratio = Math.Sqrt(totalPower / sx);
        Console.WriteLine($"FM_RF_METER reference_correlation={correlation:F4} rms_ratio={ratio:F3}");
        Pass(correlation > 0.65 && ratio > 0.5 && ratio < 1.5,
            "fm_modulation_and_quadrature_discriminator");
    }

    private static void CheckPttAndSquelch()
    {
        var config = ChannelProfiles.Resolve("FM_VHF_FIXED") with
        {
            FmRfSnrDb = double.PositiveInfinity,
            FmRiceKDb = 50,
            FmPttAttackMs = 40,
            FmSquelchAttackMs = 10
        };
        var test = Tone(14_400);
        var channel = new FmRfChannelProcessor();
        var off = channel.Process(new float[240], config, transmitterKeyed: false);
        var on = channel.Process(test, config, transmitterKeyed: true);
        var startup = on[..(int)(SampleRate*0.035)].Select(Math.Abs).Max();
        var tail = on[(int)(SampleRate*0.150)..].Select(Math.Abs).Max();
        var muted = new FmRfChannelProcessor().Process(test, config with
        {
            FmPttAttackMs = 0,
            FmSquelchDb = 2,
            FmPathGainDb = -30
        });
        Pass(off.All(v => v == 0) && startup == 0 && tail > 0.04f &&
            muted.All(v => v == 0), "ptt_attack_and_squelch_cutoff");
    }

    private static void CheckCoChannelInterference()
    {
        var source = Tone(58_000, hz: 1234, gain: 0.28);
        var cfg = ChannelProfiles.Resolve("FM_BUSY_SHARED") with
        {
            FmRfSnrDb = double.PositiveInfinity,
            FmRiceKDb = 50,
            FmSquelchAttackMs = 0,
            FmInterfererDb = -120,
        };
        var ideal = new FmRfChannelProcessor().Process(source, cfg);
        var withInterferer = new FmRfChannelProcessor().Process(source,
            cfg with { FmInterfererDb = 8.0 });
        var start = SampleRate * 9 / 10;
        var end = SampleRate * 11 / 10;
        double idealError = 0, jammerError = 0;
        for(var i = start; i < end; i++)
        {
            idealError += Math.Pow(ideal[i] - source[i], 2);
            jammerError += Math.Pow(withInterferer[i] - source[i], 2);
        }
        Console.WriteLine($"FM_RF_METER clean_mse={idealError/(end-start):F7} jammer_mse={jammerError/(end-start):F7}");
        Pass(jammerError > idealError * 2 && withInterferer.All(float.IsFinite),
            "fm_capture_with_stronger_cochannel_signal");
    }
}
