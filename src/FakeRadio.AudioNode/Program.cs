using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FakeRadio.AudioNode;

internal static class Program
{
    private const int SampleRate = 48_000;
    private const int MaximumInjectionSeconds = 15;
    private const int FrameSamples = 240; // 5 ms mono float32 -> 960-byte TCP audio payload.
    internal const int DefaultPttPreRollFrames = 5; // 25 ms historical default.
    internal const int DefaultPttHangMilliseconds = 150; // Historical tail-preservation default.
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VAFM");

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--channel-self-test", StringComparer.OrdinalIgnoreCase))
        {
            ChannelSelfTest.Run();
            return 0;
        }

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }
        if (args.Contains("--profiles", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var profile in ChannelProfiles.Names)
                Console.WriteLine(profile);
            return 0;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("FakeRadio live audio bridge needs Windows WASAPI. Portable channel previews and tests work on Linux/macOS.");
            return 1;
        }

        using var enumerator = new MMDeviceEnumerator();
        if (args.Contains("--list-devices", StringComparer.OrdinalIgnoreCase))
        {
            ListDevices(enumerator);
            return 0;
        }

        var configPath = Path.Combine(AppContext.BaseDirectory, "fakeradio.json");
        Options options;
        if (args.Contains("--setup", StringComparer.OrdinalIgnoreCase) || (args.Length == 0 && !File.Exists(configPath)))
        {
            options = RunSetup(enumerator);
            SaveConfig(configPath, options);
            Console.WriteLine($"Saved setup to {configPath}");
        }
        else if (args.Length == 0)
        {
            options = LoadConfig(configPath);
            Console.WriteLine($"Loaded setup from {configPath}");
        }
        else
        {
            options = Options.Parse(args);
        }
        if (options.Channel.IonosMode != "OFF")
            IonosNativeProcessor.AssertAvailable();
        var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var captureDevice = FindDevice(captureDevices, options.CaptureDevice, "capture/recording");
        var playbackDevice = FindDevice(renderDevices, options.PlaybackDevice, "render/playback");

        Console.WriteLine("FakeRadio — software-only audio channel simulator");
        Console.WriteLine($"TX capture (recording): {captureDevice.FriendlyName}");
        Console.WriteLine($"RX playback:           {playbackDevice.FriendlyName}");
        Console.WriteLine($"TCP audio local:      0.0.0.0:{options.ListenPort}");
        Console.WriteLine($"TCP audio peer:       {options.PeerHost}:{options.PeerPort}");
        Console.WriteLine($"Control TCP:          {(options.ControlLocalOnly ? "127.0.0.1" : "0.0.0.0")}:{options.ControlPort}");
        Console.WriteLine($"TX monitor TCP:       {(options.ControlLocalOnly ? "127.0.0.1" : "0.0.0.0")}:{options.MonitorPort}");
        Console.WriteLine($"RX monitor TCP:       {(options.ControlLocalOnly ? "127.0.0.1" : "0.0.0.0")}:{options.RxMonitorPort}");
        Console.WriteLine($"Channel gain:         {options.Channel.GainDb:F1} dB");
        Console.WriteLine($"Target SNR:           {(double.IsPositiveInfinity(options.Channel.SnrDb) ? "OFF" : $"{options.Channel.SnrDb:F1} dB")}");
        Console.WriteLine($"Channel profile:       {options.Channel.Profile}");
        Console.WriteLine($"Channel seed:          {options.Channel.NoiseSeed}");
        var pttDescription = options.PttPort is not null
            ? $"CTS on {options.PttPort}{(options.PttInvert ? " (inverted)" : string.Empty)}"
            : options.ExternalPtt ? "external TNC control" : "always-on audio bridge";
        Console.WriteLine($"PTT source:           {pttDescription}");

        var channel = new ChannelSettings(options.Channel);
        var ptt = new PttState(options.PttPort is null && !options.ExternalPtt);
        var pttGate = new PttGateState();
        var diagnostics = new AudioDiagnostics();
        var testTone = new TestToneState();
        var txInjection = new TxInjectionState();
        using var recorder = new TxWaveRecorder(AppContext.BaseDirectory, SampleRate, "tx", channel);
        using var playbackRecorder = new TxWaveRecorder(AppContext.BaseDirectory, SampleRate, "rx", channel);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Keep capture latency in the same low-latency range as playback. The default WASAPI
        // capture buffering is large enough to push the responder waveform into VARA's retry
        // window in the two-node laboratory, causing otherwise clean replies to collide.
        using var capture = new WasapiCapture(captureDevice, true, 10);
        // This signal is level-triggered: one pending wakeup is enough to tell the send loop
        // that capture data may now be available. An unbounded semaphore accumulates one token
        // per WASAPI callback even while the consumer is already draining buffered audio. After
        // a long-running lab session that stale-token backlog can let an empty-buffer underflow
        // loop run much faster than real time and flood the peer playback queue.
        using var captureSignal = new SemaphoreSlim(0, 1);
        var captureBuffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        capture.DataAvailable += (_, e) =>
        {
            captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            try
            {
                captureSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // A wakeup is already pending. The signal is deliberately level-triggered.
            }
        };

        var canonical = BuildCanonicalProvider(captureBuffer, capture.WaveFormat);
        var outputBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1))
        {
            // Capacity is deliberately larger than the normal network jitter requirement so the
            // lab-only PCM injection command can enqueue a complete short PHY crop atomically.
            // BufferedWaveProvider does not prebuffer this duration, so this does not add two seconds
            // of normal receive latency.
            BufferDuration = TimeSpan.FromSeconds(Math.Max(MaximumInjectionSeconds, options.JitterBufferMs / 1000.0)),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        using var output = new WasapiOut(playbackDevice, AudioClientShareMode.Shared, true, 10);
        output.Init(outputBuffer);

        var audioListener = new TcpListener(IPAddress.Any, options.ListenPort);
        audioListener.Start();
        var monitorHub = new AudioMonitorHub(options.MonitorPort, options.ControlLocalOnly);
        var rxMonitorHub = new AudioMonitorHub(options.RxMonitorPort, options.ControlLocalOnly);

        capture.StartRecording();
        output.Play();

        Console.WriteLine("TCP audio bridge running. Ctrl+C to stop.");

        var tasks = new List<Task>
        {
            SendLoopAsync(options.PeerHost, options.PeerPort, canonical, captureSignal, ptt, pttGate, diagnostics, testTone, txInjection, recorder, monitorHub, cts.Token),
            ReceiveLoopAsync(audioListener, outputBuffer, channel, ptt, pttGate, diagnostics, playbackRecorder, rxMonitorHub, cts.Token),
            ControlLoopAsync(options.ControlPort, options.ControlLocalOnly, channel, ptt, pttGate, diagnostics, testTone, txInjection, recorder, playbackRecorder, outputBuffer, monitorHub, rxMonitorHub, cts.Token),
            monitorHub.RunAsync(cts.Token),
            rxMonitorHub.RunAsync(cts.Token)
        };
        if (options.PttPort is not null)
            tasks.Add(SerialPttLoopAsync(options.PttPort, options.PttInvert, ptt, cts.Token));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            capture.StopRecording();
            output.Stop();
        }

        return 0;
    }

    private static ISampleProvider BuildCanonicalProvider(BufferedWaveProvider source, WaveFormat format)
    {
        ISampleProvider sample = source.ToSampleProvider();
        if (sample.WaveFormat.Channels == 2)
        {
            sample = new StereoToMonoSampleProvider(sample) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        else if (sample.WaveFormat.Channels != 1)
        {
            var mux = new MultiplexingSampleProvider(new[] { sample }, 1);
            mux.ConnectInputToOutput(0, 0);
            sample = mux;
        }

        if (sample.WaveFormat.SampleRate != SampleRate)
            sample = new WdlResamplingSampleProvider(sample, SampleRate);

        return sample;
    }

    private static async Task SendLoopAsync(string peerHost, int peerPort, ISampleProvider source, SemaphoreSlim captureSignal, PttState ptt, PttGateState pttGate, AudioDiagnostics diagnostics, TestToneState testTone, TxInjectionState txInjection, TxWaveRecorder recorder, AudioMonitorHub monitorHub, CancellationToken token)
    {
        var samples = new float[FrameSamples];
        var packet = new byte[12 + FrameSamples * sizeof(float)];
        var frame = new byte[4 + packet.Length];
        Magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), packet.Length);
        var sequence = 0u;
        var testTonePhase = 0.0;
        var preRoll = new Queue<float[]>(DefaultPttPreRollFrames + 1);

        while (!token.IsCancellationRequested)
        {
            using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            try
            {
                await client.ConnectAsync(peerHost, peerPort, token);
                preRoll.Clear();
                Console.WriteLine($"TCP audio TX connected to {peerHost}:{peerPort}.");
                using var stream = client.GetStream();

                while (!token.IsCancellationRequested && client.Connected)
                {
                    var filled = 0;
                    while (filled < FrameSamples)
                    {
                        var read = source.Read(samples, filled, FrameSamples - filled);
                        if (read > 0)
                        {
                            filled += read;
                            continue;
                        }

                        diagnostics.RecordCaptureUnderflow();
                        await captureSignal.WaitAsync(token);
                    }

                    diagnostics.UpdateCapture(samples);

                    var gateEnabled = pttGate.Enabled;
                    float[] transmitSamples;
                    if (!gateEnabled)
                    {
                        // GATE OFF is the clean digital-audio diagnostic path. Do not retain the
                        // gated-mode pre-roll delay here; forward the current 5 ms frame immediately.
                        preRoll.Clear();
                        transmitSamples = samples.ToArray();
                    }
                    else
                    {
                        // Delay the captured stream by a few frames before applying the PTT gate.
                        // VARA's documented PTT notification can arrive just after audio has started
                        // (or just before its final samples have drained). The short pre-roll plus
                        // hang time preserves those edge samples in the gated radio simulation.
                        preRoll.Enqueue(samples.ToArray());
                        var preRollFrames = pttGate.PreRollFrames;
                        if (preRoll.Count <= preRollFrames)
                            continue;
                        transmitSamples = preRoll.Dequeue();
                    }

                    var keyed = ptt.IsGateOpen(TimeSpan.FromMilliseconds(pttGate.HangMilliseconds));
                    if (gateEnabled && !keyed)
                        Array.Clear(transmitSamples);
                    else if (testTone.FrequencyHz > 0)
                        testTone.Generate(transmitSamples, ref testTonePhase, SampleRate);

                    // A prepared TX injection replaces only the canonical samples while the normal
                    // PTT remains keyed. The packet's remote-PTT flag therefore follows the same
                    // path as an ordinary modem transmission, while the original captured audio for
                    // these samples is discarded rather than mixed with the replacement.
                    txInjection.Apply(transmitSamples, keyed);

                    diagnostics.UpdateTx(transmitSamples);
                    recorder.Write(transmitSamples);
                    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), sequence++);
                    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8, 2), FrameSamples);
                    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), (!gateEnabled || keyed) ? (ushort)1 : (ushort)0);
                    Buffer.BlockCopy(transmitSamples, 0, packet, 12, FrameSamples * sizeof(float));
                    Buffer.BlockCopy(packet, 0, frame, 4, packet.Length);
                    monitorHub.Publish(frame);
                    await stream.WriteAsync(frame, token);
                    diagnostics.RecordTxPacket(packet.Length);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                Console.WriteLine($"TCP audio TX reconnecting after {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(250, token);
            }
        }
    }

    private static async Task ReceiveLoopAsync(TcpListener listener, BufferedWaveProvider output, ChannelSettings settings, PttState localPtt, PttGateState pttGate, AudioDiagnostics diagnostics, TxWaveRecorder playbackRecorder, AudioMonitorHub rxMonitorHub, CancellationToken token)
    {
        var processor = new ChannelImpairmentProcessor();
        using var ionos = new IonosNativeProcessor();

        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                Console.WriteLine($"TCP audio RX accepted from {client.Client.RemoteEndPoint}.");
                using var stream = client.GetStream();
                var fm = new FmRfChannelProcessor(); // reset FM channel on each new TCP audio session
                uint? lastSequence = null;
                var lengthBytes = new byte[4];

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await stream.ReadExactlyAsync(lengthBytes.AsMemory(), token);
                        var packetLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                        if (packetLength is < 12 or > 64 * 1024)
                            throw new InvalidDataException($"Invalid TCP audio frame length {packetLength}.");

                        var packet = new byte[packetLength];
                        await stream.ReadExactlyAsync(packet.AsMemory(), token);
                        var span = packet.AsSpan();
                        if (!span[..4].SequenceEqual(Magic))
                            continue;

                        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
                        var count = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8, 2));
                        var flags = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10, 2));
                        var remotePtt = (flags & 1) != 0;
                        if (count == 0 || span.Length != 12 + count * sizeof(float))
                            continue;

                        if (lastSequence.HasValue && sequence != lastSequence.Value + 1)
                        {
                            diagnostics.RecordSequenceGap();
                            Console.WriteLine($"TCP audio sequence gap: expected {lastSequence.Value + 1}, got {sequence}");
                        }
                        lastSequence = sequence;

                        var samples = new float[count];
                        Buffer.BlockCopy(packet, 12, samples, 0, count * sizeof(float));
                        diagnostics.RecordRxPacket(packet.Length, remotePtt);
                        diagnostics.UpdateRx(samples);
                        var snapshot = settings.Snapshot;
                        if (pttGate.Enabled && !remotePtt)
                        {
                            // Keep the IONOS fading/clock state advancing during
                            // received silence; never play its noise during PTT-off.
                            if (snapshot.IonosMode != "OFF")
                                _ = ionos.Process(samples, snapshot);
                            else if (snapshot.FmMode != "OFF")
                                _ = fm.Process(samples, snapshot, transmitterKeyed: false);
                            Array.Clear(samples);
                        }
                        else
                        {
                            samples = snapshot.FmMode != "OFF"
                                ? fm.Process(samples, snapshot, transmitterKeyed: !pttGate.Enabled || remotePtt)
                                : snapshot.IonosMode != "OFF"
                                    ? ionos.Process(samples, snapshot)
                                    : processor.Process(samples, snapshot);
                        }
                        diagnostics.UpdatePlayback(samples);
                        playbackRecorder.Write(samples);

                        // Publish the exact post-gate, post-impairment PCM that is about to be
                        // played into official VARA RX. This read-only stream lets CQWave shadow
                        // the official receiver without changing the live session path.
                        Buffer.BlockCopy(samples, 0, packet, 12, count * sizeof(float));
                        var rxFrame = new byte[4 + packet.Length];
                        BinaryPrimitives.WriteInt32LittleEndian(rxFrame.AsSpan(0, 4), packet.Length);
                        Buffer.BlockCopy(packet, 0, rxFrame, 4, packet.Length);
                        rxMonitorHub.Publish(rxFrame);

                        // The two Windows audio clocks are independent. Over long runs a small clock
                        // mismatch can accumulate more than a second of queued receive audio, which
                        // pushes otherwise-valid responder bursts into the peer's retry window. When
                        // the remote endpoint is not keyed and gating is enabled, these samples are
                        // known idle silence, so it is safe to discard stale queued silence before the
                        // next keyed burst. Never trim while remote PTT is active.
                        double bufferedMs = 1000.0 * output.BufferedBytes / output.WaveFormat.AverageBytesPerSecond;
                        if (!remotePtt && pttGate.Enabled && bufferedMs > 50.0)
                            output.ClearBuffer();

                        var bytes = new byte[count * sizeof(float)];
                        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                        output.AddSamples(bytes, 0, bytes.Length);
                    }
                }
                catch (EndOfStreamException)
                {
                    Console.WriteLine("TCP audio RX disconnected; waiting for reconnect.");
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"TCP audio RX reconnecting after IOException: {ex.Message}");
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task SerialPttLoopAsync(string portName, bool invert, PttState ptt, CancellationToken token)
    {
        using var port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 100,
            WriteTimeout = 100
        };

        port.Open();
        Console.WriteLine($"PTT monitor open on {portName}; expecting peer RTS -> local CTS.");

        bool? last = null;
        while (!token.IsCancellationRequested)
        {
            var current = port.CtsHolding;
            if (invert)
                current = !current;

            if (last != current)
            {
                ptt.Transmitting = current;
                Console.WriteLine($"{DateTimeOffset.UtcNow:O} PTT {(current ? "ON" : "OFF")} ({portName} CTS)");
                last = current;
            }

            await Task.Delay(5, token);
        }
    }

    private static async Task ControlLoopAsync(int port, bool localOnly, ChannelSettings settings, PttState ptt, PttGateState pttGate, AudioDiagnostics diagnostics, TestToneState testTone, TxInjectionState txInjection, TxWaveRecorder recorder, TxWaveRecorder playbackRecorder, BufferedWaveProvider outputBuffer, AudioMonitorHub txMonitorHub, AudioMonitorHub rxMonitorHub, CancellationToken token)
    {
        var listener = new TcpListener(localOnly ? IPAddress.Loopback : IPAddress.Any, port);
        listener.Start();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => HandleControlClientAsync(client, settings, ptt, pttGate, diagnostics, testTone, txInjection, recorder, playbackRecorder, outputBuffer, txMonitorHub, rxMonitorHub, token), token);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleControlClientAsync(TcpClient client, ChannelSettings settings, PttState ptt, PttGateState pttGate, AudioDiagnostics diagnostics, TestToneState testTone, TxInjectionState txInjection, TxWaveRecorder recorder, TxWaveRecorder playbackRecorder, BufferedWaveProvider outputBuffer, AudioMonitorHub txMonitorHub, AudioMonitorHub rxMonitorHub, CancellationToken token)
    {
        using (client)
        using (var reader = new StreamReader(client.GetStream(), Encoding.ASCII, false, 1024, true))
        using (var writer = new StreamWriter(client.GetStream(), Encoding.ASCII, 1024, true) { AutoFlush = true })
        {
            MemoryStream? injectionBuffer = null;
            int injectionExpectedSamples = 0;
            MemoryStream? txInjectionBuffer = null;
            int txInjectionExpectedSamples = 0;
            await writer.WriteLineAsync("OK VAFM-AUDIO-NODE");
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null)
                    break;
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                if (parts[0].Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                {
                    var snapshot = settings.Snapshot;
                    var legacyPrefix = $"GAIN {snapshot.GainDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"SNR {(double.IsPositiveInfinity(snapshot.SnrDb) ? "OFF" : snapshot.SnrDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))} " +
                        $"PTT {(ptt.Transmitting ? "ON" : "OFF")} GATE {(pttGate.Enabled ? "ON" : "OFF")}";
                    var extended = snapshot.ToStatusFields();
                    var gainFieldLength = extended.IndexOf(" SEED ", StringComparison.Ordinal);
                    var appended = gainFieldLength >= 0 ? extended[(gainFieldLength + 1)..] : extended;
                    await writer.WriteLineAsync(
                        $"{legacyPrefix} {appended} PREROLL_MS {pttGate.PreRollFrames * 5} HANG_MS {pttGate.HangMilliseconds}");
                }
                else if (parts[0].Equals("DIAG", StringComparison.OrdinalIgnoreCase))
                {
                    var diag = diagnostics.Snapshot;
                    var tone = testTone.FrequencyHz;
                    double playbackBufferedMs = 1000.0 * outputBuffer.BufferedBytes / outputBuffer.WaveFormat.AverageBytesPerSecond;
                    await writer.WriteLineAsync(
                        $"CAP_RMS {diag.CaptureRmsDbfs:F1} CAP_PEAK {diag.CapturePeakDbfs:F1} " +
                        $"TX_RMS {diag.TxRmsDbfs:F1} RX_RMS {diag.RxRmsDbfs:F1} PLAY_RMS {diag.PlaybackRmsDbfs:F1} PLAYBUF_MS {playbackBufferedMs:F1} " +
                        $"TXPKT {diag.TxPackets} TX_FPS {diag.TxPacketsPerSecond:F1} RXPKT {diag.RxPackets} GAPS {diag.SequenceGaps} CAP_UFLOW {diag.CaptureUnderflows} " +
                        $"REMOTE_PTT {(diag.RemotePtt ? "ON" : "OFF")} TESTTONE {(tone > 0 ? tone.ToString("F0") : "OFF")} " +
                        $"GATE {(pttGate.Enabled ? "ON" : "OFF")} PREROLL_MS {pttGate.PreRollFrames * 5} HANG_MS {pttGate.HangMilliseconds} " +
                        $"TXMON_SUB {txMonitorHub.SubscriberCount} TXMON_PUB {txMonitorHub.PublishedFrameCount} " +
                        $"RXMON_SUB {rxMonitorHub.SubscriberCount} RXMON_PUB {rxMonitorHub.PublishedFrameCount}");
                }
                else if (parts[0].Equals("RECORD", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                {
                    if (parts[1].Equals("START", StringComparison.OrdinalIgnoreCase))
                    {
                        var tag = parts.Length >= 3 ? string.Join('-', parts.Skip(2)) : "session";
                        try
                        {
                            var started = recorder.Start(tag);
                            await writer.WriteLineAsync($"OK RECORDING {started.Path} UTC {started.StartedUtc:O}");
                        }
                        catch (InvalidOperationException ex)
                        {
                            await writer.WriteLineAsync($"ERR RECORD {ex.Message}");
                        }
                    }
                    else if (parts[1].Equals("STOP", StringComparison.OrdinalIgnoreCase))
                    {
                        var stopped = recorder.Stop();
                        await writer.WriteLineAsync(stopped is null
                            ? "OK RECORD OFF"
                            : $"OK STOPPED {stopped.Value.Path} SAMPLES {stopped.Value.Samples} SECONDS {stopped.Value.Seconds:F3}");
                    }
                    else if (parts[1].Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                    {
                        var status = recorder.Status;
                        await writer.WriteLineAsync(status.IsRecording
                            ? $"RECORD ON {status.Path} SAMPLES {status.Samples} UTC {status.StartedUtc:O}"
                            : "RECORD OFF");
                    }
                    else if (parts[1].Equals("FETCH", StringComparison.OrdinalIgnoreCase))
                    {
                        var completed = recorder.LastCompleted;
                        if (completed is null)
                        {
                            await writer.WriteLineAsync("ERR RECORD NO-COMPLETED-CAPTURE");
                        }
                        else
                        {
                            await SendRecordingAsync(writer, completed.Value, token);
                        }
                    }
                    else
                    {
                        await writer.WriteLineAsync("ERR RECORD");
                    }
                }
                else if (parts[0].Equals("RECORDRX", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                {
                    if (parts[1].Equals("START", StringComparison.OrdinalIgnoreCase))
                    {
                        var tag = parts.Length >= 3 ? string.Join('-', parts.Skip(2)) : "session";
                        try
                        {
                            var started = playbackRecorder.Start(tag);
                            await writer.WriteLineAsync($"OK RECORDING {started.Path} UTC {started.StartedUtc:O}");
                        }
                        catch (InvalidOperationException ex)
                        {
                            await writer.WriteLineAsync($"ERR RECORDRX {ex.Message}");
                        }
                    }
                    else if (parts[1].Equals("STOP", StringComparison.OrdinalIgnoreCase))
                    {
                        var stopped = playbackRecorder.Stop();
                        await writer.WriteLineAsync(stopped is null
                            ? "OK RECORDRX OFF"
                            : $"OK STOPPED {stopped.Value.Path} SAMPLES {stopped.Value.Samples} SECONDS {stopped.Value.Seconds:F3}");
                    }
                    else if (parts[1].Equals("FETCH", StringComparison.OrdinalIgnoreCase))
                    {
                        var completed = playbackRecorder.LastCompleted;
                        if (completed is null)
                            await writer.WriteLineAsync("ERR RECORDRX NO-COMPLETED-CAPTURE");
                        else
                            await SendRecordingAsync(writer, completed.Value, token);
                    }
                    else
                    {
                        await writer.WriteLineAsync("ERR RECORDRX");
                    }
                }
                else if (parts[0].Equals("TESTTONE", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                {
                    if (parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                    {
                        testTone.FrequencyHz = 0;
                        await writer.WriteLineAsync("OK");
                    }
                    else if (double.TryParse(parts[1], out var toneHz) && toneHz is >= 100 and <= 5000)
                    {
                        testTone.FrequencyHz = toneHz;
                        await writer.WriteLineAsync("OK");
                    }
                    else
                    {
                        await writer.WriteLineAsync("ERR TESTTONE");
                    }
                }
                else if (parts[0].Equals("TXINJECT", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                {
                    if (parts[1].Equals("START", StringComparison.OrdinalIgnoreCase)
                        && parts.Length == 3
                        && int.TryParse(parts[2], out var expectedSamples)
                        && expectedSamples is > 0 and <= SampleRate * MaximumInjectionSeconds)
                    {
                        txInjectionBuffer?.Dispose();
                        txInjectionExpectedSamples = expectedSamples;
                        txInjectionBuffer = new MemoryStream(expectedSamples * sizeof(float));
                        await writer.WriteLineAsync($"OK TXINJECT START SAMPLES {expectedSamples}");
                    }
                    else if (parts[1].Equals("DATA", StringComparison.OrdinalIgnoreCase) && parts.Length == 3)
                    {
                        if (txInjectionBuffer is null)
                        {
                            await writer.WriteLineAsync("ERR TXINJECT NO-START");
                            continue;
                        }
                        try
                        {
                            var chunk = Convert.FromBase64String(parts[2]);
                            if (txInjectionBuffer.Length + chunk.Length > txInjectionExpectedSamples * sizeof(float))
                            {
                                txInjectionBuffer.Dispose();
                                txInjectionBuffer = null;
                                txInjectionExpectedSamples = 0;
                                await writer.WriteLineAsync("ERR TXINJECT TOO-MUCH-DATA");
                                continue;
                            }
                            txInjectionBuffer.Write(chunk, 0, chunk.Length);
                            await writer.WriteLineAsync($"OK TXINJECT DATA BYTES {chunk.Length}");
                        }
                        catch (FormatException)
                        {
                            await writer.WriteLineAsync("ERR TXINJECT BASE64");
                        }
                    }
                    else if (parts[1].Equals("END", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                    {
                        if (txInjectionBuffer is null)
                        {
                            await writer.WriteLineAsync("ERR TXINJECT NO-START");
                            continue;
                        }

                        int expectedBytes = txInjectionExpectedSamples * sizeof(float);
                        if (txInjectionBuffer.Length != expectedBytes)
                        {
                            int actualBytes = checked((int)txInjectionBuffer.Length);
                            txInjectionBuffer.Dispose();
                            txInjectionBuffer = null;
                            txInjectionExpectedSamples = 0;
                            await writer.WriteLineAsync($"ERR TXINJECT LENGTH EXPECTED {expectedBytes} ACTUAL {actualBytes}");
                            continue;
                        }

                        var bytes = txInjectionBuffer.ToArray();
                        txInjection.Prepare(bytes);
                        int sampleCount = txInjectionExpectedSamples;
                        txInjectionBuffer.Dispose();
                        txInjectionBuffer = null;
                        txInjectionExpectedSamples = 0;
                        await writer.WriteLineAsync($"OK TXINJECT PREPARED SAMPLES {sampleCount}");
                    }
                    else if (parts[1].Equals("COMMIT", StringComparison.OrdinalIgnoreCase)
                        && (parts.Length == 2 || parts.Length == 3))
                    {
                        int delaySamples = 0;
                        bool startOnSignal = false;
                        if (parts.Length == 3)
                        {
                            if (parts[2].Equals("SIGNAL", StringComparison.OrdinalIgnoreCase))
                            {
                                startOnSignal = true;
                            }
                            else if (!int.TryParse(parts[2], out delaySamples) ||
                                     delaySamples < 0 ||
                                     delaySamples > SampleRate * MaximumInjectionSeconds)
                            {
                                await writer.WriteLineAsync("ERR TXINJECT DELAY");
                                continue;
                            }
                        }

                        int sampleCount = txInjection.Commit(delaySamples, startOnSignal);
                        await writer.WriteLineAsync($"OK TXINJECT COMMIT SAMPLES {sampleCount}");
                    }
                    else if (parts[1].Equals("ABORT", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                    {
                        txInjectionBuffer?.Dispose();
                        txInjectionBuffer = null;
                        txInjectionExpectedSamples = 0;
                        txInjection.Abort();
                        await writer.WriteLineAsync("OK TXINJECT ABORT");
                    }
                    else
                    {
                        await writer.WriteLineAsync("ERR TXINJECT");
                    }
                }
                else if (parts[0].Equals("INJECT", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                {
                    if (parts[1].Equals("START", StringComparison.OrdinalIgnoreCase)
                        && parts.Length == 3
                        && int.TryParse(parts[2], out var expectedSamples)
                        && expectedSamples is > 0 and <= SampleRate * MaximumInjectionSeconds)
                    {
                        injectionBuffer?.Dispose();
                        injectionExpectedSamples = expectedSamples;
                        injectionBuffer = new MemoryStream(expectedSamples * sizeof(float));
                        await writer.WriteLineAsync($"OK INJECT START SAMPLES {expectedSamples}");
                    }
                    else if (parts[1].Equals("DATA", StringComparison.OrdinalIgnoreCase) && parts.Length == 3)
                    {
                        if (injectionBuffer is null)
                        {
                            await writer.WriteLineAsync("ERR INJECT NO-START");
                            continue;
                        }
                        try
                        {
                            var chunk = Convert.FromBase64String(parts[2]);
                            if (injectionBuffer.Length + chunk.Length > injectionExpectedSamples * sizeof(float))
                            {
                                injectionBuffer.Dispose();
                                injectionBuffer = null;
                                injectionExpectedSamples = 0;
                                await writer.WriteLineAsync("ERR INJECT TOO-MUCH-DATA");
                                continue;
                            }
                            injectionBuffer.Write(chunk, 0, chunk.Length);
                            await writer.WriteLineAsync($"OK INJECT DATA BYTES {chunk.Length}");
                        }
                        catch (FormatException)
                        {
                            await writer.WriteLineAsync("ERR INJECT BASE64");
                        }
                    }
                    else if (parts[1].Equals("END", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                    {
                        if (injectionBuffer is null)
                        {
                            await writer.WriteLineAsync("ERR INJECT NO-START");
                            continue;
                        }
                        int expectedBytes = injectionExpectedSamples * sizeof(float);
                        if (injectionBuffer.Length != expectedBytes)
                        {
                            int actualBytes = checked((int)injectionBuffer.Length);
                            injectionBuffer.Dispose();
                            injectionBuffer = null;
                            injectionExpectedSamples = 0;
                            await writer.WriteLineAsync($"ERR INJECT LENGTH EXPECTED {expectedBytes} ACTUAL {actualBytes}");
                            continue;
                        }

                        var bytes = injectionBuffer.ToArray();
                        outputBuffer.AddSamples(bytes, 0, bytes.Length);
                        int sampleCount = injectionExpectedSamples;
                        injectionBuffer.Dispose();
                        injectionBuffer = null;
                        injectionExpectedSamples = 0;
                        await writer.WriteLineAsync($"OK INJECT SAMPLES {sampleCount} SECONDS {sampleCount / (double)SampleRate:F3}");
                    }
                    else if (parts[1].Equals("ABORT", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                    {
                        injectionBuffer?.Dispose();
                        injectionBuffer = null;
                        injectionExpectedSamples = 0;
                        await writer.WriteLineAsync("OK INJECT ABORT");
                    }
                    else
                    {
                        await writer.WriteLineAsync("ERR INJECT");
                    }
                }
                else if (parts[0].Equals("GAIN", StringComparison.OrdinalIgnoreCase) && parts.Length == 2 && TryParseNumber(parts[1], out var gain))
                {
                    await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { GainDb = gain }));
                }
                else if (parts[0].Equals("SNR", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                {
                    if (parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { SnrDb = double.PositiveInfinity }));
                    else if (TryParseNumber(parts[1], out var snr))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { SnrDb = snr }));
                    else
                        await writer.WriteLineAsync("ERR SNR INVALID-NUMBER");
                }
                else if (parts[0].Equals("SEED", StringComparison.OrdinalIgnoreCase) && parts.Length == 2 && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seed))
                {
                    await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { NoiseSeed = seed }));
                }
                else if (parts[0].Equals("CLIP", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                {
                    if (parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { ClipLevel = 1.0 }));
                    else if (TryParseNumber(parts[1], out var clip))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { ClipLevel = clip }));
                    else
                        await writer.WriteLineAsync("ERR CLIP INVALID-NUMBER");
                }
                else if (parts[0].Equals("DC", StringComparison.OrdinalIgnoreCase) && parts.Length == 2 && TryParseNumber(parts[1], out var dc))
                {
                    await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { DcOffset = dc }));
                }
                else if (parts[0].Equals("BANDPASS", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length == 2 && parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { HighPassHz = 0.0, LowPassHz = 0.0 }));
                    else if (parts.Length == 3 && TryParseNumber(parts[1], out var highPass) && TryParseNumber(parts[2], out var lowPass))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { HighPassHz = highPass, LowPassHz = lowPass }));
                    else
                        await writer.WriteLineAsync("ERR BANDPASS EXPECTED-OFF-OR-HIGHHZ-LOWHZ");
                }
                else if (parts[0].Equals("CLOCK", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length == 2 && parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { ClockPpm = 0.0, ClockDriftPpmPerSecond = 0.0 }));
                    else if ((parts.Length == 2 || parts.Length == 3) && TryParseNumber(parts[1], out var clockPpm) &&
                        (parts.Length == 2 || TryParseNumber(parts[2], out _)))
                    {
                        var drift = parts.Length == 3 && TryParseNumber(parts[2], out var parsedDrift) ? parsedDrift : 0.0;
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { ClockPpm = clockPpm, ClockDriftPpmPerSecond = drift }));
                    }
                    else
                        await writer.WriteLineAsync("ERR CLOCK EXPECTED-OFF-OR-PPM-[DRIFT_PPM_PER_SECOND]");
                }
                else if (parts[0].Equals("DROPOUT", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length == 2 && parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { DropoutRatePerMinute = 0.0, DropoutDurationMs = 0.0 }));
                    else if (parts.Length == 3 && TryParseNumber(parts[1], out var rate) && TryParseNumber(parts[2], out var duration))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { DropoutRatePerMinute = rate, DropoutDurationMs = duration }));
                    else
                        await writer.WriteLineAsync("ERR DROPOUT EXPECTED-OFF-OR-RATE_PER_MIN-DURATION_MS");
                }
                else if (parts[0].Equals("IMPULSE", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length == 2 && parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { ImpulseRatePerMinute = 0.0, ImpulseAmplitude = 0.0 }));
                    else if (parts.Length == 3 && TryParseNumber(parts[1], out var rate) && TryParseNumber(parts[2], out var amplitude))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { ImpulseRatePerMinute = rate, ImpulseAmplitude = amplitude }));
                    else
                        await writer.WriteLineAsync("ERR IMPULSE EXPECTED-OFF-OR-RATE_PER_MIN-AMPLITUDE");
                }
                else if (parts[0].Equals("FADE", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length == 2 && parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { FadeDepthDb = 0.0, FadeRateHz = 0.0 }));
                    else if (parts.Length == 3 && TryParseNumber(parts[1], out var fadeDepth) && TryParseNumber(parts[2], out var fadeRate))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { FadeDepthDb = fadeDepth, FadeRateHz = fadeRate }));
                    else
                        await writer.WriteLineAsync("ERR FADE EXPECTED-OFF-OR-DEPTH_DB-RATE_HZ");
                }
                else if (parts[0].Equals("ECHO", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length == 2 && parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { EchoDelayMs = 0.0, EchoGainDb = 0.0 }));
                    else if (parts.Length == 3 && TryParseNumber(parts[1], out var delay) && TryParseNumber(parts[2], out var echoGain))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { EchoDelayMs = delay, EchoGainDb = echoGain }));
                    else
                        await writer.WriteLineAsync("ERR ECHO EXPECTED-OFF-OR-DELAY_MS-GAIN_DB");
                }
                else if (parts[0].Equals("PROFILE", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                {
                    await writer.WriteLineAsync(TryApplyProfile(settings, parts[1]));
                }
                else if (parts[0].Equals("IONOS", StringComparison.OrdinalIgnoreCase) && parts.Length == 3)
                {
                    if (parts[1].Equals("PATHS", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(parts[2], out var paths))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { IonosPaths = paths }));
                    else if (parts[1].Equals("OFFSET", StringComparison.OrdinalIgnoreCase) &&
                        TryParseNumber(parts[2], out var offsetHz))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, value => value with { IonosOffsetHz = offsetHz }));
                    else
                        await writer.WriteLineAsync("ERR IONOS EXPECTED PATHS 2|4 OR OFFSET -200..200");
                }
                else if (parts[0].Equals("FM", StringComparison.OrdinalIgnoreCase) && parts.Length == 3)
                {
                    if (parts[1].Equals("SPEED", StringComparison.OrdinalIgnoreCase) && TryParseNumber(parts[2], out var kmh))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, s => s with { FmSpeedKmh = kmh }));
                    else if (parts[1].Equals("SNR", StringComparison.OrdinalIgnoreCase))
                    {
                        if (parts[2].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                            await writer.WriteLineAsync(TryUpdateChannel(settings, s => s with { FmRfSnrDb = double.PositiveInfinity }));
                        else if (TryParseNumber(parts[2], out var rfSnr))
                            await writer.WriteLineAsync(TryUpdateChannel(settings, s => s with { FmRfSnrDb = rfSnr }));
                        else
                            await writer.WriteLineAsync("ERR FM SNR INVALID");
                    }
                    else if (parts[1].Equals("SQUELCH", StringComparison.OrdinalIgnoreCase) && TryParseNumber(parts[2], out var sql))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, s => s with { FmSquelchDb = sql }));
                    else if (parts[1].Equals("PTT", StringComparison.OrdinalIgnoreCase) && TryParseNumber(parts[2], out var pttMs))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, s => s with { FmPttAttackMs = pttMs }));
                    else if (parts[1].Equals("INTERFERER", StringComparison.OrdinalIgnoreCase) && TryParseNumber(parts[2], out var jammerDb))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, s => s with { FmInterfererDb = jammerDb }));
                    else if (parts[1].Equals("SHADOW", StringComparison.OrdinalIgnoreCase) && TryParseNumber(parts[2], out var shadowDb))
                        await writer.WriteLineAsync(TryUpdateChannel(settings, s => s with { FmShadowDb = shadowDb }));
                    else
                        await writer.WriteLineAsync("ERR FM EXPECTED SPEED|SNR|SQUELCH|PTT|INTERFERER|SHADOW VALUE");
                }
                else if (parts[0].Equals("CHANNEL", StringComparison.OrdinalIgnoreCase) && parts.Length == 2 && parts[1].Equals("JSON", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(settings.Snapshot, RecordingChannelMetadata.CompactJsonOptions));
                }
                else if (parts[0].Equals("GATETIMING", StringComparison.OrdinalIgnoreCase) && parts.Length == 3)
                {
                    if (int.TryParse(parts[1], out var preRollMs)
                        && int.TryParse(parts[2], out var hangMs)
                        && preRollMs is >= 0 and <= 100
                        && preRollMs % 5 == 0
                        && hangMs is >= 0 and <= 1000)
                    {
                        pttGate.PreRollFrames = preRollMs / 5;
                        pttGate.HangMilliseconds = hangMs;
                        await writer.WriteLineAsync("OK");
                    }
                    else
                    {
                        await writer.WriteLineAsync("ERR GATETIMING EXPECTED-PREROLL_MS_MULTIPLE_OF_5_0_TO_100-HANG_MS_0_TO_1000");
                    }
                }
                else if (parts[0].Equals("GATE", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                {
                    if (parts[1].Equals("ON", StringComparison.OrdinalIgnoreCase))
                    {
                        pttGate.Enabled = true;
                        await writer.WriteLineAsync("OK");
                    }
                    else if (parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                    {
                        pttGate.Enabled = false;
                        await writer.WriteLineAsync("OK");
                    }
                    else
                    {
                        await writer.WriteLineAsync("ERR GATE");
                    }
                }
                else if (parts[0].Equals("PTT", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                {
                    if (parts[1].Equals("ON", StringComparison.OrdinalIgnoreCase))
                    {
                        ptt.Transmitting = true;
                        // Do not synchronously write to Console here.  On a long-running
                        // scheduled-task instance the stdout sink can block; that used to
                        // prevent the control reply from being sent and wedged LabController.
                        await writer.WriteLineAsync("OK");
                    }
                    else if (parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                    {
                        ptt.Transmitting = false;
                        // Same rule for OFF: the control protocol acknowledgement must not
                        // depend on a potentially blocked console/stdout handle.
                        await writer.WriteLineAsync("OK");
                    }
                    else
                    {
                        await writer.WriteLineAsync("ERR PTT");
                    }
                }
                else
                {
                    await writer.WriteLineAsync("ERR COMMAND");
                }
            }
        }
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

    private static string TryUpdateChannel(ChannelSettings settings, Func<ChannelSnapshot, ChannelSnapshot> update)
    {
        try
        {
            settings.Update(update);
            return "OK";
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return $"ERR CHANNEL {ex.Message.Replace(Environment.NewLine, " ", StringComparison.Ordinal)}";
        }
    }

    private static string TryApplyProfile(ChannelSettings settings, string profile)
    {
        try
        {
            var resolved = ChannelProfiles.Resolve(profile);
            if (resolved.IonosMode != "OFF")
                IonosNativeProcessor.AssertAvailable();
            settings.ApplyProfile(profile);
            return "OK";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"ERR PROFILE {ex.Message.Replace(Environment.NewLine, " ", StringComparison.Ordinal)}";
        }
    }

    private static MMDevice FindDevice(MMDeviceCollection devices, string selector, string kind)
    {
        var byId = devices.FirstOrDefault(d => d.ID.Equals(selector, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
            return byId;

        var matches = devices.Where(d => d.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"No active {kind} device matches '{selector}'. Use --list-devices."),
            _ => throw new ArgumentException($"More than one {kind} device matches '{selector}'. Use the exact device ID from --list-devices.")
        };
    }

    private static void ListDevices(MMDeviceEnumerator enumerator)
    {
        Console.WriteLine("Windows recording devices (capture modem TX from a virtual cable Output side):");
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            Console.WriteLine($"NAME: {device.FriendlyName}");
            Console.WriteLine($"ID:   {device.ID}");
            Console.WriteLine();
        }

        Console.WriteLine("Windows playback devices (feed modem RX using a virtual cable Input side):");
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            Console.WriteLine($"NAME: {device.FriendlyName}");
            Console.WriteLine($"ID:   {device.ID}");
            Console.WriteLine();
        }
    }

    private static Options RunSetup(MMDeviceEnumerator enumerator)
    {
        Console.WriteLine("FakeRadio — first-run audio bridge setup (software only; no physical RF)");
        Console.WriteLine();
        var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToArray();
        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();

        Console.WriteLine("TX capture / recording devices:");
        for (var i = 0; i < captureDevices.Length; i++)
            Console.WriteLine($"  [{i + 1}] {captureDevices[i].FriendlyName}");
        Console.WriteLine();
        var txIndex = PromptIndex("Choose the virtual recording device carrying your modem's TX audio (cable Output side)", captureDevices.Length);

        Console.WriteLine();
        Console.WriteLine("RX playback devices:");
        for (var i = 0; i < renderDevices.Length; i++)
            Console.WriteLine($"  [{i + 1}] {renderDevices[i].FriendlyName}");
        Console.WriteLine();
        var rxIndex = PromptIndex("Choose the virtual playback device feeding your modem RX (cable Input side)", renderDevices.Length);
        var peer = Prompt("Peer fake-radio IP or hostname");
        var peerPort = PromptInt("Peer TCP audio port", 19400);
        var listenPort = PromptInt("Local TCP audio port", 19400);
        var controlPort = PromptInt("Local TCP control port", 19401);
        var monitorPort = PromptInt("Local read-only TX monitor port", 19402);
        var rxMonitorPort = PromptInt("Local read-only post-impairment RX monitor port", 19403);
        Console.Write("Allow control commands from other computers? (private lab LAN only) [y/N]: ");
        var remoteControl = Console.ReadLine()?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true;
        Console.WriteLine("Channel presets (can change while running using PROFILE <name>):");
        Console.WriteLine("  " + string.Join("  ", ChannelProfiles.Names));
        Console.Write("Initial profile [CLEAN]: ");
        var chosenProfile = Console.ReadLine()?.Trim();
        var channelProfile = ChannelProfiles.Resolve(string.IsNullOrWhiteSpace(chosenProfile) ? "CLEAN" : chosenProfile);

        Console.Write("Use TNC-controlled PTT (recommended)? [Y/n]: ");
        var external = Console.ReadLine();
        var externalPtt = string.IsNullOrWhiteSpace(external) || !external.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase);
        string? pttPort = null;
        var pttInvert = false;
        if (!externalPtt)
        {
            var ports = SerialPort.GetPortNames().OrderBy(x => x).ToArray();
            Console.WriteLine(ports.Length == 0 ? "No COM ports detected." : $"COM ports: {string.Join(", ", ports)}");
            Console.Write("PTT COM port for CTS monitoring, or blank for always-on: ");
            pttPort = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(pttPort))
                pttPort = null;
        }

        return new Options(
            captureDevices[txIndex].ID,
            renderDevices[rxIndex].ID,
            listenPort,
            peer,
            peerPort,
            controlPort,
            monitorPort,
            rxMonitorPort,
            pttPort,
            pttInvert,
            externalPtt,
            channelProfile,
            250,
            !remoteControl);
    }

    private static int PromptIndex(string prompt, int count)
    {
        while (true)
        {
            Console.Write($"{prompt} [1-{count}]: ");
            if (int.TryParse(Console.ReadLine(), out var value) && value >= 1 && value <= count)
                return value - 1;
            Console.WriteLine("Invalid selection.");
        }
    }

    private static string Prompt(string prompt)
    {
        while (true)
        {
            Console.Write($"{prompt}: ");
            var value = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
    }

    private static int PromptInt(string prompt, int defaultValue)
    {
        while (true)
        {
            Console.Write($"{prompt} [{defaultValue}]: ");
            var value = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            if (int.TryParse(value, out var parsed) && parsed is > 0 and <= 65535)
                return parsed;
            Console.WriteLine("Enter a valid TCP/UDP port.");
        }
    }

    private static void SaveConfig(string path, Options options)
    {
        var config = new ConfigFile(
            options.CaptureDevice,
            options.PlaybackDevice,
            options.ListenPort,
            options.PeerHost,
            options.PeerPort,
            options.ControlPort,
            options.PttPort,
            options.PttInvert,
            options.ExternalPtt,
            options.Channel.GainDb,
            double.IsPositiveInfinity(options.Channel.SnrDb) ? null : options.Channel.SnrDb,
            options.JitterBufferMs,
            ChannelConfigFile.FromSnapshot(options.Channel),
            options.MonitorPort,
            options.RxMonitorPort,
            options.ControlLocalOnly);
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Options LoadConfig(string path)
    {
        var config = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Could not read {path}.");
        var channel = config.Channel?.ToSnapshot() ??
            ChannelSnapshot.Clean(config.GainDb, config.SnrDb ?? double.PositiveInfinity);
        ChannelSettings.Validate(channel);
        return new Options(
            config.CaptureDevice,
            config.PlaybackDevice,
            config.ListenPort,
            config.PeerHost,
            config.PeerPort,
            config.ControlPort,
            config.MonitorPort <= 0 ? 19402 : config.MonitorPort,
            config.RxMonitorPort <= 0 ? 19403 : config.RxMonitorPort,
            config.PttPort,
            config.PttInvert,
            config.ExternalPtt,
            channel,
            config.JitterBufferMs,
            config.ControlLocalOnly ?? false);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("FakeRadio: software-only modem audio channel simulator (Windows/WASAPI)");
        Console.WriteLine();
        Console.WriteLine("Run one node beside each HF, VHF or UHF soundcard modem. It captures modem TX audio");
        Console.WriteLine("from the virtual cable recording endpoint, streams canonical 48 kHz mono float PCM to the peer, and");
        Console.WriteLine("plays the peer audio into a separate virtual playback endpoint feeding modem RX.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --setup                     interactive setup; saves fakeradio.json next to the EXE");
        Console.WriteLine("  --list-devices              list active recording and playback endpoints");
        Console.WriteLine("  --profiles                  list all built-in channel profiles");
        Console.WriteLine("  --capture-device TEXT       modem TX cable recording endpoint name fragment or ID");
        Console.WriteLine("  --playback-device TEXT      virtual playback endpoint that feeds VARA RX");
        Console.WriteLine("  --listen-port PORT          TCP audio listen port (default 19400)");
        Console.WriteLine("  --peer HOST:PORT            peer audio node (required)");
        Console.WriteLine("  --control-port PORT         TCP impairment control (default 19401, local only)");
        Console.WriteLine("  --control-remote            allow unauthenticated control from LAN (firewall required)");
        Console.WriteLine("  --monitor-port PORT         read-only canonical TX PCM monitor (default 19402)");
        Console.WriteLine("  --rx-monitor-port PORT      read-only post-impairment RX PCM monitor (default 19403)");
        Console.WriteLine("  --external-ptt              start unkeyed; accept PTT ON/OFF over control TCP");
        Console.WriteLine("  --ptt-port COMx             optional paired virtual COM port; monitor CTS as modem RTS PTT");
        Console.WriteLine("  --ptt-invert                invert the CTS/PTT sense");
        Console.WriteLine("  --profile NAME              named channel profile; use CLEAN for identity");
        Console.WriteLine("  --ionos-paths 2|4           number of independent IONOS Gaussian paths");
        Console.WriteLine("  --fm-speed-kmh VALUE        mobile speed 0..250 km/h for FM RF profiles");
        Console.WriteLine("  --fm-rf-snr-db VALUE        receiver complex-baseband SNR (-10..90, OFF)");
        Console.WriteLine("  --ionos-offset-hz HZ        IONOS frequency offset, -200..200 Hz");
        Console.WriteLine("  --gain-db DB                receive-channel gain (default 0)");
        Console.WriteLine("  --snr-db DB|OFF             seeded signal-relative white noise (default OFF)");
        Console.WriteLine("  --noise-seed INTEGER        deterministic noise/event seed");
        Console.WriteLine("  --clip-level LINEAR         symmetric output clip level, 0..1 (default 1)");
        Console.WriteLine("  --dc-offset LINEAR          add DC offset, -1..1 (default 0)");
        Console.WriteLine("  --highpass-hz HZ            receive high-pass corner; 0 disables");
        Console.WriteLine("  --lowpass-hz HZ             receive low-pass corner; 0 disables");
        Console.WriteLine("  --clock-ppm PPM             receive sample-clock offset");
        Console.WriteLine("  --clock-drift-ppm-per-second PPM_S");
        Console.WriteLine("  --dropout-rate-per-minute N --dropout-duration-ms MS");
        Console.WriteLine("  --impulse-rate-per-minute N --impulse-amplitude LINEAR");
        Console.WriteLine("  --echo-delay-ms MS          audio echo stress delay; 0 disables");
        Console.WriteLine("  --echo-gain-db DB           delayed-path gain, -120..0 dB");
        Console.WriteLine("  --fade-depth-db DB          signal envelope fade depth, 0..60 dB");
        Console.WriteLine("  --fade-rate-hz HZ           fade cycles per second, 0..20 Hz");
        Console.WriteLine("  --jitter-ms MS              playback buffer capacity (default 250)");
        Console.WriteLine("  --channel-self-test         run deterministic impairment tests and exit");
        Console.WriteLine();
        Console.WriteLine($"Profiles: {string.Join(", ", ChannelProfiles.Names)}");
        Console.WriteLine();
        Console.WriteLine("Channel controls: STATUS, CHANNEL JSON, PROFILE <name>, GAIN <dB>, SNR <dB>|OFF, SEED <int>, CLIP <linear>|OFF, DC <linear>, BANDPASS <highHz> <lowHz>|OFF, CLOCK <ppm> [driftPpm/s]|OFF, DROPOUT <rate/min> <durationMs>|OFF, IMPULSE <rate/min> <amplitude>|OFF, ECHO <delayMs> <gainDb>|OFF, FADE <depthDb> <rateHz>|OFF, IONOS PATHS 2|4, IONOS OFFSET <Hz>, FM SPEED|SNR|SQUELCH|PTT|INTERFERER|SHADOW <value>");
        Console.WriteLine("Other controls: DIAG, RECORD START [tag], RECORD STOP, RECORD STATUS, RECORD FETCH, RECORDRX START [tag], RECORDRX STOP, RECORDRX FETCH, TESTTONE <100-5000|OFF>, TXINJECT START <samples>, TXINJECT DATA <base64-f32le>, TXINJECT END, TXINJECT COMMIT [delay-samples|SIGNAL], TXINJECT ABORT, INJECT START <samples>, INJECT DATA <base64-f32le>, INJECT END, INJECT ABORT, GATE ON|OFF, GATETIMING <preroll-ms> <hang-ms>, PTT ON|OFF");
    }

    private static async Task SendRecordingAsync(StreamWriter writer, RecordingStopped recording, CancellationToken token)
    {
        const int rawChunkBytes = 24 * 1024;
        var info = new FileInfo(recording.Path);
        if (!info.Exists)
        {
            await writer.WriteLineAsync("ERR RECORD FILE-MISSING");
            return;
        }

        byte[] hash;
        await using (var hashStream = File.OpenRead(recording.Path))
            hash = await SHA256.HashDataAsync(hashStream, token);

        var fileName = Path.GetFileName(recording.Path);
        await writer.WriteLineAsync($"FILE BEGIN {fileName} {info.Length} {Convert.ToHexString(hash)}");

        var buffer = new byte[rawChunkBytes];
        await using var input = File.OpenRead(recording.Path);
        while (true)
        {
            var count = await input.ReadAsync(buffer, token);
            if (count == 0)
                break;
            await writer.WriteLineAsync("FILE DATA " + Convert.ToBase64String(buffer, 0, count));
        }

        await writer.WriteLineAsync("FILE END");
    }
}

internal sealed class TxWaveRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly string _captureDirectory;
    private readonly int _sampleRate;
    private readonly string _suffix;
    private readonly ChannelSettings _channelSettings;
    private WaveFileWriter? _writer;
    private string? _path;
    private DateTimeOffset _startedUtc;
    private long _samples;
    private RecordingStopped? _lastCompleted;
    private ChannelSnapshot _startedChannel;

    public TxWaveRecorder(string baseDirectory, int sampleRate, string suffix, ChannelSettings channelSettings)
    {
        _captureDirectory = Path.Combine(baseDirectory, "captures");
        _sampleRate = sampleRate;
        _suffix = suffix;
        _channelSettings = channelSettings;
    }

    public RecordingStatus Start(string tag)
    {
        lock (_gate)
        {
            if (_writer is not null)
                throw new InvalidOperationException("already recording");

            Directory.CreateDirectory(_captureDirectory);
            var safeTag = SanitizeTag(tag);
            _startedUtc = DateTimeOffset.UtcNow;
            _startedChannel = _channelSettings.Snapshot;
            _samples = 0;
            _path = Path.Combine(_captureDirectory, $"{_startedUtc:yyyyMMdd-HHmmssfff}-{safeTag}-{_suffix}.wav");
            _writer = new WaveFileWriter(_path, WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 1));
            return new RecordingStatus(true, _path, _startedUtc, 0);
        }
    }

    public void Write(float[] samples)
    {
        lock (_gate)
        {
            if (_writer is null)
                return;
            _writer.WriteSamples(samples, 0, samples.Length);
            _samples += samples.Length;
        }
    }

    public RecordingStopped? Stop()
    {
        lock (_gate)
        {
            if (_writer is null || _path is null)
                return null;

            var stoppedUtc = DateTimeOffset.UtcNow;
            var stoppedChannel = _channelSettings.Snapshot;
            var path = _path;
            var result = new RecordingStopped(path, _samples, _samples / (double)_sampleRate);
            _writer.Dispose();
            _writer = null;

            var metadata = new RecordingChannelMetadata(
                "fake-radio-channel-v1",
                _suffix == "rx" ? "post-channel-rx" : "pre-channel-tx",
                Path.GetFileName(path),
                _startedUtc,
                stoppedUtc,
                _startedChannel,
                stoppedChannel,
                _channelSettings.ChangesAfter(_startedChannel.Revision));
            File.WriteAllText(path + ".channel.json", JsonSerializer.Serialize(metadata, RecordingChannelMetadata.JsonOptions));

            _path = null;
            _samples = 0;
            _lastCompleted = result;
            return result;
        }
    }

    public RecordingStatus Status
    {
        get
        {
            lock (_gate)
                return _writer is null
                    ? new RecordingStatus(false, string.Empty, default, 0)
                    : new RecordingStatus(true, _path ?? string.Empty, _startedUtc, _samples);
        }
    }

    public RecordingStopped? LastCompleted
    {
        get
        {
            lock (_gate)
                return _lastCompleted;
        }
    }

    public void Dispose() => Stop();

    private static string SanitizeTag(string tag)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = tag.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray();
        var safe = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "session" : safe[..Math.Min(safe.Length, 80)];
    }
}

