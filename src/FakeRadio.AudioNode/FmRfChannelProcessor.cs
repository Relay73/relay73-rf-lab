using System.Numerics;

namespace FakeRadio.AudioNode;

/// <summary>
/// Experimental narrowband-FM baseband radio channel (NOT a calibrated RF
/// emulator). Converts 48-kHz audio to 384-kHz complex FM baseband, applies
/// short fractional-microsecond delays, sum-of-sinusoids fading, cochannel
/// FM interference and thermal noise, then limits/demodulates back to audio.
/// The transmitter, channel and receiver are all software; no RF hardware.
/// </summary>
internal sealed class FmRfChannelProcessor
{
    private const int AudioRate = 48_000;
    private const int Oversample = 8;
    private const int IqRate = AudioRate * Oversample;
    private const double SpeedOfLight = 299_792_458.0;
    private const int Oscillators = 12;

    private sealed class Path
    {
        public double DelayIq;
        public double Amplitude;
        public bool LineOfSight;
        public double[] Phases = new double[Oscillators];
        public double[] Increments = new double[Oscillators];
        public double LosPhase;
        public double LosIncrement;

        public Complex Coefficient(double kFactor)
        {
            Complex scattering = Complex.Zero;
            for (var k = 0; k < Oscillators; k++)
            {
                var angle = Phases[k];
                scattering += new Complex(Math.Cos(angle), Math.Sin(angle));
                Phases[k] = Wrap(angle + Increments[k]);
            }
            scattering /= Math.Sqrt(Oscillators);

            if (!LineOfSight)
                return Amplitude * scattering;

            var los = new Complex(Math.Cos(LosPhase), Math.Sin(LosPhase));
            LosPhase = Wrap(LosPhase + LosIncrement);
            // Rice K is the LOS power divided by scattered power.
            return Amplitude * (Math.Sqrt(kFactor / (1.0 + kFactor)) * los +
                                scattering / Math.Sqrt(1.0 + kFactor));
        }
    }

    private long _revision = long.MinValue;
    private Random _random = new(ChannelSettings.DefaultNoiseSeed);
    private Path[] _paths = [];
    private readonly Complex[] _iqRing = new Complex[64];
    private int _iqCursor;
    private double _transmitPhase, _jammerPhase, _jammerTonePhase;
    private Complex _previousRx = Complex.One;
    private double _previousAudio;
    private double _squelchPower;
    private double _squelchOpenSamples;
    private bool _squelchOpen, _wasKeyed;
    private int _pttDelayRemaining;
    private long _audioSampleIndex;
    private double _noiseSpare;
    private bool _haveNoiseSpare;
    private double _noiseLevel, _interferenceLevel;
    private double _audioDrive, _deviationHz, _squelchThresholdPower, _squelchAttackSamples;
    private double _shadowDepthDb, _shadowPhase;
    private double _channelGain;
    private bool _syntheticInterferer, _badAudio;
    private Biquad? _transmitHighPass, _transmitLowPass, _receiverLowPass1, _receiverLowPass2;

