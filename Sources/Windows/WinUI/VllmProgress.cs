using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonalTools;

public sealed record VllmProgress(int Step, string Detail, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, int? Percent = null)
{
    public static readonly string[] Steps = ["WSL 연결", "서비스 시작", "모델 가중치 로딩", "추론 엔진 준비", "API 준비 확인", "대화 워밍업", "최종 연결 확인"];
    public string Title => Step >= Steps.Length ? "시작 완료" : $"{Step + 1}/{Steps.Length} · {Steps[Step]}";
    public string ElapsedText => $"경과 {(int)((Step >= Steps.Length ? UpdatedAt : DateTimeOffset.Now) - StartedAt).TotalMinutes:00}:{((Step >= Steps.Length ? UpdatedAt : DateTimeOffset.Now) - StartedAt).Seconds:00}";
    public static VllmProgress Begin() => new(0, "WSL에 연결하고 기존 실행 설정을 확인합니다.", DateTimeOffset.Now, DateTimeOffset.Now);
    public VllmProgress Move(int step, string detail, int? percent = null)
        => step < Step ? this : this with { Step = step, Detail = detail, Percent = percent, UpdatedAt = DateTimeOffset.Now };
    public VllmProgress Observe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            string source = document.RootElement.GetProperty("source").GetString() ?? "";
            string line = document.RootElement.GetProperty("line").GetString() ?? "";
            if (source == "bridge")
                return line == "locked" ? Move(1, "기존 start.sh로 서비스를 시작합니다.") : line == "waiting" ? Move(0, "다른 시작 작업이 끝나기를 기다립니다.") : this;
            if (source == "stdout")
            {
                if (line.StartsWith("Ready:")) return Move(5, "API 준비 완료 · 대화 워밍업 1/2 진행 중");
                if (line.StartsWith("Chat warmup 1/2:")) return Move(5, "대화 워밍업 1/2 완료 · 2/2 진행 중", 50);
                if (line.StartsWith("Chat warmup 2/2:")) return Move(6, "워밍업 2/2 완료 · Windows 연결 확인 중");
                return this;
            }
            if (source != "log") return this;
            if (line.Contains("Loading safetensors checkpoint shards:"))
            {
                var match = Regex.Match(line, @"(\d{1,3})%.*?\|\s*(\d+/\d+)");
                return Move(2, match.Success ? $"가중치 파일 {match.Groups[2].Value} · {match.Groups[1].Value}%" : "가중치 파일을 읽고 있습니다.",
                    match.Success ? Math.Clamp(int.Parse(match.Groups[1].Value), 0, 100) : null);
            }
            if (line.Contains("Loading drafter model")) return Move(2, "MTP 모델 가중치 로딩 중");
            if (line.Contains("Starting to load model")) return Move(2, "모델 가중치를 GPU에 올리고 있습니다.");
            if (line.Contains("Model loading took")) return Move(3, "가중치 로딩 완료 · 캐시와 실행 커널 준비 중");
            if (line.Contains("GPU KV cache size:")) return Move(3, "KV 캐시 준비 완료 · CUDA 그래프 준비 중");
            if (line.Contains("Capturing CUDA graphs"))
            {
                var match = Regex.Match(line, @"(\d{1,3})%");
                return Move(3, "CUDA 그래프 캡처" + (match.Success ? $" · {match.Value}" : " 중"), match.Success ? Math.Clamp(int.Parse(match.Groups[1].Value), 0, 100) : null);
            }
            if (line.Contains("torch.compile took") || line.Contains("Directly load AOT compilation") || line.Contains("Compiling a graph"))
                return Move(3, "추론 커널 컴파일 및 캐시 준비 중");
            if (line.Contains("init engine (profile") || line.Contains("Starting vLLM server on")) return Move(4, "추론 엔진 준비 완료 · API 응답 대기 중");
            if (line.Contains("Application startup complete")) return Move(4, "API 서버 시작 완료 · 모델 응답 확인 중");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
        return this;
    }
}
