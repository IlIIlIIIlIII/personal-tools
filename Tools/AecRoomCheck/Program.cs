// Room echo check: plays a speech-like test signal through the selected speaker
// while recording the selected microphone in shared mode (so installed capture
// effects apply), then reports the echo level. Run once with echo cancellation
// on and once off; the difference is the echo reduction in this room.
// Usage: PersonalToolsAecRoomCheck --render SMSL --capture "MOTU" [--level -20] [--wav out.wav]
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.Render;
using WinRT;

string Arg(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
// --analyze <wav>: re-analyse an earlier recording of the same deterministic test signal.
if (Array.IndexOf(args, "--analyze") is int at and >= 0 && at + 1 < args.Length)
{
    var (samples, wavRate) = ReadWav(args[at + 1]);
    var result = Analyze(samples, wavRate, double.Parse(Arg("--level", "-20"), System.Globalization.CultureInfo.InvariantCulture), int.Parse(Arg("--seconds", "10")));
    Console.WriteLine(JsonSerializer.Serialize(new { coherentEchoDb = result.CoherentEchoDb, uncorrelatedDb = result.UncorrelatedDb, echoDelayMs = result.DelayMs, meanCoherence = result.MeanCoherence }));
    return 0;
}
// --leak on.wav on.json off.wav off.json: per-window echo suppression of a --bursts run with
// echo cancellation on, against the same bursts with it off. Room sound in the recording does
// not correlate with the played signal, so only echo that got through counts as a leak.
if (Array.IndexOf(args, "--leak") is int leakAt and >= 0 && leakAt + 4 < args.Length)
{
    Console.WriteLine(JsonSerializer.Serialize(Leak(args[leakAt + 1], args[leakAt + 2], args[leakAt + 3], args[leakAt + 4], double.Parse(Arg("--level", "-12"), System.Globalization.CultureInfo.InvariantCulture))));
    return 0;
}
// --tones 66,140,1000: speaker EQ check. Plays each sine tone and reports its level at the
// microphone (narrowband), so EQ on/off runs show each filter's cut in the room.
double[] tones = Arg("--tones", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
string renderName = Arg("--render", "SMSL"), captureName = Arg("--capture", "MOTU"), wav = Arg("--wav", "");
double levelDb = double.Parse(Arg("--level", "-20"), System.Globalization.CultureInfo.InvariantCulture);
int playSeconds = int.Parse(Arg("--seconds", "10"));

var renders = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
var captures = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
var render = renders.FirstOrDefault(d => d.Name.Contains(renderName, StringComparison.OrdinalIgnoreCase));
var capture = captures.FirstOrDefault(d => d.Name.Contains(captureName, StringComparison.OrdinalIgnoreCase) && !d.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase));
if (render is null || capture is null)
{
    Console.WriteLine(JsonSerializer.Serialize(new { error = "device not found", renders = renders.Select(d => d.Name), captures = captures.Select(d => d.Name) }));
    return 2;
}
// --bursts N: speech bursts separated by real speaker idle (the render stream is closed
// between bursts), to catch echo leaking when playback starts or stops.
if (int.Parse(Arg("--bursts", "0")) is int burstCount and > 0)
    return await Bursts(render, capture, renders, burstCount, double.Parse(Arg("--burst", "2"), System.Globalization.CultureInfo.InvariantCulture),
        double.Parse(Arg("--idle", "4"), System.Globalization.CultureInfo.InvariantCulture), double.Parse(Arg("--level", "-20"), System.Globalization.CultureInfo.InvariantCulture), Arg("--wav", ""),
        int.Parse(Arg("--seed", "11")));
// --raw: play as a RAW stream (no mode effects), as some voice apps do; --category: render category.
var settings = new AudioGraphSettings(Enum.Parse<AudioRenderCategory>(Arg("--category", "Media"), true)) { PrimaryRenderDevice = render };
if (Array.IndexOf(args, "--raw") >= 0) settings.DesiredRenderDeviceAudioProcessing = Windows.Media.AudioProcessing.Raw;
var created = await AudioGraph.CreateAsync(settings);
if (created.Status != AudioGraphCreationStatus.Success) { Console.WriteLine(JsonSerializer.Serialize(new { error = "graph " + created.Status })); return 3; }
using var graph = created.Graph;
uint rate = graph.EncodingProperties.SampleRate, channels = graph.EncodingProperties.ChannelCount;
var output = await graph.CreateDeviceOutputNodeAsync();
if (output.Status != AudioDeviceNodeCreationStatus.Success) { Console.WriteLine(JsonSerializer.Serialize(new { error = "speaker " + output.Status })); return 4; }
var input = await graph.CreateDeviceInputNodeAsync(MediaCategory.Communications, graph.EncodingProperties, capture);
if (input.Status != AudioDeviceNodeCreationStatus.Success) { Console.WriteLine(JsonSerializer.Serialize(new { error = "microphone " + input.Status, hint = "Check Windows privacy: let desktop apps access the microphone." })); return 5; }

// 1 s silence, 10 s speech-like signal (or the tones), 1.5 s tail.
int toneFrames = (int)(rate * 1.5), toneGap = (int)(rate * 0.3);
int lead = (int)rate, play = tones.Length > 0 ? tones.Length * (toneFrames + toneGap) : (int)rate * playSeconds, tail = (int)(rate * 1.5);
var signal = tones.Length > 0 ? Tones(tones, toneFrames, toneGap, rate, Math.Pow(10, levelDb / 20)) : Speech(play, rate, 11, 128, Math.Pow(10, levelDb / 20));
var frameIn = graph.CreateFrameInputNode(graph.EncodingProperties);
frameIn.AddOutgoingConnection(output.DeviceOutputNode);
var frameOut = graph.CreateFrameOutputNode(graph.EncodingProperties);
input.DeviceInputNode.AddOutgoingConnection(frameOut);
long written = 0;
var recorded = new List<float>(lead + play + tail + (int)rate);
frameIn.QuantumStarted += (node, e) =>
{
    int samples = e.RequiredSamples;
    if (samples <= 0) return;
    var frame = new AudioFrame((uint)(samples * channels * sizeof(float)));
    using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
    using (var reference = buffer.CreateReference())
    unsafe
    {
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out _);
        var target = (float*)data;
        for (int i = 0; i < samples; i++)
        {
            long index = written + i - lead;
            float value = index >= 0 && index < play ? signal[index] : 0f;
            for (int c = 0; c < channels; c++) target[i * channels + c] = value;
        }
    }
    written += samples;
    node.AddFrame(frame);
};
graph.QuantumProcessed += (g, _) =>
{
    using var frame = frameOut.GetFrame();
    using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
    using var reference = buffer.CreateReference();
    unsafe
    {
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out uint capacity);
        var source = (float*)data; int frames = (int)(capacity / sizeof(float) / channels);
        lock (recorded) for (int i = 0; i < frames; i++) recorded.Add(source[i * channels]);
    }
};
graph.Start();
await Task.Delay(TimeSpan.FromSeconds((lead + play + tail) / (double)rate + 0.3));
graph.Stop();