internal readonly record struct RecordingStatus(bool IsRecording, string Path, DateTimeOffset StartedUtc, long Samples);
internal readonly record struct RecordingStopped(string Path, long Samples, double Seconds);

internal sealed class AudioDiagnostics
{
    private long _captureRmsBits = BitConverter.DoubleToInt64Bits(-120.0);
    private long _capturePeakBits = BitConverter.DoubleToInt64Bits(-120.0);
    private long _txRmsBits = BitConverter.DoubleToInt64Bits(-120.0);
    private long _rxRmsBits = BitConverter.DoubleToInt64Bits(-120.0);
    private long _playbackRmsBits = BitConverter.DoubleToInt64Bits(-120.0);
    private long _txPackets;
    private long _txRateWindowStartTimestamp = Stopwatch.GetTimestamp();
    private long _txRateWindowPackets;
    private long _txPacketsPerSecondBits = BitConverter.DoubleToInt64Bits(0.0);
    private long _rxPackets;
    private long _sequenceGaps;
    private long _captureUnderflows;
    private int _remotePtt;

    public void UpdateCapture(float[] samples)
    {
        var (rms, peak) = MeasureDbfs(samples);
        Interlocked.Exchange(ref _captureRmsBits, BitConverter.DoubleToInt64Bits(rms));
        Interlocked.Exchange(ref _capturePeakBits, BitConverter.DoubleToInt64Bits(peak));
    }

