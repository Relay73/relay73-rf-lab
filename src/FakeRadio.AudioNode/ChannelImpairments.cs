using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FakeRadio.AudioNode;

internal readonly record struct ChannelSnapshot(
    long Revision,
    double GainDb,
    double SnrDb,
    int NoiseSeed,
    double ClipLevel,
    double DcOffset,
    double HighPassHz,
    double LowPassHz,
    double ClockPpm,
    double ClockDriftPpmPerSecond,
    double DropoutRatePerMinute,
    double DropoutDurationMs,
    double ImpulseRatePerMinute,
    double ImpulseAmplitude,
    double EchoDelayMs,
    double EchoGainDb,
    string Profile,
    double FadeDepthDb = 0.0,
    double FadeRateHz = 0.0,
    string IonosMode = "OFF",
    int IonosPaths = 2,
    double IonosOffsetHz = 0.0,
    string FmMode = "OFF",
    string FmBand = "VHF",
    double FmSpeedKmh = 0.0,
    double FmRfSnrDb = double.PositiveInfinity,
    double FmRiceKDb = 40.0,
    double FmShadowDb = 0.0,
    double FmPathGainDb = 0.0,
    double FmSquelchDb = -60.0,
    double FmSquelchAttackMs = 0.0,
    double FmPttAttackMs = 0.0,
    double FmInterfererDb = -120.0,
    double FmAudioDrive = 1.0,
    double FmDeviationHz = 4800.0,
    double FmRxAudioLowPassHz = 3500.0)
{
    public static ChannelSnapshot Clean(double gainDb = 0.0, double snrDb = double.PositiveInfinity, int noiseSeed = ChannelSettings.DefaultNoiseSeed) =>
        new(
            0,
            gainDb,
            snrDb,
            noiseSeed,
            1.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            "CUSTOM");

    public bool IsIdentity =>
        GainDb == 0.0 &&
        double.IsPositiveInfinity(SnrDb) &&
        ClipLevel == 1.0 &&
        DcOffset == 0.0 &&
        HighPassHz == 0.0 &&
        LowPassHz == 0.0 &&
        ClockPpm == 0.0 &&
        ClockDriftPpmPerSecond == 0.0 &&
        DropoutRatePerMinute == 0.0 &&
        ImpulseRatePerMinute == 0.0 &&
        EchoDelayMs == 0.0 &&
        FadeDepthDb == 0.0 &&
        IonosMode == "OFF" &&
        FmMode == "OFF";

    public string ToStatusFields()
    {
        static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        var snr = double.IsPositiveInfinity(SnrDb) ? "OFF" : F(SnrDb);
        return $"GAIN {F(GainDb)} SNR {snr} SEED {NoiseSeed} CLIP {F(ClipLevel)} DC {F(DcOffset)} " +
            $"HP {F(HighPassHz)} LP {F(LowPassHz)} CLOCK_PPM {F(ClockPpm)} DRIFT_PPM_S {F(ClockDriftPpmPerSecond)} " +
            $"DROPOUT_PER_MIN {F(DropoutRatePerMinute)} DROPOUT_MS {F(DropoutDurationMs)} " +
            $"IMPULSE_PER_MIN {F(ImpulseRatePerMinute)} IMPULSE_AMP {F(ImpulseAmplitude)} " +
            $"ECHO_MS {F(EchoDelayMs)} ECHO_DB {F(EchoGainDb)} FADE_DEPTH_DB {F(FadeDepthDb)} FADE_HZ {F(FadeRateHz)} " +
            $"ENGINE {(FmMode != "OFF" ? "FM_RF" : IonosMode != "OFF" ? "IONOS" : "LEGACY")} " +
            $"IONOS_MODE {IonosMode} IONOS_PATHS {IonosPaths} IONOS_OFFSET_HZ {F(IonosOffsetHz)} " +
            $"FM_MODE {FmMode} FM_BAND {FmBand} FM_SPEED_KMH {F(FmSpeedKmh)} FM_RF_SNR_DB {(double.IsPositiveInfinity(FmRfSnrDb) ? "OFF" : F(FmRfSnrDb))} " +
            $"FM_K_DB {F(FmRiceKDb)} FM_SHADOW_DB {F(FmShadowDb)} FM_GAIN_DB {F(FmPathGainDb)} FM_SQL_DB {F(FmSquelchDb)} " +
            $"FM_SQL_MS {F(FmSquelchAttackMs)} FM_PTT_MS {F(FmPttAttackMs)} FM_INTERFERER_DB {F(FmInterfererDb)} " +
            $"FM_DRIVE {F(FmAudioDrive)} FM_DEVIATION_HZ {F(FmDeviationHz)} FM_RX_LP_HZ {F(FmRxAudioLowPassHz)} PROFILE {Profile} REV {Revision}";
    }

    public ChannelSnapshot WithoutRevision() => this with { Revision = 0 };
}

internal readonly record struct ChannelChange(DateTimeOffset ChangedUtc, ChannelSnapshot Snapshot);

internal sealed class ChannelSettings
{
    public const int DefaultNoiseSeed = 0x5641464D;
    private const int MaxHistory = 4096;
    private readonly object _gate = new();
    private ChannelSnapshot _snapshot;
    private readonly List<ChannelChange> _history = new();

    public ChannelSettings(ChannelSnapshot initial)
    {
        Validate(initial);
        _snapshot = initial with { Revision = 0 };
    }

    public ChannelSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public void Update(Func<ChannelSnapshot, ChannelSnapshot> update)
    {
        lock (_gate)
        {
            var next = update(_snapshot) with
            {
                Revision = checked(_snapshot.Revision + 1),
                Profile = "CUSTOM"
            };
            Validate(next);
            _snapshot = next;
            _history.Add(new ChannelChange(DateTimeOffset.UtcNow, next));
            if (_history.Count > MaxHistory)
                _history.RemoveRange(0, _history.Count - MaxHistory);
        }
    }