float[] mic; lock (recorded) mic = recorded.ToArray();
double Level(int from, int to) { from = Math.Clamp(from, 0, mic.Length); to = Math.Clamp(to, from, mic.Length); double sum = 0; for (int i = from; i < to; i++) sum += mic[i] * (double)mic[i]; return to > from ? sum / (to - from) : 0; }
double Db(double power) => 10 * Math.Log10(Math.Max(power, 1e-14));
double noise = Level((int)(rate * 0.2), (int)(rate * 0.9));
double echo = Level(lead + (int)rate * 2, lead + play - (int)(rate * 0.2));
double clipped = mic.Count(x => Math.Abs(x) >= 0.98f) / (double)Math.Max(1, mic.Length);
if (wav.Length > 0) WriteWav(wav, mic, rate);
if (tones.Length > 0)
{
    // Narrowband level of each tone over its steady middle second (after ~200 ms of output latency),
    // and of the same frequency in the silent lead as the floor.
    double Tone(double frequency, int from, int length)
    {
        double re = 0, im = 0, w = 2 * Math.PI * frequency / rate;
        for (int i = 0; i < length && from + i < mic.Length; i++) { double h = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / length); re += mic[from + i] * h * Math.Cos(w * i); im -= mic[from + i] * h * Math.Sin(w * i); }
        return 20 * Math.Log10(Math.Max(Math.Sqrt(re * re + im * im) * 4 / length, 1e-9)); // amplitude in dBFS (Hann gain compensated)
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        render = render.Name, capture = capture.Name, playbackDbfs = levelDb, clippedRatio = clipped,
        tones = tones.Select((f, k) => new { hz = f, levelDb = Math.Round(Tone(f, lead + k * (toneFrames + toneGap) + (int)(rate * 0.35), (int)rate), 1), floorDb = Math.Round(Tone(f, (int)(rate * 0.05), (int)(rate * 0.8)), 1) })
    }));
    return 0;
}
var coherent = Analyze(mic, rate, levelDb, playSeconds);
Console.WriteLine(JsonSerializer.Serialize(new
{
    render = render.Name, capture = capture.Name, rate, channels, recordedSeconds = mic.Length / (double)rate, playbackDbfs = levelDb,
    noiseDb = Math.Round(Db(noise), 1), echoDb = Math.Round(Db(echo), 1), echoAboveNoiseDb = Math.Round(Db(echo) - Db(noise), 1),
    echoPowerMinusNoise = Math.Max(echo - noise, 1e-14), clippedRatio = clipped,
    coherentEchoDb = coherent.CoherentEchoDb, uncorrelatedDb = coherent.UncorrelatedDb, echoDelayMs = coherent.DelayMs, meanCoherence = coherent.MeanCoherence
}));
return 0;

