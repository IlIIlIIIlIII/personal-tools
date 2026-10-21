using System.Text.Json;
using PersonalTools;

namespace PersonalTools.HdrWidget;

internal static class VllmCard
{
    public const string DefinitionId = "PersonalToolsVllm";
    public static string Create(VllmSnapshot snapshot, bool large = false)
    {
        var progress = snapshot.Progress;
        int step = progress?.Step ?? -1;
        var body = new List<object>
        {
            new { type = "TextBlock", text = "Qwen3.8 · 27B", weight = "Bolder", size = "Medium", wrap = false },
            new { type = "TextBlock", text = snapshot.State == "stopping" ? "서비스 중지 중…" : snapshot.State == "stopped" ? "중지됨" : snapshot.Busy ? progress?.Title ?? "준비 중" : snapshot.Ready ? "사용 가능" : "연결 대기", color = snapshot.Ready ? "Good" : snapshot.Busy ? "Accent" : "Default", weight = "Bolder", spacing = "Small", wrap = true },
        };
        body.Add(new { type = "TextBlock", text = VllmProtocol.StartProfile, wrap = true, size = "Small", spacing = "Small", isSubtle = true });
        var columns = new List<object>();
        for (int i = 0; i < VllmProgress.Steps.Length; i++)
        {
            bool done = i < step || snapshot.Ready && step < 0 && i == 6;
            bool active = i == step;
            columns.Add(new { type = "Column", width = "stretch", spacing = "Small", items = new object[] {
                new { type = "Container", style = active ? "accent" : "emphasis", items = new object[] {
                    new { type = "TextBlock", text = (i + 1).ToString(), horizontalAlignment = "Center", weight = "Bolder", size = "Small", color = done ? "Good" : "Default" }
                } }
            } });
        }
        body.Add(new { type = "ColumnSet", spacing = "Medium", columns });
        string detail = progress?.Detail ?? (snapshot.Ready ? $"컨텍스트 {snapshot.Context?.ToString("N0") ?? "—"} 토큰" : "시작하면 준비 단계를 표시합니다.");
        body.Add(new { type = "TextBlock", text = detail, wrap = true, maxLines = large ? 3 : 2, size = "Small", spacing = "Small", isSubtle = true });
        if (snapshot.Busy && progress is not null)
            body.Add(new { type = "TextBlock", text = progress.ElapsedText, size = "Small", spacing = "Small", isSubtle = true });
        if (large)
        {
            for (int i = 0; i < VllmProgress.Steps.Length; i++)
            {
                string status = i < step ? "완료" : i == step ? snapshot.Busy ? "진행 중" : "확인 필요" : snapshot.Ready && step < 0 ? i == 6 ? "연결 확인" : "기록 없음" : "대기";
                body.Add(new { type = "ColumnSet", spacing = "Small", columns = new object[] {
                    new { type = "Column", width = "stretch", items = new object[] { new { type = "TextBlock", text = VllmProgress.Steps[i], size = "Small", wrap = false } } },
                    new { type = "Column", width = "auto", items = new object[] { new { type = "TextBlock", text = status, size = "Small", color = i < step ? "Good" : i == step ? "Accent" : "Default", isSubtle = i > step } } }
                } });
            }
        }
        if (snapshot.Error is not null)
            body.Add(new { type = "TextBlock", text = snapshot.Error, wrap = true, maxLines = 2, color = "Attention", size = "Small" });
        var actions = new List<object>();
        if (snapshot.CanStart) actions.Add(new { type = "Action.Execute", title = "vLLM 시작", verb = "vllm-start", style = "positive" });
        if (snapshot.CanStop) actions.Add(new { type = "Action.Execute", title = "중지", verb = "vllm-stop" });
        actions.Add(new { type = "Action.Execute", title = "새로 고침", verb = "refresh" });
        return JsonSerializer.Serialize(new { type = "AdaptiveCard", version = "1.5", body, actions });
    }
}
