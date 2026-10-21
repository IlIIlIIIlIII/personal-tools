namespace PersonalTools;

public sealed record DisplayPowerRequest(string Kind, string Name, string Reason);
public sealed record DisplayPowerSnapshot(DisplayPowerRequest[] Requests, DateTimeOffset? CheckedAt = null, string? Error = null, bool Running = false)
{
    public string Summary => Error is not null ? "화면 꺼짐 · 확인 실패"
        : CheckedAt is null ? Running ? "화면 꺼짐 · 확인 중" : "화면 꺼짐 · 조회 시작 필요"
        : Requests.Length == 0 ? "화면 꺼짐 방지 요청 없음" : $"화면 꺼짐 방지 요청 {Requests.Length}개";
}

internal static class DisplayPowerParser
{
    // Only DISPLAY requests prevent display idle-off. SYSTEM/AWAYMODE are not display blockers.
    internal static DisplayPowerRequest[] Parse(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        int start = Array.FindIndex(lines, line => line.Trim().TrimEnd(':', '：').ToUpperInvariant() is "DISPLAY" or "디스플레이" or "화면");
        if (start < 0) throw new IOException("Windows의 디스플레이 요청 목록을 해석하지 못했습니다.");
        var requests = new List<DisplayPowerRequest>();
        bool empty = false, ended = false;
        foreach (string raw in lines.Skip(start + 1))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if ((line.EndsWith(':') || line.EndsWith('：')) && line.TrimEnd(':', '：').ToUpperInvariant() is "SYSTEM" or "시스템") { ended = true; break; }
            if (line.StartsWith('['))
            {
                int end = line.IndexOf(']');
                if (end < 2 || string.IsNullOrWhiteSpace(line[(end + 1)..])) throw new IOException("전원 요청 항목이 올바르지 않습니다.");
                requests.Add(new(line[1..end], line[(end + 1)..].Trim(), ""));
            }
            else if (requests.Count > 0)
            {
                var last = requests[^1];
                requests[^1] = last with { Reason = last.Reason.Length == 0 ? line : last.Reason + "\n" + line };
            }
            else if (line.TrimEnd('.', '。') is "None" or "NONE" or "없음" or "없습니다") empty = true;
            else throw new IOException("알 수 없는 디스플레이 요청 응답입니다: " + line);
        }
        if (!ended || (requests.Count == 0 && !empty) || (requests.Count > 0 && empty)) throw new IOException("디스플레이 요청 응답이 불완전합니다.");
        return requests.ToArray();
    }
}