static async Task<int> Bursts(DeviceInformation render, DeviceInformation capture, IReadOnlyList<DeviceInformation> renders, int count, double burstSeconds, double idleSeconds, double levelDb, string wav, int seed)
{
    // The capture graph is clocked by another output so that the speaker endpoint goes idle.
    var clock = renders.FirstOrDefault(d => d.Id != render.Id);
    var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Media) { PrimaryRenderDevice = clock });
    if (created.Status != AudioGraphCreationStatus.Success) { Console.WriteLine(JsonSerializer.Serialize(new { error = "graph " + created.Status })); return 3; }
    using var graph = created.Graph;
    uint rate = graph.EncodingProperties.SampleRate, channels = graph.EncodingProperties.ChannelCount;
    var input = await graph.CreateDeviceInputNodeAsync(MediaCategory.Communications, graph.EncodingProperties, capture);
    if (input.Status != AudioDeviceNodeCreationStatus.Success) { Console.WriteLine(JsonSerializer.Serialize(new { error = "microphone " + input.Status })); return 5; }
    var frameOut = graph.CreateFrameOutputNode(graph.EncodingProperties);
    input.DeviceInputNode.AddOutgoingConnection(frameOut);
    var recorded = new List<float>((int)(rate * (2 + count * (burstSeconds + idleSeconds))));
    graph.QuantumProcessed += (g, _) =>
    {
        using var frame = frameOut.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        unsafe
        {
            reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out uint capacity);
            var source = (float*)data; int frames = (int)(capacity / sizeof(float) / channels);
            lock (recorded) for (int i = 0; i < frames; i++) recorded.Add(source[i * channels]);
        }
    };
    int burstFrames = (int)(rate * burstSeconds);
    var signal = Speech(burstFrames * count, rate, seed, 128, Math.Pow(10, levelDb / 20));
    var starts = new List<(int Position, long Qpc)>();
    graph.Start();
    await Task.Delay(1500);
    for (int b = 0; b < count; b++)
    {
        var renderCreated = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Media) { PrimaryRenderDevice = render });
        if (renderCreated.Status != AudioGraphCreationStatus.Success) { Console.WriteLine(JsonSerializer.Serialize(new { error = "render graph " + renderCreated.Status })); return 3; }
        // Stop() alone keeps the speaker stream open; dispose before the idle gap.
        var renderGraph = renderCreated.Graph;
        if (renderGraph.EncodingProperties.SampleRate != rate) { Console.WriteLine(JsonSerializer.Serialize(new { error = "rate mismatch", render = renderGraph.EncodingProperties.SampleRate, rate })); return 6; }
        uint renderChannels = renderGraph.EncodingProperties.ChannelCount;
        var speaker = await renderGraph.CreateDeviceOutputNodeAsync();
        if (speaker.Status != AudioDeviceNodeCreationStatus.Success) { Console.WriteLine(JsonSerializer.Serialize(new { error = "speaker " + speaker.Status })); return 4; }
        var frameIn = renderGraph.CreateFrameInputNode(renderGraph.EncodingProperties);
        frameIn.AddOutgoingConnection(speaker.DeviceOutputNode);
        long sent = 0; int offset = b * burstFrames; var done = new TaskCompletionSource(); bool stopping = false;
        frameIn.QuantumStarted += (node, e) =>
        {
            int samples = e.RequiredSamples;
            if (samples <= 0 || Volatile.Read(ref stopping)) return;
            var frame = new AudioFrame((uint)(samples * renderChannels * sizeof(float)));
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (var reference = buffer.CreateReference())
            unsafe
            {
                reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out _);
                var target = (float*)data;
                for (int i = 0; i < samples; i++)
                {
                    long index = sent + i;
                    float value = index < burstFrames ? signal[offset + index] : 0f;
                    for (int c = 0; c < renderChannels; c++) target[i * renderChannels + c] = value;
                }
            }
            sent += samples;
            node.AddFrame(frame);
            // Keep 100 ms of silence after the burst, then let the stream close like a short sound would.
            if (sent >= burstFrames + rate / 10) done.TrySetResult();
        };
        int position; lock (recorded) position = recorded.Count;
        long qpc = System.Diagnostics.Stopwatch.GetTimestamp();
        renderGraph.Start();
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(burstSeconds + 2)));
        Volatile.Write(ref stopping, true);
        renderGraph.Stop();
        await Task.Delay(100); // let an in-flight quantum finish before the graph goes away
        frameIn.Dispose(); speaker.DeviceOutputNode.Dispose(); renderGraph.Dispose();
        starts.Add((position, qpc));
        await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
    }
    await Task.Delay(500);
    graph.Stop();
    float[] mic; lock (recorded) mic = recorded.ToArray();
    if (wav.Length > 0) WriteWav(wav, mic, rate);
    double Power(int from, int to) { from = Math.Clamp(from, 0, mic.Length); to = Math.Clamp(to, from, mic.Length); double sum = 0; for (int i = from; i < to; i++) sum += mic[i] * (double)mic[i]; return to > from ? sum / (to - from) : 0; }
    double Db(double power) => Math.Round(10 * Math.Log10(Math.Max(power, 1e-14)), 1);
    int window = (int)(rate * 0.04), step = (int)(rate * 0.02), ms = (int)(rate / 1000);
    var lags = starts.Select((s, b) => FindLag(mic, signal, b * burstFrames, burstFrames, s.Position, (int)(rate * 0.6))).ToList();
    int fallback = lags.Where(l => l.Strong).Select(l => l.Lag).DefaultIfEmpty((int)(rate * 0.1)).OrderBy(l => l).ElementAt(lags.Count(l => l.Strong) / 2);
    // Noise floor: the quietest late-idle stretch (the output may switch between raw and processed audio).
    double noise = Enumerable.Range(0, count).Select(b => Power(starts[b].Position + fallback + burstFrames + 2000 * ms, starts[b].Position + fallback + burstFrames + 3500 * ms)).Where(p => p > 0).DefaultIfEmpty(0).Min();
    double leadNoise = Power((int)(rate * 0.3), (int)(rate * 1.4));
    var results = new List<object>();
    for (int b = 0; b < count; b++)
    {
        int lag = lags[b].Strong ? lags[b].Lag : fallback, at = starts[b].Position + lag, offset = b * burstFrames;
        // Echo return per 40 ms window (microphone above noise / played energy), against the burst median.
        var ratios = new List<(double Ms, double Db)>();
        double maxRef = 0;
        for (int k = 0; k + window <= burstFrames; k += step) { double e = 0; for (int i = 0; i < window; i++) e += signal[offset + k + i] * (double)signal[offset + k + i]; maxRef = Math.Max(maxRef, e); }
        for (int k = 0; k + window <= burstFrames; k += step)
        {
            double e = 0; for (int i = 0; i < window; i++) e += signal[offset + k + i] * (double)signal[offset + k + i];
            if (e < maxRef * 0.03) continue;
            double m = (Power(at + k, at + k + window) - noise) * window;
            ratios.Add((k / (double)ms, 10 * Math.Log10(Math.Max(m, 1e-14) / e)));
        }
        double median = ratios.Select(r => r.Db).OrderBy(x => x).ElementAtOrDefault(ratios.Count / 2);
        var worst = ratios.OrderByDescending(r => r.Db).FirstOrDefault();
        var onset = ratios.Where(r => r.Ms < 300).Select(r => r.Db - median).DefaultIfEmpty(0).Max();
        results.Add(new
        {
            burst = b, startQpc100ns = (long)(starts[b].Qpc * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency)), lagMs = Math.Round(lag / (double)ms, 1), lagFound = lags[b].Strong,
            onsetDb = Db(Power(at, at + 300 * ms) - noise), steadyDb = Db(Power(at + 500 * ms, at + burstFrames - 100 * ms) - noise),
            tailDb = Db(Power(at + burstFrames + 50 * ms, at + burstFrames + 550 * ms) - noise), lateTailDb = Db(Power(at + burstFrames + 550 * ms, at + burstFrames + 1550 * ms) - noise),
            idleDb = Db(Power(at + burstFrames + 2000 * ms, at + burstFrames + 3500 * ms)),
            medianReturnDb = Math.Round(median, 1), onsetExcessDb = Math.Round(onset, 1), worstExcessDb = Math.Round(worst.Db - median, 1), worstAtMs = worst.Ms
        });
    }
    // 50 ms level envelope (dBFS) of the whole recording, for locating leaks by eye.
    var envelope = Enumerable.Range(0, mic.Length / (50 * ms)).Select(k => Db(Power(k * 50 * ms, (k + 1) * 50 * ms))).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { render = render.Name, capture = capture.Name, clock = clock?.Name, rate, playbackDbfs = levelDb, burstSeconds, seed, noiseDb = Db(noise), leadNoiseDb = Db(leadNoise),
        renderStarts = starts.Select(s => Math.Round(s.Position / (double)rate, 3)), bursts = results, envelope50ms = envelope }));
    return 0;
}
static object Leak(string onWav, string onJson, string offWav, string offJson, double levelDb)
{
    var (on, rate) = ReadWav(onWav); var (off, _) = ReadWav(offWav);
    double[] Starts(string json) => JsonDocument.Parse(File.ReadAllText(json)).RootElement.GetProperty("renderStarts").EnumerateArray().Select(e => e.GetDouble()).ToArray();
    var onStarts = Starts(onJson); var offStarts = Starts(offJson);
    int count = Math.Min(onStarts.Length, offStarts.Length);
    double burstSeconds = JsonDocument.Parse(File.ReadAllText(onJson)).RootElement.TryGetProperty("burstSeconds", out var b) ? b.GetDouble() : 2;
    int burstFrames = (int)(rate * burstSeconds), window = (int)(rate * 0.1), step = (int)(rate * 0.05);
    var signal = Speech(burstFrames * count, rate, 11, 128, Math.Pow(10, levelDb / 20));
    // Echo gain on the delayed played signal per 100 ms window; on/off ratio is the suppression there.
    double Gain(float[] mic, int at, int offset, int k) { double xy = 0, xx = 0; for (int i = 0; i < window; i++) { double x = signal[offset + k + i]; xy += x * mic[Math.Clamp(at + k + i, 0, mic.Length - 1)]; xx += x * x; } return xy / Math.Max(xx, 1e-20); }
    var windows = new List<(int Burst, double Ms, double SuppressionDb, double OffGainDb)>();
    for (int burst = 0; burst < count; burst++)
    {
        int offset = burst * burstFrames;
        var lagOn = FindLag(on, signal, offset, burstFrames, (int)(onStarts[burst] * rate), (int)(rate * 0.6));
        var lagOff = FindLag(off, signal, offset, burstFrames, (int)(offStarts[burst] * rate), (int)(rate * 0.6));
        int atOn = (int)(onStarts[burst] * rate) + lagOff.Lag, atOff = (int)(offStarts[burst] * rate) + lagOff.Lag;
        // Loud room sound can win the correlation in the suppressed recording; trust it only near the open-loop lag.
        if (lagOn.Strong && Math.Abs(lagOn.Lag - lagOff.Lag) < rate / 25) atOn = (int)(onStarts[burst] * rate) + lagOn.Lag;
        double maxRef = 0; var energy = new List<double>();
        for (int k = 0; k + window <= burstFrames; k += step) { double e = 0; for (int i = 0; i < window; i++) e += signal[offset + k + i] * (double)signal[offset + k + i]; energy.Add(e); maxRef = Math.Max(maxRef, e); }
        for (int k = 0, w = 0; k + window <= burstFrames; k += step, w++)
        {
            if (energy[w] < maxRef * 0.03) continue;
            double gOff = Gain(off, atOff, offset, k), gOn = Gain(on, atOn, offset, k);
            windows.Add((burst, k * 1000.0 / rate, 20 * Math.Log10(Math.Max(Math.Abs(gOff), 1e-9) / Math.Max(Math.Abs(gOn), 1e-9)), 20 * Math.Log10(Math.Max(Math.Abs(gOff), 1e-9))));
        }
    }
    var sorted = windows.Select(w => w.SuppressionDb).OrderBy(x => x).ToArray();
    return new
    {
        windows = windows.Count, medianSuppressionDb = Math.Round(sorted[sorted.Length / 2], 1), p10SuppressionDb = Math.Round(sorted[sorted.Length / 10], 1),
        minSuppressionDb = Math.Round(sorted[0], 1), under10dB = sorted.Count(x => x < 10), under6dB = sorted.Count(x => x < 6),
        // offGainDb far below offGainMedianDb means the open-loop echo itself was missing there (not a leak).
        offGainMedianDb = Math.Round(windows.Select(w => w.OffGainDb).OrderBy(x => x).ElementAt(windows.Count / 2), 1),
        weakest = windows.OrderBy(w => w.SuppressionDb).Take(6).Select(w => new { w.Burst, w.Ms, suppressionDb = Math.Round(w.SuppressionDb, 1), offGainDb = Math.Round(w.OffGainDb, 1) }),
        perBurst = windows.GroupBy(w => w.Burst).Select(g => new { burst = g.Key, firstWindowDb = Math.Round(g.First().SuppressionDb, 1), medianDb = Math.Round(g.Select(w => w.SuppressionDb).OrderBy(x => x).ElementAt(g.Count() / 2), 1), lastWindowDb = Math.Round(g.Last().SuppressionDb, 1) })
    };
}
// Locates a played burst in the recording: lag from the render start position, and whether the peak is clear.
static (int Lag, bool Strong) FindLag(float[] mic, float[] signal, int offset, int frames, int position, int maxLag)
{
    int from = Math.Max(0, position - maxLag / 6), length = Math.Min(mic.Length - from, frames + maxLag);
    if (length <= 0) return (0, false);
    int n = 1; while (n < length + frames) n <<= 1;
    var a = new System.Numerics.Complex[n]; var b = new System.Numerics.Complex[n];
    for (int i = 0; i < frames; i++) a[i] = signal[offset + i];
    for (int i = 0; i < length; i++) b[i] = mic[from + i];
    Fft(a, false); Fft(b, false);
    for (int k = 0; k < n; k++) a[k] = System.Numerics.Complex.Conjugate(a[k]) * b[k];
    Fft(a, true);
    int best = 0; double peak = double.MinValue; var magnitudes = new List<double>();
    for (int l = 0; l <= Math.Min(maxLag, length - 1); l++) { magnitudes.Add(Math.Abs(a[l].Real)); if (a[l].Real > peak) { peak = a[l].Real; best = l; } }
    magnitudes.Sort();
    double typical = magnitudes[magnitudes.Count / 2];
    return (from + best - position, peak > 8 * typical);
}
// Echo is the part of the recording that is linearly coherent with the played test signal
// after delay alignment; uncorrelated room sound and speech fall into the remainder.
static (double CoherentEchoDb, double UncorrelatedDb, double DelayMs, double MeanCoherence) Analyze(float[] mic, uint rate, double levelDb, int playSeconds = 10)
{
    int lead = (int)rate, play = (int)rate * playSeconds;
    var signal = Speech(play, rate, 11, 128, Math.Pow(10, levelDb / 20));
    var reference = new double[mic.Length];
    for (int i = 0; i < play && lead + i < mic.Length; i++) reference[lead + i] = signal[i];
    // Delay search over 0-800 ms by FFT cross-correlation of the playback window.
    int maxLag = (int)(rate * 0.8), from = lead, to = Math.Min(mic.Length, lead + play + maxLag);
    int n = 1; while (n < (to - from) + maxLag) n <<= 1;
    var a = new System.Numerics.Complex[n]; var b = new System.Numerics.Complex[n];
    for (int i = from; i < to; i++) { a[i - from] = reference[i]; b[i - from] = mic[i]; }
    Fft(a, false); Fft(b, false);
    for (int k = 0; k < n; k++) a[k] = System.Numerics.Complex.Conjugate(a[k]) * b[k];
    Fft(a, true);
    int lag = 0; double best = double.MinValue;
    for (int l = 0; l <= maxLag; l++) if (a[l].Real > best) { best = a[l].Real; lag = l; }
    // Welch cross-spectra between the delayed reference and the recording, 100 Hz - 8 kHz.
    const int size = 8192, hop = 4096;
    var sxx = new double[size / 2]; var syy = new double[size / 2]; var sxy = new System.Numerics.Complex[size / 2];
    var window = Enumerable.Range(0, size).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size)).ToArray();
    int start = lead + (int)rate * 2 + lag, end = Math.Min(mic.Length, lead + play + lag) - size, frames = 0;
    var x = new System.Numerics.Complex[size]; var y = new System.Numerics.Complex[size];
    for (int s = start; s <= end; s += hop, frames++)
    {
        for (int i = 0; i < size; i++) { x[i] = reference[s - lag + i] * window[i]; y[i] = mic[s + i] * window[i]; }
        Fft(x, false); Fft(y, false);
        for (int k = 0; k < size / 2; k++) { sxx[k] += x[k].Magnitude * x[k].Magnitude; syy[k] += y[k].Magnitude * y[k].Magnitude; sxy[k] += System.Numerics.Complex.Conjugate(x[k]) * y[k]; }
    }
    int lo = (int)(100.0 * size / rate), hi = (int)(8000.0 * size / rate);
    double coherentPower = 0, totalPower = 0, coherenceSum = 0;
    for (int k = lo; k <= hi; k++)
    {
        if (sxx[k] <= 0) continue;
        double c = sxy[k].Magnitude * sxy[k].Magnitude / (sxx[k] * syy[k] + 1e-30);
        coherentPower += c * syy[k]; totalPower += syy[k]; coherenceSum += c;
    }
    double norm = Math.Max(1, frames) * window.Sum(w => w * w) * size / 2.0;
    return (Math.Round(10 * Math.Log10(Math.Max(coherentPower / norm, 1e-14)), 1), Math.Round(10 * Math.Log10(Math.Max((totalPower - coherentPower) / norm, 1e-14)), 1),
        Math.Round(lag * 1000.0 / rate, 1), Math.Round(coherenceSum / (hi - lo + 1), 3));
}
static void Fft(System.Numerics.Complex[] data, bool inverse)
{
    int n = data.Length;
    for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) (data[i], data[j]) = (data[j], data[i]); }
    for (int length = 2; length <= n; length <<= 1)
    {
        double angle = 2 * Math.PI / length * (inverse ? 1 : -1);
        var step = new System.Numerics.Complex(Math.Cos(angle), Math.Sin(angle));
        for (int i = 0; i < n; i += length)
        {
            var w = System.Numerics.Complex.One;
            for (int k = 0; k < length / 2; k++) { var u = data[i + k]; var v = data[i + k + length / 2] * w; data[i + k] = u + v; data[i + k + length / 2] = u - v; w *= step; }
        }
    }
    if (inverse) for (int i = 0; i < n; i++) data[i] /= n;
}
static (float[] Samples, uint Rate) ReadWav(string path)
{
    var bytes = File.ReadAllBytes(path);
    uint rate = BitConverter.ToUInt32(bytes, 24);
    var samples = new float[(bytes.Length - 44) / 4];
    Buffer.BlockCopy(bytes, 44, samples, 0, samples.Length * 4);
    return (samples, rate);
}