    public void ApplyProfile(string name)
    {
        lock (_gate)
        {
            var next = ChannelProfiles.Resolve(name) with
            {
                Revision = checked(_snapshot.Revision + 1)
            };
            Validate(next);
            _snapshot = next;
            _history.Add(new ChannelChange(DateTimeOffset.UtcNow, next));
            if (_history.Count > MaxHistory)
                _history.RemoveRange(0, _history.Count - MaxHistory);
        }
    }

    public ChannelChange[] ChangesAfter(long revision)
    {
        lock (_gate)
            return _history.Where(change => change.Snapshot.Revision > revision).ToArray();
    }

    public static void Validate(ChannelSnapshot value)
    {
        static void Finite(string name, double number)
        {
            if (!double.IsFinite(number))
                throw new ArgumentOutOfRangeException(name, "Value must be finite.");
        }

        Finite(nameof(value.GainDb), value.GainDb);
        if (!double.IsPositiveInfinity(value.SnrDb))
            Finite(nameof(value.SnrDb), value.SnrDb);
        Finite(nameof(value.ClipLevel), value.ClipLevel);
        Finite(nameof(value.DcOffset), value.DcOffset);
        Finite(nameof(value.HighPassHz), value.HighPassHz);
        Finite(nameof(value.LowPassHz), value.LowPassHz);
        Finite(nameof(value.ClockPpm), value.ClockPpm);
        Finite(nameof(value.ClockDriftPpmPerSecond), value.ClockDriftPpmPerSecond);
        Finite(nameof(value.DropoutRatePerMinute), value.DropoutRatePerMinute);
        Finite(nameof(value.DropoutDurationMs), value.DropoutDurationMs);
        Finite(nameof(value.ImpulseRatePerMinute), value.ImpulseRatePerMinute);
        Finite(nameof(value.ImpulseAmplitude), value.ImpulseAmplitude);
        Finite(nameof(value.EchoDelayMs), value.EchoDelayMs);
        Finite(nameof(value.EchoGainDb), value.EchoGainDb);
        Finite(nameof(value.FadeDepthDb), value.FadeDepthDb);
        Finite(nameof(value.FadeRateHz), value.FadeRateHz);
        Finite(nameof(value.IonosOffsetHz), value.IonosOffsetHz);
        Finite(nameof(value.FmSpeedKmh), value.FmSpeedKmh);
        if (!double.IsPositiveInfinity(value.FmRfSnrDb)) Finite(nameof(value.FmRfSnrDb), value.FmRfSnrDb);
        Finite(nameof(value.FmRiceKDb), value.FmRiceKDb);
        Finite(nameof(value.FmShadowDb), value.FmShadowDb);
        Finite(nameof(value.FmPathGainDb), value.FmPathGainDb);
        Finite(nameof(value.FmSquelchDb), value.FmSquelchDb);
        Finite(nameof(value.FmSquelchAttackMs), value.FmSquelchAttackMs);
        Finite(nameof(value.FmPttAttackMs), value.FmPttAttackMs);
        Finite(nameof(value.FmInterfererDb), value.FmInterfererDb);
        Finite(nameof(value.FmAudioDrive), value.FmAudioDrive);
        Finite(nameof(value.FmDeviationHz), value.FmDeviationHz);
        Finite(nameof(value.FmRxAudioLowPassHz), value.FmRxAudioLowPassHz);

        if (value.GainDb is < -120.0 or > 60.0)
            throw new ArgumentOutOfRangeException(nameof(value.GainDb), "Gain must be between -120 and +60 dB.");
        if (!double.IsPositiveInfinity(value.SnrDb) && value.SnrDb is < -40.0 or > 120.0)
            throw new ArgumentOutOfRangeException(nameof(value.SnrDb), "SNR must be OFF or between -40 and 120 dB.");
        if (value.ClipLevel is <= 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(value.ClipLevel), "Clip level must be greater than 0 and at most 1.");
        if (Math.Abs(value.DcOffset) > 1.0)
            throw new ArgumentOutOfRangeException(nameof(value.DcOffset), "DC offset magnitude must be at most 1.");
        if (value.HighPassHz is < 0.0 or > 20_000.0)
            throw new ArgumentOutOfRangeException(nameof(value.HighPassHz), "High-pass frequency must be 0..20000 Hz.");
        if (value.LowPassHz is < 0.0 or > 24_000.0)
            throw new ArgumentOutOfRangeException(nameof(value.LowPassHz), "Low-pass frequency must be 0..24000 Hz.");
        if (value.HighPassHz > 0.0 && value.LowPassHz > 0.0 && value.HighPassHz >= value.LowPassHz)
            throw new ArgumentOutOfRangeException(nameof(value.HighPassHz), "High-pass frequency must be below low-pass frequency.");
        if (Math.Abs(value.ClockPpm) > 10_000.0)
            throw new ArgumentOutOfRangeException(nameof(value.ClockPpm), "Clock offset magnitude must be at most 10000 ppm.");
        if (Math.Abs(value.ClockDriftPpmPerSecond) > 1_000.0)
            throw new ArgumentOutOfRangeException(nameof(value.ClockDriftPpmPerSecond), "Clock drift magnitude must be at most 1000 ppm/s.");
        if (value.DropoutRatePerMinute is < 0.0 or > 60_000.0)
            throw new ArgumentOutOfRangeException(nameof(value.DropoutRatePerMinute), "Dropout rate must be 0..60000/min.");
        if (value.DropoutDurationMs is < 0.0 or > 60_000.0)
            throw new ArgumentOutOfRangeException(nameof(value.DropoutDurationMs), "Dropout duration must be 0..60000 ms.");
        if (value.DropoutRatePerMinute > 0.0 && value.DropoutDurationMs <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(value.DropoutDurationMs), "A nonzero dropout rate requires a positive duration.");
        if (value.ImpulseRatePerMinute is < 0.0 or > 60_000.0)
            throw new ArgumentOutOfRangeException(nameof(value.ImpulseRatePerMinute), "Impulse rate must be 0..60000/min.");
        if (value.ImpulseAmplitude is < 0.0 or > 10.0)
            throw new ArgumentOutOfRangeException(nameof(value.ImpulseAmplitude), "Impulse amplitude must be 0..10.");
        if (value.ImpulseRatePerMinute > 0.0 && value.ImpulseAmplitude <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(value.ImpulseAmplitude), "A nonzero impulse rate requires a positive amplitude.");
        if (value.EchoDelayMs is < 0.0 or > 5_000.0)
            throw new ArgumentOutOfRangeException(nameof(value.EchoDelayMs), "Echo delay must be 0..5000 ms.");
        if (value.EchoGainDb is < -120.0 or > 0.0)
            throw new ArgumentOutOfRangeException(nameof(value.EchoGainDb), "Echo gain must be between -120 and 0 dB.");
        if (value.FadeDepthDb is < 0.0 or > 60.0)
            throw new ArgumentOutOfRangeException(nameof(value.FadeDepthDb), "Fade depth must be 0..60 dB.");
        if (value.FadeRateHz is < 0.0 or > 20.0 || (value.FadeDepthDb > 0.0 && value.FadeRateHz <= 0.0))
            throw new ArgumentOutOfRangeException(nameof(value.FadeRateHz), "Fade rate must be >0..20 Hz if fading is enabled.");
        if (value.IonosMode is not ("OFF" or "WGN" or "MPG" or "MPM" or "MPP" or "MPD"))
            throw new ArgumentOutOfRangeException(nameof(value.IonosMode), "IONOS mode must be OFF/WGN/MPG/MPM/MPP/MPD.");
        if (value.IonosPaths is not (2 or 4))
            throw new ArgumentOutOfRangeException(nameof(value.IonosPaths), "IONOS paths must be 2 or 4.");
        if (Math.Abs(value.IonosOffsetHz) > 200.0)
            throw new ArgumentOutOfRangeException(nameof(value.IonosOffsetHz), "IONOS audio frequency offset must be within +/-200 Hz.");
        if (value.FmMode is not ("OFF" or "REFERENCE" or "VHF_FIXED" or "VHF_MOBILE" or "UHF_URBAN" or "UHF_EDGE" or "BUSY" or "BAD_AUDIO"))
            throw new ArgumentOutOfRangeException(nameof(value.FmMode), "Unknown FM RF mode.");
        if (value.FmBand is not ("VHF" or "UHF"))
            throw new ArgumentOutOfRangeException(nameof(value.FmBand), "FM band must be VHF or UHF.");
        if (value.FmMode != "OFF" && value.IonosMode != "OFF")
            throw new ArgumentException("FM RF and IONOS are mutually exclusive channel engines.");
        if (value.FmSpeedKmh is < 0 or > 250 || value.FmShadowDb is < 0 or > 50 ||
            value.FmRiceKDb is < -50 or > 50 || value.FmPathGainDb is < -80 or > 20 ||
            value.FmSquelchDb is < -80 or > 10 || value.FmSquelchAttackMs is < 0 or > 200 ||
            value.FmPttAttackMs is < 0 or > 500 || value.FmInterfererDb is < -120 or > 20 ||
            value.FmAudioDrive is <= 0 or > 10 || value.FmDeviationHz is < 1500 or > 6000 ||
            value.FmRxAudioLowPassHz is < 1000 or > 10000 ||
            (!double.IsPositiveInfinity(value.FmRfSnrDb) && (value.FmRfSnrDb < -10 || value.FmRfSnrDb > 90)))
            throw new ArgumentOutOfRangeException(nameof(value.FmMode), "FM RF parameter out of supported range.");
    }
}

