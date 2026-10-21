using System.Text.Json;

namespace PersonalTools;

public sealed record VllmSnapshot(string State = "unknown", string Message = "연결 상태 확인 전", int? Context = null, string? Error = null, VllmProgress? Progress = null)
{
    public bool Busy => State is "starting" or "stopping";
    public bool Ready => State == "ready";
    public bool CanStop => State is "ready" or "offline";
    public bool CanStart => !Busy && !Ready && State != "conflict";
}

internal static class VllmProtocol
{
    public static string PipeName => HdrWire.PipeName + ".Vllm";
    public const string StartProfile = "시작 설정: 64K · MTP2 · vLLM 외 GPU 여유 3 GiB 목표";
    public const string Model = "qwen3.8-27b";
    public const string Endpoint = "http://127.0.0.1:8000/v1";
    public static VllmSnapshot ParseModels(string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var model in document.RootElement.GetProperty("data").EnumerateArray())
            if (model.GetProperty("id").GetString() == Model)
                return new("ready", "사용 가능", model.TryGetProperty("max_model_len", out var length) && length.TryGetInt32(out int size) ? size : null);
        return new("conflict", "다른 모델이 8000 포트를 사용 중입니다.");
    }
}