static float[] Tones(double[] frequencies, int toneFrames, int gap, uint rate, double gain)
{
    var output = new float[frequencies.Length * (toneFrames + gap)];
    int ramp = (int)(rate / 50);
    for (int k = 0; k < frequencies.Length; k++)
        for (int i = 0; i < toneFrames; i++)
        {
            double envelope = Math.Min(1.0, Math.Min(i, toneFrames - 1 - i) / (double)ramp);
            output[k * (toneFrames + gap) + i] = (float)(gain * envelope * Math.Sin(2 * Math.PI * frequencies[k] * i / rate));
        }
    return output;
}
static float[] Speech(int frames, uint rate, int seed, double basePitch, double gain)
{
    var random = new Random(seed); var output = new float[frames]; int pos = 0;
    while (pos < frames)
    {
        int length = (int)(rate * (0.15 + 0.18 * random.NextDouble())), gap = (int)(rate * (0.05 + 0.12 * random.NextDouble()));
        double f1 = 400 + 500 * random.NextDouble(), f2 = 1100 + 1300 * random.NextDouble();
        double pitch = basePitch * (0.85 + 0.3 * random.NextDouble()), glide = (random.NextDouble() - 0.5) * 0.4, phase = 0;
        for (int i = 0; i < length && pos + i < frames; i++)
        {
            double t = (double)i / length, envelope = Math.Pow(Math.Sin(Math.PI * t), 2), f0 = pitch * (1 + glide * t);
            phase += 2 * Math.PI * f0 / rate; double sample = 0;
            for (int h = 1; h * f0 < 4000; h++) { double f = h * f0; sample += (1 / (1 + Math.Pow((f - f1) / 180, 2)) + 0.6 / (1 + Math.Pow((f - f2) / 260, 2)) + 0.05) * Math.Sin(h * phase) / h; }
            output[pos + i] = (float)(gain * 2.5 * envelope * sample);
        }
        pos += length + gap;
    }
    return output;
}
static void WriteWav(string path, float[] samples, uint rate)
{
    using var file = new BinaryWriter(File.Create(path));
    int bytes = samples.Length * 4;
    file.Write("RIFF"u8); file.Write(36 + bytes); file.Write("WAVE"u8); file.Write("fmt "u8); file.Write(16); file.Write((short)3); file.Write((short)1);
    file.Write((int)rate); file.Write((int)rate * 4); file.Write((short)4); file.Write((short)32); file.Write("data"u8); file.Write(bytes);
    foreach (var s in samples) file.Write(s);
}

[ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
unsafe interface IMemoryBufferByteAccess { void GetBuffer(out byte* buffer, out uint capacity); }