internal static class ChannelProfiles
{
    public static readonly string[] Names =
    [
        "CLEAN",
        "FM_AWGN_12DB",
        "FM_LOW_GAIN",
        "FM_CLIP",
        "FM_CLOCK_100PPM",
        "VHF_MOBILE_FADING",
        "VHF_URBAN_MULTIPATH",
        "UHF_WEAK_SIGNAL",
        "UHF_INTERFERENCE_BURSTS",
        "HF_STABLE",
        "HF_QSB_SLOW",
        "HF_SELECTIVE_FADING",
        "HF_NOISY_BAND",
        "HF_BURST_INTERFERENCE",
        "HF_MULTIPATH_1",
        "IONOS_WGN",
        "IONOS_MPG",
        "IONOS_MPM",
        "IONOS_MPP",
        "IONOS_MPD",
        "FM_RF_REFERENCE",
        "FM_VHF_FIXED",
        "FM_VHF_MOBILE",
        "FM_UHF_URBAN",
        "FM_UHF_EDGE",
        "FM_BUSY_SHARED",
        "FM_BAD_AUDIO"
    ];

    public static ChannelSnapshot Resolve(string name)
    {
        var normalized = name.Trim().ToUpperInvariant();
        var clean = ChannelSnapshot.Clean(noiseSeed: ChannelSettings.DefaultNoiseSeed);
        return normalized switch
        {
            "CLEAN" => clean with { Profile = normalized },
            "FM_AWGN_12DB" => clean with { GainDb = -6.0, SnrDb = 12.0, Profile = normalized },
            "FM_LOW_GAIN" => clean with { GainDb = -24.0, Profile = normalized },
            "FM_CLIP" => clean with { GainDb = 6.0, ClipLevel = 0.35, Profile = normalized },
            "FM_CLOCK_100PPM" => clean with { GainDb = -6.0, ClockPpm = 100.0, Profile = normalized },
            "VHF_MOBILE_FADING" => clean with { GainDb = -6.0, SnrDb = 14.0, FadeDepthDb = 18.0, FadeRateHz = 1.2, Profile = normalized },
            "VHF_URBAN_MULTIPATH" => clean with { GainDb = -6.0, SnrDb = 18.0, EchoDelayMs = 1.6, EchoGainDb = -8.0, FadeDepthDb = 10.0, FadeRateHz = 0.4, Profile = normalized },
            "UHF_WEAK_SIGNAL" => clean with { GainDb = -12.0, SnrDb = 7.0, FadeDepthDb = 22.0, FadeRateHz = 0.2, Profile = normalized },
            "UHF_INTERFERENCE_BURSTS" => clean with { GainDb = -6.0, SnrDb = 13.0, ImpulseRatePerMinute = 120.0, ImpulseAmplitude = 0.65, DropoutRatePerMinute = 12.0, DropoutDurationMs = 35.0, Profile = normalized },
            "HF_STABLE" => clean with { GainDb = -6.0, SnrDb = 25.0, HighPassHz = 250.0, LowPassHz = 2900.0, Profile = normalized },
            "HF_QSB_SLOW" => clean with { GainDb = -6.0, SnrDb = 18.0, HighPassHz = 250.0, LowPassHz = 2900.0, FadeDepthDb = 24.0, FadeRateHz = 0.25, Profile = normalized },
            "HF_SELECTIVE_FADING" => clean with { GainDb = -6.0, SnrDb = 15.0, HighPassHz = 300.0, LowPassHz = 2700.0, FadeDepthDb = 20.0, FadeRateHz = 0.6, EchoDelayMs = 4.0, EchoGainDb = -7.0, Profile = normalized },
            "HF_NOISY_BAND" => clean with { GainDb = -6.0, SnrDb = 6.0, HighPassHz = 250.0, LowPassHz = 3000.0, ImpulseRatePerMinute = 60.0, ImpulseAmplitude = 0.45, Profile = normalized },
            "HF_BURST_INTERFERENCE" => clean with { GainDb = -6.0, SnrDb = 10.0, HighPassHz = 250.0, LowPassHz = 2900.0, ImpulseRatePerMinute = 180.0, ImpulseAmplitude = 0.6, DropoutRatePerMinute = 25.0, DropoutDurationMs = 45.0, Profile = normalized },
            "HF_MULTIPATH_1" => clean with { GainDb = -6.0, EchoDelayMs = 4.0, EchoGainDb = -6.0, Profile = normalized },
            "IONOS_WGN" => clean with { SnrDb = 15.0, IonosMode = "WGN", Profile = normalized },
            "IONOS_MPG" => clean with { SnrDb = 15.0, IonosMode = "MPG", Profile = normalized },
            "IONOS_MPM" => clean with { SnrDb = 15.0, IonosMode = "MPM", Profile = normalized },
            "IONOS_MPP" => clean with { SnrDb = 15.0, IonosMode = "MPP", Profile = normalized },
            "IONOS_MPD" => clean with { SnrDb = 15.0, IonosMode = "MPD", Profile = normalized },
            "FM_RF_REFERENCE" => clean with { FmMode = "REFERENCE", FmBand = "VHF", FmRfSnrDb = 60, FmRiceKDb = 50, FmSquelchDb = -60, Profile = normalized },
            "FM_VHF_FIXED" => clean with { FmMode = "VHF_FIXED", FmBand = "VHF", FmRfSnrDb = 46, FmRiceKDb = 24, FmSquelchDb = -38, FmPttAttackMs = 12, FmSquelchAttackMs = 2, Profile = normalized },
            "FM_VHF_MOBILE" => clean with { FmMode = "VHF_MOBILE", FmBand = "VHF", FmRfSnrDb = 37, FmRiceKDb = 3, FmSpeedKmh = 50, FmShadowDb = 12, FmSquelchDb = -32, FmPttAttackMs = 25, FmSquelchAttackMs = 4, Profile = normalized },
            "FM_UHF_URBAN" => clean with { FmMode = "UHF_URBAN", FmBand = "UHF", FmRfSnrDb = 38, FmRiceKDb = -50, FmSpeedKmh = 30, FmShadowDb = 8, FmSquelchDb = -32, FmPttAttackMs = 30, FmSquelchAttackMs = 5, Profile = normalized },
            "FM_UHF_EDGE" => clean with { FmMode = "UHF_EDGE", FmBand = "UHF", FmRfSnrDb = 29, FmRiceKDb = -50, FmSpeedKmh = 6, FmShadowDb = 22, FmPathGainDb = -10, FmSquelchDb = -19, FmPttAttackMs = 60, FmSquelchAttackMs = 18, Profile = normalized },
            "FM_BUSY_SHARED" => clean with { FmMode = "BUSY", FmBand = "VHF", FmRfSnrDb = 42, FmRiceKDb = 20, FmInterfererDb = -1, FmSquelchDb = -32, FmSquelchAttackMs = 4, Profile = normalized },
            "FM_BAD_AUDIO" => clean with { FmMode = "BAD_AUDIO", FmBand = "VHF", FmRfSnrDb = 46, FmRiceKDb = 30, FmAudioDrive = 3.6, FmDeviationHz = 3500, FmRxAudioLowPassHz = 2200, FmSquelchDb = -35, FmPttAttackMs = 75, FmSquelchAttackMs = 3, Profile = normalized },
            _ => throw new ArgumentException($"Unknown channel profile '{name}'. Available: {string.Join(", ", Names)}")
        };
    }
}