    public float[] Process(float[] input, ChannelSnapshot settings, bool transmitterKeyed = true)
    {
        if (settings.FmMode == "OFF")
            throw new ArgumentException("The FM RF model is disabled.", nameof(settings));
        if (_revision != settings.Revision)
            Reconfigure(settings);

        var output = new float[input.Length];
        // Audio samples and their 8 complex sub-samples are processed in order.
        // No state is reset at 5-ms packet boundaries.
        var fading = new Complex[_paths.Length];
        for (var j = 0; j < input.Length; j++)
        {
            var enabledCarrier = false;
            if (transmitterKeyed && !_wasKeyed)
                _pttDelayRemaining = (int)Math.Round(settings.FmPttAttackMs * AudioRate / 1000.0);
            if (!transmitterKeyed)
            {
                _pttDelayRemaining = 0;
                _squelchOpen = false;
                _squelchOpenSamples = 0;
            }
            _wasKeyed = transmitterKeyed;
            enabledCarrier = transmitterKeyed && _pttDelayRemaining <= 0;
            if (_pttDelayRemaining > 0)
                _pttDelayRemaining--;

            var audio = input[j] * _audioDrive;
            if (_transmitHighPass is not null)
                audio = _transmitHighPass.Process(audio);
            if (_transmitLowPass is not null)
                audio = _transmitLowPass.Process(audio);
            audio = Math.Clamp(audio, -1.0, 1.0);

            // A deterministic slow terrain/obstruction fade applied to the
            // desired carrier only. Unlike HF, RF path delay is microseconds.
            var time = _audioSampleIndex / (double)AudioRate;
            var shadowCycle = Math.Max(0.0, Math.Sin(2.0 * Math.PI * 0.18 * time + _shadowPhase));
            var shadowDb = -_shadowDepthDb * shadowCycle * shadowCycle;
            var wantedScale = enabledCarrier
                ? _channelGain * Math.Pow(10.0, shadowDb / 20.0)
                : 0.0;

            for (var p = 0; p < _paths.Length; p++)
                fading[p] = _paths[p].Coefficient(settings.FmRiceKDb <= -40.0 ? 0.0 :
                    Math.Pow(10.0, settings.FmRiceKDb / 10.0));
            var wantedPower = 0.0;
            // A continuous interfering FM transmission is an illustrative
            // hidden-station/capture input, not a real independently encoded
            // third Modem73 station.
            var jammerPeriod = (int)(_audioSampleIndex / (AudioRate * 0.8));
            var jammerActive = _syntheticInterferer && jammerPeriod % 4 is 1 or 2;
            var jammerLevel = jammerActive ? _interferenceLevel : 0.0;

            var demodulated = 0.0;
            for (var sub = 0; sub < Oversample; sub++)
            {
                // 48k audio is smoothly held/interpolated onto 384k RF time.
                var portion = (sub + 1.0) / Oversample;
                var x = _previousAudio + (audio - _previousAudio) * portion;
                _transmitPhase = Wrap(_transmitPhase +
                    2.0 * Math.PI * _deviationHz * x / IqRate);
                _iqRing[_iqCursor] = new Complex(
                    Math.Cos(_transmitPhase), Math.Sin(_transmitPhase));

                Complex wanted = Complex.Zero;
                if (enabledCarrier)
                {
                    for (var p = 0; p < _paths.Length; p++)
                    {
                        var delayed = Delayed(_paths[p].DelayIq);
                        wanted += fading[p] * delayed;
                    }
                    wanted *= wantedScale;
                }
                wantedPower += wanted.Magnitude * wanted.Magnitude / Oversample;

                Complex jammer = Complex.Zero;
                if (jammerLevel != 0.0)
                {
                    // Offset and FM-modulated interfering source to exercise
                    // the capture effect of a limiting FM discriminator.
                    _jammerTonePhase = Wrap(_jammerTonePhase + 2.0 * Math.PI * 730.0 / IqRate);
                    _jammerPhase = Wrap(_jammerPhase +
                        2.0 * Math.PI * (700.0 + 3200.0 * Math.Sin(_jammerTonePhase)) / IqRate);
                    jammer = jammerLevel * new Complex(Math.Cos(_jammerPhase), Math.Sin(_jammerPhase));
                }

                var mixed = wanted + jammer + _noiseLevel *
                    new Complex(NextGaussian(), NextGaussian());
                // Idealized FM limiting plus quadrature discriminator.
                var magnitude = mixed.Magnitude;
                var normalized = magnitude > 1e-15 ? mixed / magnitude : _previousRx;
                var step = normalized * Complex.Conjugate(_previousRx);
                _previousRx = normalized;
                demodulated += Math.Atan2(step.Imaginary, step.Real) *
                    IqRate / (2.0 * Math.PI * _deviationHz * Oversample);
                _iqCursor = (_iqCursor + 1) % _iqRing.Length;
            }
            _previousAudio = audio;
            _audioSampleIndex++;

            // Squelch sensing is based on desired/interferer carrier power
            // before injection of thermal noise; RSSI smoothing and open
            // attack emulate threshold and clipped leading packet audio.
            var carrierPower = wantedPower + jammerLevel * jammerLevel;
            _squelchPower += (carrierPower - _squelchPower) *
                (1.0 - Math.Exp(-1.0 / (0.001 * AudioRate)));
            if (_squelchPower >= _squelchThresholdPower)
                _squelchOpenSamples++;
            else
            {
                _squelchOpenSamples = 0;
                _squelchOpen = false;
            }
            if (_squelchOpenSamples >= _squelchAttackSamples)
                _squelchOpen = true;

            var audioOut = _receiverLowPass1!.Process(demodulated);
            audioOut = _receiverLowPass2!.Process(audioOut);
            output[j] = _squelchOpen ? (float)Math.Clamp(audioOut, -1.0, 1.0) : 0.0f;
        }
        return output;
    }

    private Complex Delayed(double delay)
    {
        var whole = (int)Math.Floor(delay);
        var fraction = delay - whole;
        var a = _iqRing[(_iqCursor - whole + _iqRing.Length) % _iqRing.Length];
        var b = _iqRing[(_iqCursor - whole - 1 + _iqRing.Length) % _iqRing.Length];
        // Linear fractional delay with oversampled *baseband* RF.
        return (1.0 - fraction) * a + fraction * b;
    }

    private static double Wrap(double angle) =>
        angle > Math.PI ? angle - 2.0 * Math.PI :
        angle < -Math.PI ? angle + 2.0 * Math.PI : angle;

    private double NextGaussian()
    {
        if (_haveNoiseSpare)
        {
            _haveNoiseSpare = false;
            return _noiseSpare;
        }
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();
        var radius = Math.Sqrt(-2.0 * Math.Log(u1));
        var angle = 2.0 * Math.PI * u2;
        _noiseSpare = radius * Math.Sin(angle);
        _haveNoiseSpare = true;
        return radius * Math.Cos(angle);
    }