    public void UpdateTx(float[] samples)
    {
        var (rms, _) = MeasureDbfs(samples);
        Interlocked.Exchange(ref _txRmsBits, BitConverter.DoubleToInt64Bits(rms));
    }

    public void UpdateRx(float[] samples)
    {
        var (rms, _) = MeasureDbfs(samples);
        Interlocked.Exchange(ref _rxRmsBits, BitConverter.DoubleToInt64Bits(rms));
    }

    public void UpdatePlayback(float[] samples)
    {
        var (rms, _) = MeasureDbfs(samples);
        Interlocked.Exchange(ref _playbackRmsBits, BitConverter.DoubleToInt64Bits(rms));
    }

    public void RecordTxPacket(int bytes)
    {
        Interlocked.Increment(ref _txPackets);
        long packets = Interlocked.Increment(ref _txRateWindowPackets);
        long now = Stopwatch.GetTimestamp();
        long start = Interlocked.Read(ref _txRateWindowStartTimestamp);
        double elapsedSeconds = (now - start) / (double)Stopwatch.Frequency;
        if (elapsedSeconds < 0.5)
            return;

        double rate = packets / elapsedSeconds;
        Interlocked.Exchange(ref _txPacketsPerSecondBits, BitConverter.DoubleToInt64Bits(rate));
        Interlocked.Exchange(ref _txRateWindowPackets, 0);
        Interlocked.Exchange(ref _txRateWindowStartTimestamp, now);
    }

