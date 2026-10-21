using System.Text.Json;
using PersonalTools;

namespace PersonalTools.HdrWidget;

internal static class WidgetCard
{
    private static readonly string ToggleOn = LoadToggle("ToggleOn");
    private static readonly string ToggleOff = LoadToggle("ToggleOff");
    private static string LoadToggle(string name)
    {
        using var stream = typeof(WidgetCard).Assembly.GetManifestResourceStream($"PersonalTools.HdrWidget.{name}.png")
            ?? throw new IOException("Missing HDR toggle resource: " + name);
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        return "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray());
    }
    internal static int Capacity(string size) => size == "large" ? 8 : size == "medium" ? 4 : 2;
    internal static bool IsPowerWidget(string definitionId) => definitionId == "PersonalToolsDisplayPower";
    public static string Create(HdrSnapshot snapshot, int page, int pageSize)
    {
        var body = new List<object>();
        if (snapshot.Error is not null) body.Add(new { type = "TextBlock", text = snapshot.Error, wrap = true, color = "Attention", maxLines = 2 });
        var monitors = snapshot.Monitors.OrderByDescending(m => m.Primary).ThenBy(m => m.Name).ThenBy(m => m.Id).ToArray();
        int pages = Math.Max(1, (monitors.Length + pageSize - 1) / pageSize);
        int current = Math.Clamp(page, 0, pages - 1);
        int row = 0;
        foreach (var monitor in monitors.Skip(current * pageSize).Take(pageSize))
        {
            bool available = monitor.Supported && monitor.Error is null;
            string state = monitor.Error is not null ? "확인 필요" : !monitor.Supported ? "미지원" : monitor.Enabled ? "켜짐" : "꺼짐";
            object control = available
                ? new {
                    type = "Image", url = monitor.Enabled ? ToggleOn : ToggleOff,
                    width = "44px", height = "24px", altText = $"{monitor.Name} HDR {state}. 눌러서 {(monitor.Enabled ? "끄기" : "켜기")}",
                    selectAction = new { type = "Action.Execute", title = $"{monitor.Name} HDR {(monitor.Enabled ? "끄기" : "켜기")}",
                        tooltip = $"HDR {state} · {(monitor.Enabled ? "끄기" : "켜기")}", verb = "set", associatedInputs = "none",
                        data = new { monitorId = monitor.Id, enabled = !monitor.Enabled } }
                }
                : new { type = "TextBlock", text = state, isSubtle = true, wrap = false };
            // Stretch distributes the host's fixed large-widget height between single-line rows.
            // Each row stays a single ColumnSet; no separate status line or action-button row.
            var columns = new List<object> {
                new { type = "Column", width = "stretch", verticalContentAlignment = "Center", items = new[] {
                    new { type = "TextBlock", text = monitor.Name + (monitor.Primary ? " · 주" : ""), weight = monitor.Primary ? "Bolder" : "Default", wrap = false, maxLines = 1 }
                } }
            };
            if (available) columns.Add(new { type = "Column", width = "auto", verticalContentAlignment = "Center", spacing = "Small", items = new[] { new { type = "TextBlock", text = state, isSubtle = true, size = "Small", wrap = false } } });
            columns.Add(new { type = "Column", width = "auto", verticalContentAlignment = "Center", spacing = "Small", items = new[] { control } });
            body.Add(new { type = "Container", height = "stretch", minHeight = "40px", verticalContentAlignment = "Center",
                separator = row++ > 0, spacing = "Small", items = new[] { new { type = "ColumnSet", columns } } });
        }
        if (monitors.Length == 0 && snapshot.Error is null) body.Add(new { type = "TextBlock", text = "연결된 모니터가 없습니다.", wrap = true });
        var actions = new List<object> { new { type = "Action.Execute", title = "새로 고침", verb = "refresh" } };
        if (pages > 1)
        {
            actions.Add(new { type = "Action.Execute", title = $"이전 ({current + 1}/{pages})", verb = "page", data = new { page = (current + pages - 1) % pages } });
            actions.Add(new { type = "Action.Execute", title = "다음", verb = "page", data = new { page = (current + 1) % pages } });
        }
        return JsonSerializer.Serialize(new { type = "AdaptiveCard", version = "1.5", body, actions });
    }
    public static string CreatePower(HdrSnapshot snapshot, int page, int pageSize)
    {
        var power = snapshot.DisplayPower ?? new DisplayPowerSnapshot([]);
        // A failed app connection invalidates the entire response, even if old data was attached.
        if (snapshot.Error is not null) power = new([], Error: snapshot.Error);
        var body = new List<object> { new { type = "TextBlock", text = power.Summary, weight = "Bolder", wrap = true } };
        int pages = Math.Max(1, (power.Requests.Length + pageSize - 1) / pageSize);
        int current = Math.Clamp(page, 0, pages - 1);
        if (power.Error is not null || power.CheckedAt is null)
            body.Add(new { type = "TextBlock", text = power.Error ?? "본 메뉴 → 디스플레이 · HDR에서 관리자 권한으로 조회를 시작하세요.", wrap = true, maxLines = 3 });
        foreach (var request in power.Requests.Skip(current * pageSize).Take(pageSize))
            body.Add(new { type = "TextBlock", text = $"[{request.Kind}] {request.Name.Split('\\', '/').Last()}\n{request.Reason}", wrap = true, maxLines = 3, size = "Small" });
        if (power.CheckedAt is not null)
            body.Add(new { type = "TextBlock", text = $"DISPLAY 요청 기준 · {power.CheckedAt.Value.ToLocalTime():HH:mm:ss}", size = "Small", isSubtle = true });
        var actions = new List<object> { new { type = "Action.Execute", title = "새로 고침", verb = "refresh" } };
        if (pages > 1) actions.Add(new { type = "Action.Execute", title = $"다음 ({current + 1}/{pages})", verb = "page", data = new { page = (current + 1) % pages } });
        return JsonSerializer.Serialize(new { type = "AdaptiveCard", version = "1.5", body, actions });
    }
}