    private void Reconfigure(ChannelSnapshot settings)
    {
        ChannelSettings.Validate(settings);
        _revision = settings.Revision;
        _random = new Random(settings.NoiseSeed);
        _iqCursor = 0;
        Array.Clear(_iqRing);
        _previousRx = Complex.One;
        _previousAudio = 0;
        _transmitPhase = _jammerPhase = _jammerTonePhase = 0;
        _squelchPower = _squelchOpenSamples = 0;
        _squelchOpen = _wasKeyed = false;
        _pttDelayRemaining = 0;
        _audioSampleIndex = 0;
        _haveNoiseSpare = false;
        _audioDrive = settings.FmAudioDrive;
        _deviationHz = settings.FmDeviationHz;
        _channelGain = Math.Pow(10.0, settings.FmPathGainDb / 20.0);
        _shadowDepthDb = settings.FmShadowDb;
        _shadowPhase = _random.NextDouble() * 2.0 * Math.PI;
        _squelchThresholdPower = Math.Pow(10.0, settings.FmSquelchDb / 10.0);
        _squelchAttackSamples = settings.FmSquelchAttackMs * AudioRate / 1000.0;
        _noiseLevel = double.IsPositiveInfinity(settings.FmRfSnrDb)
            ? 0.0 : Math.Pow(10.0, -settings.FmRfSnrDb / 20.0) / Math.Sqrt(2.0);
        _syntheticInterferer = settings.FmInterfererDb > -119.0;
        _interferenceLevel = Math.Pow(10.0, settings.FmInterfererDb / 20.0);
        _badAudio = settings.FmMode == "BAD_AUDIO";
        _transmitHighPass = _badAudio ? Biquad.HighPass(AudioRate, 450.0) : null;
        _transmitLowPass = _badAudio ? Biquad.LowPass(AudioRate, 1900.0) : null;
        // 48k playback/audio receiver: nominal <=3k usable voice/data passband.
        _receiverLowPass1 = Biquad.LowPass(AudioRate, settings.FmRxAudioLowPassHz);
        _receiverLowPass2 = Biquad.LowPass(AudioRate, settings.FmRxAudioLowPassHz);

        var carrierHz = settings.FmBand == "UHF" ? 435_000_000.0 : 145_000_000.0;
        var maxDopplerHz = settings.FmSpeedKmh / 3.6 / SpeedOfLight * carrierHz;
        var profile = settings.FmMode;
        var delays = profile switch
        {
            "REFERENCE" => new[] { 0.0 },
            "VHF_FIXED" => new[] { 0.0, 0.7 },
            "VHF_MOBILE" => new[] { 0.0, 0.7, 2.0 },
            "UHF_URBAN" => new[] { 0.0, 0.6, 2.2 },
            "UHF_EDGE" => new[] { 0.0, 0.7, 1.9 },
            "BUSY" => new[] { 0.0, 0.6 },
            "BAD_AUDIO" => new[] { 0.0 },
            _ => throw new ArgumentOutOfRangeException(nameof(settings.FmMode))
        };
        var weights = profile switch
        {
            "REFERENCE" => new[] { 1.0 },
            "VHF_FIXED" => new[] { 1.0, 0.08 },
            "VHF_MOBILE" => new[] { 0.8, 0.38, 0.20 },
            "UHF_URBAN" => new[] { 0.6, 0.55, 0.40 },
            "UHF_EDGE" => new[] { 0.6, 0.50, 0.35 },
            "BUSY" => new[] { 1.0, 0.1 },
            "BAD_AUDIO" => new[] { 1.0 },
            _ => new[] { 1.0 }
        };
        var norm = Math.Sqrt(weights.Sum(w => w * w));
        _paths = new Path[delays.Length];
        for (var p = 0; p < _paths.Length; p++)
        {
            var path = new Path
            {
                DelayIq = delays[p] * IqRate / 1_000_000.0,
                Amplitude = weights[p] / norm,
                LineOfSight = p == 0 && settings.FmRiceKDb > -40.0,
                LosPhase = profile == "REFERENCE" || profile == "BAD_AUDIO" ? 0 : 2.0 * Math.PI * _random.NextDouble(),
                LosIncrement = 2.0 * Math.PI * maxDopplerHz * Math.Cos(0.4) / AudioRate
            };
            for (var k = 0; k < Oscillators; k++)
            {
                // Seeded Clarke-like isotropic scatterers with one oscillator
                // per arrival azimuth, updated once per 48-kHz audio sample.
                var azimuth = 2.0 * Math.PI * (k + _random.NextDouble()) / Oscillators;
                path.Phases[k] = 2.0 * Math.PI * _random.NextDouble();
                path.Increments[k] = 2.0 * Math.PI * maxDopplerHz *
                    Math.Cos(azimuth) / AudioRate;
            }
            _paths[p] = path;
        }
    }
}