internal sealed class ChannelImpairmentProcessor
{
    private const int SampleRate = 48_000;
    private long _revision = long.MinValue;
    private Random _random = new(ChannelSettings.DefaultNoiseSeed);
    private Biquad? _highPass;
    private Biquad? _lowPass;
    private float[] _echoBuffer = [];
    private int _echoIndex;
    private int _dropoutRemaining;
    private double _clockAccumulator;
    private double _elapsedSeconds;

    public float[] Process(float[] input, ChannelSnapshot settings)
    {
        if (_revision != settings.Revision)
            Reconfigure(settings);

        var samples = input.ToArray();
        var gain = Math.Pow(10.0, settings.GainDb / 20.0);
        double squareSum = 0.0;
        var echoGain = settings.EchoDelayMs > 0.0 ? Math.Pow(10.0, settings.EchoGainDb / 20.0) : 0.0;
        var dropoutProbability = settings.DropoutRatePerMinute / (60.0 * SampleRate);
        var impulseProbability = settings.ImpulseRatePerMinute / (60.0 * SampleRate);
        var dropoutSamples = Math.Max(1, (int)Math.Round(settings.DropoutDurationMs * SampleRate / 1000.0));
        var fading = settings.FadeDepthDb > 0.0;

        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i] * gain + settings.DcOffset;
            if (_highPass is not null)
                sample = _highPass.Process(sample);
            if (_lowPass is not null)
                sample = _lowPass.Process(sample);