    public void RecordRxPacket(int bytes, bool remotePtt)
    {
        Interlocked.Increment(ref _rxPackets);
        Volatile.Write(ref _remotePtt, remotePtt ? 1 : 0);
    }

    public void RecordSequenceGap() => Interlocked.Increment(ref _sequenceGaps);

    public void RecordCaptureUnderflow() => Interlocked.Increment(ref _captureUnderflows);

    public AudioDiagnosticsSnapshot Snapshot => new(
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _captureRmsBits)),
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _capturePeakBits)),
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _txRmsBits)),
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _rxRmsBits)),
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _playbackRmsBits)),
        Interlocked.Read(ref _txPackets),
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _txPacketsPerSecondBits)),
        Interlocked.Read(ref _rxPackets),
        Interlocked.Read(ref _sequenceGaps),
        Interlocked.Read(ref _captureUnderflows),
        Volatile.Read(ref _remotePtt) != 0);

    private static (double RmsDbfs, double PeakDbfs) MeasureDbfs(float[] samples)
    {
        if (samples.Length == 0)
            return (-120.0, -120.0);

        double squareSum = 0;
        double peak = 0;
        foreach (var sample in samples)
        {
            var abs = Math.Abs(sample);
            if (abs > peak)
                peak = abs;
            squareSum += sample * sample;
        }

        var rms = Math.Sqrt(squareSum / samples.Length);
        return (ToDbfs(rms), ToDbfs(peak));
    }

    private static double ToDbfs(double value) => value <= 1e-6 ? -120.0 : Math.Max(-120.0, 20.0 * Math.Log10(value));
}

