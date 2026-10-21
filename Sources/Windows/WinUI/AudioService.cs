using System.Runtime.InteropServices;
using System.Text.Json;

namespace PersonalTools;
public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}
public sealed record Filter(double Frequency, double Gain, double Q)
{
    public string FrequencyText => $"{Frequency:0.##} Hz";
    public string GainText => $"{Gain:+0.##;-0.##;0} dB";
    public string QText => $"Q {Q:0.##}";
    public string GainCell => Gain.ToString("0.0;−0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
    public string QCell => $"{Q:0.00}";
}
public sealed record Profile(bool Valid, string Summary, Filter[] Filters);
public sealed record Meter(string State, bool Active, int Rate, int Error, bool Enabled,
    int NeuralState = 0, int NeuralRestarts = 0, int NeuralFailure = 0, long NeuralBlocks = 0, long MissingFrames = 0)
{
    // Matches NpuStage in DtlnNpu.hpp.
    public string FailureText => NeuralFailure switch
    {
        1 => "OpenVINO 런타임을 불러오지 못했습니다",
        2 => "NPU를 열지 못했습니다",
        3 => "NPU용 모델을 준비하지 못했습니다",
        4 => "NPU 추론 요청을 만들지 못했습니다",
        5 => "NPU 추론이 실패했습니다",
        6 => "NPU 출력이 올바르지 않습니다",
        _ => "NPU 처리 중 오류가 발생했습니다"
    };
}
public sealed record AudioSnapshot(bool Ready, int Revision, bool EqEnabled, bool AecEnabled,
    string RenderId, string CaptureId, AudioDevice[] Outputs, AudioDevice[] Inputs,
    Profile Left, Profile Right, Meter Render, Meter Capture, bool Reference, bool Clipping, string? Error, string NpuDevice = "");

internal sealed class AudioService : IDisposable
{
    private const string Bridge = "PersonalToolsBridge.dll";
    private readonly object gate = new();
    private nint handle = MT_Create();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    // The bridge's loopback worker is the only producer of the echo canceller's speaker reference.
    // A dev build run from build\<name>\ may lack it; use the installed copy rather than run without one.
    public static string? BridgeFallback { get; private set; }
    static AudioService() => NativeLibrary.SetDllImportResolver(typeof(AudioService).Assembly, ResolveBridge);
    private static nint ResolveBridge(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Bridge) return 0;
        if (NativeLibrary.TryLoad(name, assembly, path, out var local)) return local;
        string installed = Path.Combine(AudioController.InstallDirectory, "ui", Bridge);
        if (!File.Exists(installed) || !NativeLibrary.TryLoad(installed, out var fallback)) return 0;
        BridgeFallback = installed;
        return fallback;
    }
    public AudioService()
    {
        if (handle == 0) throw new InvalidOperationException("오디오 설정 서비스를 시작하지 못했습니다.");
    }
    public Task<AudioSnapshot> ReadAsync() => Task.Run(() =>
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            var json = Marshal.PtrToStringUni(MT_Snapshot(handle)) ?? throw new InvalidDataException("빈 오디오 상태");
            var snapshot = JsonSerializer.Deserialize<AudioSnapshot>(json, JsonOptions)!;
            if (snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error);
            return snapshot;
        }
    });
    public Task EnableAsync(bool eq, bool aec) => Task.Run(() =>
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            Check(MT_SetEnabled(handle, eq ? 1 : 0, aec ? 1 : 0));
        }
    });
    public Task ImportAsync(int channel, string path) => Task.Run(() =>
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            Check(MT_Import(handle, channel, path));
        }
    });
    private static void Check(nint result)
    {
        string error = Marshal.PtrToStringUni(result) ?? "오디오 서비스 응답 오류";
        if (error.Length > 0) throw new InvalidOperationException(error);
    }
    public void Dispose()
    {
        lock (gate) { if (handle != 0) MT_Destroy(handle); handle = 0; }
    }
    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl)] private static extern nint MT_Create();
    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl)] private static extern void MT_Destroy(nint handle);
    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl)] private static extern nint MT_Snapshot(nint handle);
    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl)] private static extern nint MT_SetEnabled(nint handle, int eq, int aec);
    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern nint MT_Import(nint handle, int channel, string path);
}
