using System.Runtime.InteropServices;
using PersonalTools;

internal static class Program
{
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public ushort Key; [FieldOffset(12)] public uint Flags; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    private static void Key(ushort key, bool up = false)
    {
        Input[] input = [new() { Type = 1, Key = key, Flags = up ? 2u : 0u }];
        if (SendInput(1, input, Marshal.SizeOf<Input>()) != 1) throw new Exception("SendInput failed");
    }
    [STAThread]
    private static void Main(string[] args)
    {
        var before = MicrophoneMute.Read();
        Console.WriteLine($"Active capture endpoints: {before.Length}; muted: {before.Count(x => x.Muted)}");
        if (args.Contains("--read")) return;
        if (before.Length == 0) throw new Exception("No microphones to verify");
        MicrophoneHotkey? hotkey = null;
        try
        {
            if (!args.Contains("--installed")) hotkey = new MicrophoneHotkey((muted, error) => Console.WriteLine($"Hook result: muted={muted}, error={error}"));
            bool expected = !before.All(x => x.Muted);
            for (int turn = 0; turn < 2; turn++)
            {
                Key(0x5B); Key(0x12); Key(0x4D);
                Thread.Sleep(700);
                // Repeated keydown while held must not toggle again.
                Key(0x4D); Thread.Sleep(300);
                Key(0x4D, true); Key(0x12, true); Key(0x5B, true);
                Thread.Sleep(700);
                var after = MicrophoneMute.Read();
                if (after.Length != before.Length || after.Any(x => x.Muted != expected)) throw new Exception($"Shortcut round {turn + 1}: expected muted={expected}");
                Console.WriteLine($"PASS round {turn + 1}: all {after.Length} endpoint mute flags={expected}; held-key repeat suppressed");
                expected = !expected;
            }
        }
        finally
        {
            Key(0x4D, true); Key(0x12, true); Key(0x5B, true);
            hotkey?.Dispose();
            MicrophoneMute.Restore(before);
            Console.WriteLine("Original endpoint mute states restored");
        }
    }
}