internal readonly record struct AudioDiagnosticsSnapshot(
    double CaptureRmsDbfs,
    double CapturePeakDbfs,
    double TxRmsDbfs,
    double RxRmsDbfs,
    double PlaybackRmsDbfs,
    long TxPackets,
    double TxPacketsPerSecond,
    long RxPackets,
    long SequenceGaps,
    long CaptureUnderflows,
    bool RemotePtt);

internal sealed class TestToneState
{
    private long _frequencyBits;

    public double FrequencyHz
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _frequencyBits));
        set => Interlocked.Exchange(ref _frequencyBits, BitConverter.DoubleToInt64Bits(value));
    }

    public void Generate(float[] samples, ref double phase, int sampleRate)
    {
        var frequency = FrequencyHz;
        if (frequency <= 0)
            return;

        var step = 2.0 * Math.PI * frequency / sampleRate;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.25 * Math.Sin(phase));
            phase += step;
            if (phase >= 2.0 * Math.PI)
                phase -= 2.0 * Math.PI;
        }
    }
}

internal sealed class TxInjectionState
{
    private readonly object _gate = new();
    private float[]? _prepared;
    private int _offset;
    private int _delayRemainingSamples;
    private bool _waitForSignal;
    private bool _active;
    private bool _sawKeyed;