            // A simple time-varying direct path against an independently held delayed
            // path. This models audible QSB and changing two-path cancellation in an
            // audio passband, not a calibrated Watterson/ionospheric channel.
            var unfadedDirect = sample;
            if (fading)
            {
                var time = _elapsedSeconds + i / (double)SampleRate;
                var attenuationDb = -0.5 * settings.FadeDepthDb *
                    (1.0 - Math.Cos(2.0 * Math.PI * settings.FadeRateHz * time));
                sample *= Math.Pow(10.0, attenuationDb / 20.0);
            }
            if (_echoBuffer.Length > 0)
            {
                sample += _echoBuffer[_echoIndex] * echoGain;
                _echoBuffer[_echoIndex] = (float)unfadedDirect;
                _echoIndex++;
                if (_echoIndex == _echoBuffer.Length)
                    _echoIndex = 0;
            }

            if (_dropoutRemaining <= 0 && dropoutProbability > 0.0 && _random.NextDouble() < dropoutProbability)
                _dropoutRemaining = dropoutSamples;
            if (_dropoutRemaining > 0)
            {
                sample = 0.0;
                _dropoutRemaining--;
            }

            if (impulseProbability > 0.0 && _random.NextDouble() < impulseProbability)
                sample += (_random.Next(2) == 0 ? -1.0 : 1.0) * settings.ImpulseAmplitude;

            // Preserve the legacy gain/AWGN calibration exactly: quantize the channel
            // sample to float before measuring frame RMS, just as the original FakeRadio did.
            samples[i] = (float)sample;
            // In a fading profile, reference noise to the pre-fade direct path.
            // Otherwise noise would fade with the signal and mask the SNR loss.
            squareSum += fading ? unfadedDirect * unfadedDirect : samples[i] * samples[i];
        }

        var rms = Math.Sqrt(squareSum / Math.Max(1, samples.Length));
        var addNoise = !double.IsPositiveInfinity(settings.SnrDb) && rms > 0.0001;
        var noiseStd = addNoise ? rms / Math.Pow(10.0, settings.SnrDb / 20.0) : 0.0;

        for (var i = 0; i < samples.Length; i++)
        {
            if (addNoise)
                samples[i] += (float)(noiseStd * NextGaussian(_random));
            samples[i] = Math.Clamp(samples[i], (float)-settings.ClipLevel, (float)settings.ClipLevel);
        }

        var effectiveClockPpm = settings.ClockPpm + settings.ClockDriftPpmPerSecond * _elapsedSeconds;
        effectiveClockPpm = Math.Clamp(effectiveClockPpm, -10_000.0, 10_000.0);
        _elapsedSeconds += input.Length / (double)SampleRate;
        if (effectiveClockPpm == 0.0)
            return samples;

        return ApplyClockOffset(samples, effectiveClockPpm);
    }

    private void Reconfigure(ChannelSnapshot settings)
    {
        _revision = settings.Revision;
        _random = new Random(settings.NoiseSeed);
        _highPass = settings.HighPassHz > 0.0 ? Biquad.HighPass(SampleRate, settings.HighPassHz) : null;
        _lowPass = settings.LowPassHz > 0.0 ? Biquad.LowPass(SampleRate, settings.LowPassHz) : null;
        var echoSamples = settings.EchoDelayMs > 0.0
            ? Math.Max(1, (int)Math.Round(settings.EchoDelayMs * SampleRate / 1000.0))
            : 0;
        _echoBuffer = echoSamples > 0 ? new float[echoSamples] : [];
        _echoIndex = 0;
        _dropoutRemaining = 0;
        _clockAccumulator = 0.0;
        _elapsedSeconds = 0.0;
    }

    private float[] ApplyClockOffset(float[] samples, double ppm)
    {
        var ratio = 1.0 + ppm / 1_000_000.0;
        var output = new List<float>(Math.Max(1, (int)Math.Ceiling(samples.Length * ratio) + 1));
        foreach (var sample in samples)
        {
            _clockAccumulator += ratio;
            while (_clockAccumulator >= 1.0)
            {
                output.Add(sample);
                _clockAccumulator -= 1.0;
            }
        }

        return output.ToArray();
    }

    private static double NextGaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = 1.0 - random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

internal sealed class Biquad
{
    private readonly double _b0;
    private readonly double _b1;
    private readonly double _b2;
    private readonly double _a1;
    private readonly double _a2;
    private double _x1;
    private double _x2;
    private double _y1;
    private double _y2;

