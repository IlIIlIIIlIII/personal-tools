using System.Globalization;
using System.Runtime.InteropServices;

namespace PersonalTools;

// The process list behind auto-HDR's 1 s poll. Process.GetProcesses() and the other snapshots (EnumProcesses, Toolhelp)
// read SystemProcessInformation, which also walks every thread: on this PC (630 processes, 15,600 threads) about 15 ms
// of kernel time per call, so the poll alone took about 1.5% of a core. SystemBasicProcessInformation (Windows 11 24H2)
// lists id, sequence number and image name in under 0.1 ms but has no session id, so the full list is read only when a
// process appears whose session is needed: 4 of 120 seconds on this PC (2026-10-21).
internal sealed class HdrProcessList
{
    // Stamp is the sequence number (start time without the basic list), so a reused id is a new entry. Session is null
    // until a poll needs it and -1 for a process that ended before the full list was read.
    internal readonly record struct Entry(int Id, string Name, int? Session, long Stamp);
    private const int FullInformation = 5, BasicInformation = 252;
    private Dictionary<int, Entry> current = [], previous = [];
    private byte[] buffer = GC.AllocateUninitializedArray<byte>(128 * 1024, pinned: true);
    private bool basic = true;

    // needsSession picks, by name, the processes whose session must be known.
    public ICollection<Entry> Read(Func<string, bool> needsSession)
    {
        (previous, current) = (current, previous);
        current.Clear();
        basic = basic && Query(BasicInformation) >= 0;
        if (basic)
        {
            bool missing = false;
            for (int offset = 0, next = 1; next != 0; offset += next)
            {
                var info = MemoryMarshal.Read<BasicInfo>(buffer.AsSpan(offset));
                next = (int)info.Next;
                var entry = Add((int)info.Id, info.Sequence, info.Name, null);
                missing |= entry.Session is null && needsSession(entry.Name);
            }
            if (!missing) return current.Values;
        }
        if (Query(FullInformation) is var status and < 0) throw new IOException($"프로세스 목록을 읽지 못했습니다. (0x{status:X8})");
        for (int offset = 0, next = 1; next != 0; offset += next)
        {
            var info = MemoryMarshal.Read<FullInfo>(buffer.AsSpan(offset));
            next = (int)info.Next;
            int id = (int)info.Id;
            if (!basic) Add(id, info.CreateTime, info.Name, (int)info.Session);
            else if (current.TryGetValue(id, out var entry) && entry.Session is null && NameOf(id, info.Name) == entry.Name)
                current[id] = entry with { Session = (int)info.Session };
        }
        if (basic)
            foreach (var entry in current.Values.Where(e => e.Session is null).ToArray()) current[entry.Id] = entry with { Session = -1 };
        return current.Values;
    }
    private Entry Add(int id, long stamp, in UnicodeString image, int? session)
    {
        var entry = previous.TryGetValue(id, out var known) && known.Stamp == stamp ? known : new(id, NameOf(id, image), null, stamp);
        return current[id] = session is null ? entry : entry with { Session = session };
    }
    // Process.ProcessName's rules: no folder, no ".exe", and fixed names for the kernel processes without an image.
    private string NameOf(int id, in UnicodeString image)
    {
        if (image.Buffer == 0) return id switch { 0 => "Idle", 4 => "System", _ => id.ToString(CultureInfo.InvariantCulture) };
        ReadOnlySpan<char> name = MemoryMarshal.Cast<byte, char>(buffer.AsSpan((int)(image.Buffer - Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0)), image.Length));
        name = name[(name.LastIndexOf('\\') + 1)..];
        return (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name).ToString();
    }
    // The image names point into the buffer, which is pinned so they stay valid after the call.
    private int Query(int infoClass)
    {
        while (true)
        {
            int status = NtQuerySystemInformation(infoClass, buffer, buffer.Length, out int needed);
            if (status != unchecked((int)0xC0000004)) return status; // STATUS_INFO_LENGTH_MISMATCH
            // Processes can start before the next call, so leave room.
            buffer = GC.AllocateUninitializedArray<byte>(Math.Max(needed, buffer.Length) + 64 * 1024, pinned: true);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, byte[] buffer, int length, out int returned);
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString { public readonly ushort Length, MaximumLength; public readonly nint Buffer; }
    // SYSTEM_BASICPROCESS_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct BasicInfo
    {
        public readonly uint Next;
        public readonly nint Id, ParentId;
        public readonly long Sequence;
        public readonly UnicodeString Name;
    }
    // The start of SYSTEM_PROCESS_INFORMATION; its threads follow each entry.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FullInfo
    {
        public readonly uint Next, Threads;
        public readonly long WorkingSetPrivate;
        public readonly uint HardFaults, ThreadsHighWatermark;
        public readonly long CycleTime, CreateTime, UserTime, KernelTime;
        public readonly UnicodeString Name;
        public readonly int BasePriority;
        public readonly nint Id, ParentId;
        public readonly uint Handles, Session;
    }
}