    public void Prepare(byte[] pcmBytes)
    {
        if (pcmBytes.Length == 0 || (pcmBytes.Length % sizeof(float)) != 0)
            throw new ArgumentException("TX injection PCM must be non-empty float32 samples.", nameof(pcmBytes));

        var samples = new float[pcmBytes.Length / sizeof(float)];
        Buffer.BlockCopy(pcmBytes, 0, samples, 0, pcmBytes.Length);
        lock (_gate)
        {
            _prepared = samples;
            _offset = 0;
            _delayRemainingSamples = 0;
            _waitForSignal = false;
            _active = false;
            _sawKeyed = false;
        }
    }

    public int Commit(int delaySamples, bool startOnSignal)
    {
        if (delaySamples < 0)
            throw new ArgumentOutOfRangeException(nameof(delaySamples));

        lock (_gate)
        {
            if (_prepared is null || _prepared.Length == 0)
                throw new InvalidOperationException("No prepared TX injection is available.");
            _offset = 0;
            _delayRemainingSamples = delaySamples;
            _waitForSignal = startOnSignal;
            _active = true;
            _sawKeyed = false;
            return _prepared.Length;
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            _prepared = null;
            _offset = 0;
            _delayRemainingSamples = 0;
            _waitForSignal = false;
            _active = false;
            _sawKeyed = false;
        }
    }

