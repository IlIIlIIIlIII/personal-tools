using System.Text.RegularExpressions;
namespace PersonalTools;

public static class ShortcutConfiguration
{
    public static string Canonical(string? value)
    {
        if(value is null||value.Length>100||!Regex.IsMatch(value,@"^(?:<(?:Super|Control|Ctrl|Shift|Alt)>)+[a-zA-Z0-9]$"))
            throw new ArgumentException("올바른 단축키를 입력해 주세요.");
        var modifiers=Regex.Matches(value,@"<([^>]+)>").Select(match=>match.Groups[1].Value.Replace("Ctrl","Control")).ToArray();
        if(modifiers.Distinct().Count()!=modifiers.Length)throw new ArgumentException("단축키의 보조 키를 중복해서 입력할 수 없습니다.");
        return string.Concat(new[]{"Super","Control","Shift","Alt"}.Where(modifiers.Contains).Select(modifier=>$"<{modifier}>"))+char.ToLowerInvariant(value[^1]);
    }
}