    private Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        _b0 = b0;
        _b1 = b1;
        _b2 = b2;
        _a1 = a1;
        _a2 = a2;
    }

    public static Biquad LowPass(double sampleRate, double frequency) => Create(sampleRate, frequency, false);
    public static Biquad HighPass(double sampleRate, double frequency) => Create(sampleRate, frequency, true);

    public float Process(double input)
    {
        var output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = input;
        _y2 = _y1;
        _y1 = output;
        return (float)output;
    }

    private static Biquad Create(double sampleRate, double frequency, bool highPass)
    {
        var omega = 2.0 * Math.PI * frequency / sampleRate;
        var cosine = Math.Cos(omega);
        var sine = Math.Sin(omega);
        const double q = 0.7071067811865476;
        var alpha = sine / (2.0 * q);
        var a0 = 1.0 + alpha;
        var b0 = highPass ? (1.0 + cosine) / 2.0 : (1.0 - cosine) / 2.0;
        var b1 = highPass ? -(1.0 + cosine) : 1.0 - cosine;
        var b2 = b0;
        return new Biquad(
            b0 / a0,
            b1 / a0,
            b2 / a0,
            -2.0 * cosine / a0,
            (1.0 - alpha) / a0);
    }
}

internal sealed record ChannelConfigFile(
    double GainDb = 0.0,
    double? SnrDb = null,
    int NoiseSeed = ChannelSettings.DefaultNoiseSeed,
    double ClipLevel = 1.0,
    double DcOffset = 0.0,
    double HighPassHz = 0.0,
    double LowPassHz = 0.0,
    double ClockPpm = 0.0,
    double ClockDriftPpmPerSecond = 0.0,
    double DropoutRatePerMinute = 0.0,
    double DropoutDurationMs = 0.0,
    double ImpulseRatePerMinute = 0.0,
    double ImpulseAmplitude = 0.0,
    double EchoDelayMs = 0.0,
    double EchoGainDb = 0.0,
    string Profile = "CUSTOM",
    double FadeDepthDb = 0.0,
    double FadeRateHz = 0.0,
    string IonosMode = "OFF",
    int IonosPaths = 2,
    double IonosOffsetHz = 0.0,
    string FmMode = "OFF",
    string FmBand = "VHF",
    double FmSpeedKmh = 0.0,
    double? FmRfSnrDb = null,
    double FmRiceKDb = 40.0,
    double FmShadowDb = 0.0,
    double FmPathGainDb = 0.0,
    double FmSquelchDb = -60.0,
    double FmSquelchAttackMs = 0.0,
    double FmPttAttackMs = 0.0,
    double FmInterfererDb = -120.0,
    double FmAudioDrive = 1.0,
    double FmDeviationHz = 4800.0,
    double FmRxAudioLowPassHz = 3500.0)
{
    public ChannelSnapshot ToSnapshot() => new(
        0,
        GainDb,
        SnrDb ?? double.PositiveInfinity,
        NoiseSeed,
        ClipLevel,
        DcOffset,
        HighPassHz,
        LowPassHz,
        ClockPpm,
        ClockDriftPpmPerSecond,
        DropoutRatePerMinute,
        DropoutDurationMs,
        ImpulseRatePerMinute,
        ImpulseAmplitude,
        EchoDelayMs,
        EchoGainDb,
        Profile,
        FadeDepthDb,
        FadeRateHz,
        IonosMode,
        IonosPaths,
        IonosOffsetHz,
        FmMode,
        FmBand,
        FmSpeedKmh,
        FmRfSnrDb ?? double.PositiveInfinity,
        FmRiceKDb,
        FmShadowDb,
        FmPathGainDb,
        FmSquelchDb,
        FmSquelchAttackMs,
        FmPttAttackMs,
        FmInterfererDb,
        FmAudioDrive,
        FmDeviationHz,
        FmRxAudioLowPassHz);

    public static ChannelConfigFile FromSnapshot(ChannelSnapshot value) => new(
        value.GainDb,
        double.IsPositiveInfinity(value.SnrDb) ? null : value.SnrDb,
        value.NoiseSeed,
        value.ClipLevel,
        value.DcOffset,
        value.HighPassHz,
        value.LowPassHz,
        value.ClockPpm,
        value.ClockDriftPpmPerSecond,
        value.DropoutRatePerMinute,
        value.DropoutDurationMs,
        value.ImpulseRatePerMinute,
        value.ImpulseAmplitude,
        value.EchoDelayMs,
        value.EchoGainDb,
        value.Profile,
        value.FadeDepthDb,
        value.FadeRateHz,
        value.IonosMode,
        value.IonosPaths,
        value.IonosOffsetHz,
        value.FmMode,
        value.FmBand,
        value.FmSpeedKmh,
        double.IsPositiveInfinity(value.FmRfSnrDb) ? null : value.FmRfSnrDb,
        value.FmRiceKDb,
        value.FmShadowDb,
        value.FmPathGainDb,
        value.FmSquelchDb,
        value.FmSquelchAttackMs,
        value.FmPttAttackMs,
        value.FmInterfererDb,
        value.FmAudioDrive,
        value.FmDeviationHz,
        value.FmRxAudioLowPassHz);
}

internal static class ChannelSelfTest
{
    public static void Run()
    {
        TestLegacyCleanPath();
        TestLegacyNoiseCalibration();
        TestSeededNoise();
        TestEcho();
        TestClip();
        TestClockOffset();
        TestProfiles();
        TestFiltersAndDc();
        TestDropoutsAndImpulses();
        TestSettingsHistory();
        TestJsonMetadata();
        TestFading();
        TestChunkBoundaryFading();
        TestInvalidFade();
        TestIonosMetadata();
        Console.WriteLine("FakeRadio channel self-test: 15/15 passed.");
    }