    public bool Apply(float[] destination, bool keyed)
    {
        lock (_gate)
        {
            if (!_active)
                return false;

            if (!keyed)
            {
                if (_sawKeyed)
                    AbortLocked();
                return false;
            }

            _sawKeyed = true;
            int position = 0;

            if (_waitForSignal)
            {
                int signalIndex = -1;
                for (int i = 0; i < destination.Length; i++)
                {
                    if (Math.Abs(destination[i]) > 1e-6f)
                    {
                        signalIndex = i;
                        break;
                    }
                }

                if (signalIndex < 0)
                {
                    Array.Clear(destination);
                    return true;
                }

                if (signalIndex > 0)
                    Array.Clear(destination, 0, signalIndex);
                position = signalIndex;
                _waitForSignal = false;
            }

            if (_delayRemainingSamples > 0)
            {
                int silentPrefix = Math.Min(destination.Length - position, _delayRemainingSamples);
                Array.Clear(destination, position, silentPrefix);
                _delayRemainingSamples -= silentPrefix;
                position += silentPrefix;
                if (position == destination.Length)
                    return true;
            }

            if (_prepared is null)
            {
                Array.Clear(destination, position, destination.Length - position);
                return true;
            }

            int remaining = _prepared.Length - _offset;
            int count = Math.Min(destination.Length - position, remaining);
            Array.Copy(_prepared, _offset, destination, position, count);
            if (position + count < destination.Length)
                Array.Clear(destination, position + count, destination.Length - position - count);
            _offset += count;

            if (_offset >= _prepared.Length)
                _prepared = null;

            return true;
        }
    }

    private void AbortLocked()
    {
        _prepared = null;
        _offset = 0;
        _delayRemainingSamples = 0;
        _waitForSignal = false;
        _active = false;
        _sawKeyed = false;
    }
}

