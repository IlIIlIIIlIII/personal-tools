namespace PersonalTools;

// Session-only window registrations for Win+key switching. No UI or native calls here.
public sealed record WindowSlot(nint Handle, uint ProcessId, uint ThreadId, string Title, string AppName, string? AppPath);
public enum WindowKeyAction { None, Register, Activate }

public static class WindowSlotKeys
{
    public static readonly char[] Keys = ['1', '2', '3', '4', 'Q', 'W', 'E', 'R'];
    public static int IndexOfVirtualKey(int virtualKey) => Array.IndexOf(Keys, (char)virtualKey);
    // Win+Shift+key registers and Win+key activates. Ctrl or Alt leave the key to Windows.
    public static (WindowKeyAction Action, int Slot) Classify(int virtualKey, bool win, bool shift, bool control, bool alt)
    {
        int slot = IndexOfVirtualKey(virtualKey);
        if (slot < 0 || !win || control || alt) return (WindowKeyAction.None, -1);
        return (shift ? WindowKeyAction.Register : WindowKeyAction.Activate, slot);
    }
    public static string Shortcut(int slot, bool register) => register ? $"Win+Shift+{Keys[slot]}" : $"Win+{Keys[slot]}";
}

public sealed class WindowSlotTable
{
    private readonly object gate = new();
    private readonly WindowSlot?[] slots = new WindowSlot?[WindowSlotKeys.Keys.Length];
    public WindowSlot?[] Snapshot() { lock (gate) return (WindowSlot?[])slots.Clone(); }
    public int Count { get { lock (gate) return slots.Count(x => x is not null); } }
    public WindowSlot? Get(int index) { lock (gate) return slots[index]; }
    public void Assign(int index, WindowSlot slot) { lock (gate) slots[index] = slot; }
    public bool Clear(int index)
    {
        lock (gate)
        {
            if (slots[index] is null) return false;
            slots[index] = null;
            return true;
        }
    }
    // A closed window frees every key it was registered to.
    public int[] ClearHandle(nint handle)
    {
        lock (gate)
        {
            var cleared = new List<int>();
            for (int i = 0; i < slots.Length; i++)
                if (slots[i]?.Handle == handle) { slots[i] = null; cleared.Add(i); }
            return cleared.ToArray();
        }
    }
    public bool UpdateTitle(nint handle, string title)
    {
        lock (gate)
        {
            bool changed = false;
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] is { } slot && slot.Handle == handle && slot.Title != title) { slots[i] = slot with { Title = title }; changed = true; }
            return changed;
        }
    }
    public bool Contains(nint handle) { lock (gate) return slots.Any(x => x?.Handle == handle); }
}