    private static void TestLegacyCleanPath()
    {
        var settings = ChannelSnapshot.Clean(-6.0);
        var input = new[] { -2.0f, -0.5f, 0.0f, 0.5f, 2.0f };
        var actual = new ChannelImpairmentProcessor().Process(input, settings);
        var gain = Math.Pow(10.0, -6.0 / 20.0);
        var expected = input.Select(value => Math.Clamp((float)(value * gain), -1.0f, 1.0f)).ToArray();
        AssertSequence(expected, actual, 1e-7f, "legacy clean/gain path");
    }

    private static void TestLegacyNoiseCalibration()
    {
        var input = Enumerable.Range(0, 240).Select(i => (float)(0.1 + 0.3 * Math.Sin(i * 0.07))).ToArray();
        var settings = ChannelSnapshot.Clean(-6.0, 12.0, ChannelSettings.DefaultNoiseSeed);
        var actual = new ChannelImpairmentProcessor().Process(input, settings);

        var expected = input.ToArray();
        var gain = Math.Pow(10.0, settings.GainDb / 20.0);
        double squareSum = 0.0;
        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = (float)(expected[i] * gain);
            squareSum += expected[i] * expected[i];
        }

        var rms = Math.Sqrt(squareSum / expected.Length);
        var noiseStd = rms / Math.Pow(10.0, settings.SnrDb / 20.0);
        var random = new Random(ChannelSettings.DefaultNoiseSeed);
        for (var i = 0; i < expected.Length; i++)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = 1.0 - random.NextDouble();
            var gaussian = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            expected[i] += (float)(noiseStd * gaussian);
            expected[i] = Math.Clamp(expected[i], -1.0f, 1.0f);
        }

        AssertSequence(expected, actual, 0.0f, "legacy seeded AWGN calibration");
    }

    private static void TestSeededNoise()
    {
        var settings = ChannelSnapshot.Clean(snrDb: 12.0, noiseSeed: 12345);
        var input = Enumerable.Repeat(0.25f, 1024).ToArray();
        var first = new ChannelImpairmentProcessor().Process(input, settings);
        var second = new ChannelImpairmentProcessor().Process(input, settings);
        AssertSequence(first, second, 0.0f, "seeded noise determinism");
        if (first.SequenceEqual(input))
            throw new InvalidOperationException("seeded noise test did not alter the signal");
    }

    private static void TestEcho()
    {
        var settings = ChannelSnapshot.Clean() with { EchoDelayMs = 1.0, EchoGainDb = -6.0 };
        var input = new float[96];
        input[0] = 0.25f;
        var actual = new ChannelImpairmentProcessor().Process(input, settings);
        var expectedEcho = (float)(0.25 * Math.Pow(10.0, -6.0 / 20.0));
        if (Math.Abs(actual[48] - expectedEcho) > 1e-6)
            throw new InvalidOperationException($"echo test failed: expected {expectedEcho}, got {actual[48]}");
    }

    private static void TestClip()
    {
        var settings = ChannelSnapshot.Clean() with { ClipLevel = 0.2 };
        var actual = new ChannelImpairmentProcessor().Process([-0.8f, 0.8f], settings);
        AssertSequence([-0.2f, 0.2f], actual, 0.0f, "clip");
    }

    private static void TestClockOffset()
    {
        var settings = ChannelSnapshot.Clean() with { ClockPpm = 10_000.0 };
        var actual = new ChannelImpairmentProcessor().Process(new float[48_000], settings);
        if (actual.Length != 48_480)
            throw new InvalidOperationException($"clock test failed: expected 48480 samples, got {actual.Length}");
    }

    private static void TestProfiles()
    {
        var clean = ChannelProfiles.Resolve("CLEAN");
        if (!clean.IsIdentity || clean.Profile != "CLEAN")
            throw new InvalidOperationException("CLEAN profile is not an identity channel");
        var hf = ChannelProfiles.Resolve("HF_MULTIPATH_1");
        if (hf.EchoDelayMs <= 0.0 || hf.EchoGainDb >= 0.0)
            throw new InvalidOperationException("HF_MULTIPATH_1 profile did not enable a delayed attenuated echo");
    }

    private static void TestFiltersAndDc()
    {
        var dcSettings = ChannelSnapshot.Clean() with { DcOffset = 0.125 };
        var dc = new ChannelImpairmentProcessor().Process(new float[64], dcSettings);
        if (dc.Any(value => Math.Abs(value - 0.125f) > 1e-7f))
            throw new InvalidOperationException("DC offset test failed");

        var highPassSettings = ChannelSnapshot.Clean() with { HighPassHz = 100.0 };
        var highPassed = new ChannelImpairmentProcessor().Process(Enumerable.Repeat(0.25f, 48_000).ToArray(), highPassSettings);
        if (Math.Abs(highPassed[^1]) > 0.001f)
            throw new InvalidOperationException($"high-pass test failed: terminal DC was {highPassed[^1]}");

        var lowPassSettings = ChannelSnapshot.Clean() with { LowPassHz = 1_000.0 };
        var alternating = Enumerable.Range(0, 48_000).Select(i => i % 2 == 0 ? 0.25f : -0.25f).ToArray();
        var lowPassed = new ChannelImpairmentProcessor().Process(alternating, lowPassSettings);
        var tailRms = Math.Sqrt(lowPassed[^4_800..].Select(value => value * value).Average());
        if (tailRms > 0.02)
            throw new InvalidOperationException($"low-pass test failed: tail RMS was {tailRms}");
    }

    private static void TestDropoutsAndImpulses()
    {
        var dropoutSettings = ChannelSnapshot.Clean(noiseSeed: 2468) with
        {
            DropoutRatePerMinute = 60_000.0,
            DropoutDurationMs = 1.0
        };
        var dropped = new ChannelImpairmentProcessor().Process(Enumerable.Repeat(0.25f, 48_000).ToArray(), dropoutSettings);
        if (!dropped.Any(value => value == 0.0f) || !dropped.Any(value => value != 0.0f))
            throw new InvalidOperationException("dropout test did not produce both muted and unmuted samples");

        var impulseSettings = ChannelSnapshot.Clean(noiseSeed: 1357) with
        {
            ImpulseRatePerMinute = 60_000.0,
            ImpulseAmplitude = 0.5
        };
        var impulses = new ChannelImpairmentProcessor().Process(new float[48_000], impulseSettings);
        if (!impulses.Any(value => Math.Abs(value) >= 0.49f))
            throw new InvalidOperationException("impulse test did not produce an impulse");
    }

    private static void TestSettingsHistory()
    {
        var settings = new ChannelSettings(ChannelSnapshot.Clean());
        settings.Update(value => value with { GainDb = -6.0 });
        settings.Update(value => value with { SnrDb = 12.0 });
        var changes = settings.ChangesAfter(0);
        if (changes.Length != 2 || changes[0].Snapshot.Revision != 1 || changes[1].Snapshot.Revision != 2)
            throw new InvalidOperationException("channel settings history test failed");
    }

    private static void TestJsonMetadata()
    {
        var json = JsonSerializer.Serialize(ChannelSnapshot.Clean(), RecordingChannelMetadata.CompactJsonOptions);
        if (json.Contains('\n', StringComparison.Ordinal) || !json.Contains("\"Infinity\"", StringComparison.Ordinal))
            throw new InvalidOperationException("compact channel JSON test failed");
    }

    private static void TestFading()
    {
        var settings = ChannelProfiles.Resolve("HF_QSB_SLOW");
        if (settings.FadeDepthDb < 15.0 || settings.FadeRateHz <= 0.0)
            throw new InvalidOperationException("HF QSB preset is not configured");
        // The fading envelope reaches a minimum after half its period.
        var noNoise = ChannelSnapshot.Clean() with { FadeDepthDb = 24.0, FadeRateHz = 1.0 };
        var actual = new ChannelImpairmentProcessor().Process(Enumerable.Repeat(0.5f, 48_001).ToArray(), noNoise);
        var expected = (float)(0.5 * Math.Pow(10.0, -24.0 / 20.0));
        if (Math.Abs(actual[24_000] - expected) > 1e-6 || Math.Abs(actual[0] - 0.5f) > 1e-6 ||
            Math.Abs(actual[48_000] - 0.5f) > 1e-6)
            throw new InvalidOperationException("Fading envelope is incorrect");
    }

    private static void TestChunkBoundaryFading()
    {
        var settings = ChannelSnapshot.Clean() with { FadeDepthDb = 20.0, FadeRateHz = 0.4, EchoDelayMs = 3.0, EchoGainDb = -9.0 };
        var input = Enumerable.Range(0, 7_200).Select(i => (float)(0.18 * Math.Sin(2.0 * Math.PI * 900 * i / 48_000))).ToArray();
        var full = new ChannelImpairmentProcessor().Process(input, settings);
        var processor = new ChannelImpairmentProcessor();
        var chunks = new List<float>();
        for (var i = 0; i < input.Length; i += 240)
            chunks.AddRange(processor.Process(input[i..Math.Min(i + 240, input.Length)], settings));
        AssertSequence(full, chunks.ToArray(), 1e-6f, "fading/echo chunk invariance");
    }

    private static void TestInvalidFade()
    {
        foreach (var settings in new[]
        {
            ChannelSnapshot.Clean() with { FadeDepthDb = 61.0, FadeRateHz = 0.3 },
            ChannelSnapshot.Clean() with { FadeDepthDb = 10.0, FadeRateHz = 0.0 },
            ChannelSnapshot.Clean() with { FadeDepthDb = 10.0, FadeRateHz = double.NaN }
        })
        {
            try
            {
                ChannelSettings.Validate(settings);
                throw new InvalidOperationException("Invalid fading settings were accepted");
            }
            catch (ArgumentOutOfRangeException)
            {
                // Expected.
            }
        }
        var json = JsonSerializer.Serialize(ChannelConfigFile.FromSnapshot(ChannelProfiles.Resolve("HF_SELECTIVE_FADING")));
        var restored = JsonSerializer.Deserialize<ChannelConfigFile>(json)!.ToSnapshot();
        if (restored.FadeDepthDb != 20.0 || restored.FadeRateHz != 0.6)
            throw new InvalidOperationException("Fading configuration did not survive JSON round trip");
    }

    private static void TestIonosMetadata()
    {
        var input = ChannelProfiles.Resolve("IONOS_MPM") with { IonosPaths = 4, IonosOffsetHz = 9.0 };
        ChannelSettings.Validate(input);
        var roundtrip = JsonSerializer.Deserialize<ChannelConfigFile>(JsonSerializer.Serialize(ChannelConfigFile.FromSnapshot(input)))!.ToSnapshot();
        if (roundtrip.IonosMode != "MPM" || roundtrip.IonosPaths != 4 || roundtrip.IonosOffsetHz != 9.0 || roundtrip.IsIdentity)
            throw new InvalidOperationException("IONOS mode JSON did not round-trip");
    }

    private static void AssertSequence(float[] expected, float[] actual, float tolerance, string name)
    {
        if (expected.Length != actual.Length)
            throw new InvalidOperationException($"{name} length mismatch: expected {expected.Length}, got {actual.Length}");
        for (var i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(expected[i] - actual[i]) > tolerance)
                throw new InvalidOperationException($"{name} mismatch at {i}: expected {expected[i]}, got {actual[i]}");
        }
    }
}

internal sealed record RecordingChannelMetadata(
    string Schema,
    string Stream,
    string WavFile,
    DateTimeOffset StartedUtc,
    DateTimeOffset StoppedUtc,
    ChannelSnapshot StartChannel,
    ChannelSnapshot StopChannel,
    ChannelChange[] Changes)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
}