internal sealed class PttGateState
{
    private int _enabled = 1;
    private int _preRollFrames = Program.DefaultPttPreRollFrames;
    private int _hangMilliseconds = Program.DefaultPttHangMilliseconds;

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    public int PreRollFrames
    {
        get => Volatile.Read(ref _preRollFrames);
        set => Volatile.Write(ref _preRollFrames, value);
    }

    public int HangMilliseconds
    {
        get => Volatile.Read(ref _hangMilliseconds);
        set => Volatile.Write(ref _hangMilliseconds, value);
    }
}

internal sealed class PttState
{
    private int _transmitting;
    private long _lastOffTimestamp;

    public PttState(bool initialTransmitting)
    {
        _transmitting = initialTransmitting ? 1 : 0;
        _lastOffTimestamp = 0;
    }

    public bool Transmitting
    {
        get => Volatile.Read(ref _transmitting) != 0;
        set
        {
            Volatile.Write(ref _transmitting, value ? 1 : 0);
            if (!value)
                Volatile.Write(ref _lastOffTimestamp, Stopwatch.GetTimestamp());
        }
    }

    public bool IsGateOpen(TimeSpan hangTime)
    {
        if (Transmitting)
            return true;

        long elapsed = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastOffTimestamp);
        return elapsed >= 0 && elapsed < hangTime.TotalSeconds * Stopwatch.Frequency;
    }
}

internal sealed class AudioMonitorHub
{
    private readonly int _port;
    private readonly bool _localOnly;
    private readonly ConcurrentDictionary<int, Channel<byte[]>> _subscribers = new();
    private int _nextId;
    private long _publishedFrameCount;

    public int SubscriberCount => _subscribers.Count;
    public long PublishedFrameCount => Interlocked.Read(ref _publishedFrameCount);

    public AudioMonitorHub(int port, bool localOnly = false)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
        _localOnly = localOnly;
    }

    public void Publish(ReadOnlySpan<byte> frame)
    {
        if (_subscribers.IsEmpty)
            return;

        Interlocked.Increment(ref _publishedFrameCount);
        byte[] copy = frame.ToArray();
        foreach (var item in _subscribers)
        {
            if (!item.Value.Writer.TryWrite(copy))
            {
                if (_subscribers.TryRemove(item.Key, out var removed))
                    removed.Writer.TryComplete(new IOException("TX monitor subscriber fell behind the live PCM stream."));
            }
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        var listener = new TcpListener(_localOnly ? IPAddress.Loopback : IPAddress.Any, _port);
        listener.Start();
        Console.WriteLine($"Read-only monitor listening on {(_localOnly ? "127.0.0.1" : "0.0.0.0")}:{_port}.");
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                int id = Interlocked.Increment(ref _nextId);
                var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropWrite
                });
                _subscribers[id] = channel;
                Console.WriteLine($"TX monitor subscriber {id} connected from {client.Client.RemoteEndPoint}.");
                _ = Task.Run(() => ServeAsync(id, client, channel, token), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
            foreach (var subscriber in _subscribers.Values)
                subscriber.Writer.TryComplete();
        }
    }

    private async Task ServeAsync(int id, TcpClient client, Channel<byte[]> channel, CancellationToken token)
    {
        using (client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();
                await foreach (byte[] frame in channel.Reader.ReadAllAsync(token))
                    await stream.WriteAsync(frame, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                Console.WriteLine($"TX monitor subscriber {id} disconnected: {ex.Message}");
            }
            finally
            {
                if (_subscribers.TryRemove(id, out var removed))
                    removed.Writer.TryComplete();
            }
        }
    }
}

internal sealed record Options(
    string CaptureDevice,
    string PlaybackDevice,
    int ListenPort,
    string PeerHost,
    int PeerPort,
    int ControlPort,
    int MonitorPort,
    int RxMonitorPort,
    string? PttPort,
    bool PttInvert,
    bool ExternalPtt,
    ChannelSnapshot Channel,
    int JitterBufferMs,
    bool ControlLocalOnly = true)
{
    public static Options Parse(string[] args)
    {
        string? capture = null;
        string? playback = null;
        var listenPort = 19400;
        string? peerHost = null;
        var peerPort = 19400;
        var controlPort = 19401;
        var monitorPort = 19402;
        var rxMonitorPort = 19403;
        string? pttPort = null;
        var pttInvert = false;
        var externalPtt = false;
        var channel = ChannelSnapshot.Clean();
        var jitter = 250;
        var controlLocalOnly = true;

        static double Number(string text) => double.Parse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
        static int Integer(string text) => int.Parse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {args[i]}");
            switch (args[i])
            {
                case "--capture-device": capture = Next(); break;
                case "--playback-device": playback = Next(); break;
                case "--listen-port": listenPort = Integer(Next()); break;
                case "--peer":
                {
                    var peer = Next();
                    var split = peer.LastIndexOf(':');
                    if (split <= 0 || !int.TryParse(peer[(split + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out peerPort))
                        throw new ArgumentException("--peer must be HOST:PORT");
                    peerHost = peer[..split];
                    break;
                }
                case "--control-port": controlPort = Integer(Next()); break;
                case "--control-remote": controlLocalOnly = false; break;
                case "--monitor-port": monitorPort = Integer(Next()); break;
                case "--rx-monitor-port": rxMonitorPort = Integer(Next()); break;
                case "--external-ptt": externalPtt = true; break;
                case "--ptt-port": pttPort = Next(); break;
                case "--ptt-invert": pttInvert = true; break;
                case "--profile": channel = ChannelProfiles.Resolve(Next()); break;
                case "--ionos-paths": channel = channel with { IonosPaths = Integer(Next()) }; break;
                case "--fm-speed-kmh": channel = channel with { FmSpeedKmh = Number(Next()) }; break;
                case "--fm-rf-snr-db":
                {
                    var value = Next();
                    channel = channel with { FmRfSnrDb = value.Equals("OFF", StringComparison.OrdinalIgnoreCase) ? double.PositiveInfinity : Number(value) };
                    break;
                }
                case "--ionos-offset-hz": channel = channel with { IonosOffsetHz = Number(Next()) }; break;
                case "--gain-db": channel = channel with { GainDb = Number(Next()), Profile = "CUSTOM" }; break;
                case "--snr-db":
                {
                    var value = Next();
                    channel = channel with
                    {
                        SnrDb = value.Equals("OFF", StringComparison.OrdinalIgnoreCase) ? double.PositiveInfinity : Number(value),
                        Profile = "CUSTOM"
                    };
                    break;
                }
                case "--noise-seed": channel = channel with { NoiseSeed = Integer(Next()), Profile = "CUSTOM" }; break;
                case "--clip-level": channel = channel with { ClipLevel = Number(Next()), Profile = "CUSTOM" }; break;
                case "--dc-offset": channel = channel with { DcOffset = Number(Next()), Profile = "CUSTOM" }; break;
                case "--highpass-hz": channel = channel with { HighPassHz = Number(Next()), Profile = "CUSTOM" }; break;
                case "--lowpass-hz": channel = channel with { LowPassHz = Number(Next()), Profile = "CUSTOM" }; break;
                case "--clock-ppm": channel = channel with { ClockPpm = Number(Next()), Profile = "CUSTOM" }; break;
                case "--clock-drift-ppm-per-second": channel = channel with { ClockDriftPpmPerSecond = Number(Next()), Profile = "CUSTOM" }; break;
                case "--dropout-rate-per-minute": channel = channel with { DropoutRatePerMinute = Number(Next()), Profile = "CUSTOM" }; break;
                case "--dropout-duration-ms": channel = channel with { DropoutDurationMs = Number(Next()), Profile = "CUSTOM" }; break;
                case "--impulse-rate-per-minute": channel = channel with { ImpulseRatePerMinute = Number(Next()), Profile = "CUSTOM" }; break;
                case "--impulse-amplitude": channel = channel with { ImpulseAmplitude = Number(Next()), Profile = "CUSTOM" }; break;
                case "--echo-delay-ms": channel = channel with { EchoDelayMs = Number(Next()), Profile = "CUSTOM" }; break;
                case "--echo-gain-db": channel = channel with { EchoGainDb = Number(Next()), Profile = "CUSTOM" }; break;
                case "--fade-depth-db": channel = channel with { FadeDepthDb = Number(Next()), Profile = "CUSTOM" }; break;
                case "--fade-rate-hz": channel = channel with { FadeRateHz = Number(Next()), Profile = "CUSTOM" }; break;
                case "--jitter-ms": jitter = Integer(Next()); break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(capture) || string.IsNullOrWhiteSpace(playback) || string.IsNullOrWhiteSpace(peerHost))
            throw new ArgumentException("--capture-device, --playback-device, and --peer are required. Use --help.");

        ChannelSettings.Validate(channel);
        return new Options(capture, playback, listenPort, peerHost, peerPort, controlPort, monitorPort, rxMonitorPort, pttPort, pttInvert, externalPtt, channel, jitter, controlLocalOnly);
    }
}

internal sealed record ConfigFile(
    string CaptureDevice,
    string PlaybackDevice,
    int ListenPort,
    string PeerHost,
    int PeerPort,
    int ControlPort,
    string? PttPort,
    bool PttInvert,
    bool ExternalPtt,
    double GainDb,
    double? SnrDb,
    int JitterBufferMs,
    ChannelConfigFile? Channel = null,
    int MonitorPort = 19402,
    int RxMonitorPort = 19403,
    bool? ControlLocalOnly = null);
