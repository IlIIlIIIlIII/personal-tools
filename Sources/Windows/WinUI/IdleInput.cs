using System.Runtime.InteropServices;

namespace PersonalTools;

internal interface IIdleInput
{
    // Time of the last user input on the Environment.TickCount64 clock. Called on one thread only.
    long LastInput(long now);
}

// Keyboard, mouse, touch and pen come from GetLastInputInfo. XInput gamepads do not reset that
// timer, so they are polled; a controller resting in its dead zone does not count as input.
internal sealed class IdleInput : IIdleInput
{
    private readonly XInputState?[] pads = new XInputState?[4];
    private bool padsAvailable = true;
    private long lastPad = long.MinValue;

    public long LastInput(long now)
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        // Without an answer, treat it as fresh input so the monitors never go dark by mistake.
        long keyboard = GetLastInputInfo(ref info) ? now - (uint)((uint)now - info.Time) : now;
        PollPads(now);
        return Math.Max(keyboard, lastPad);
    }

    private void PollPads(long now)
    {
        if (!padsAvailable) return;
        for (uint slot = 0; slot < pads.Length; slot++)
        {
            uint result;
            XInputState state;
            try { result = XInputGetState(slot, out state); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { padsAvailable = false; return; }
            if (result != 0) { pads[slot] = null; continue; }
            // Connecting a controller (pressing its guide button) counts as input.
            if (pads[slot] is not { } previous || IsActive(state, previous)) lastPad = now;
            pads[slot] = state;
        }
    }

    internal static bool IsActive(XInputState now, XInputState before)
    {
        if (now.Packet == before.Packet) return false;
        var pad = now.Pad;
        return pad.Buttons != before.Pad.Buttons || pad.Buttons != 0 || pad.LeftTrigger > TriggerThreshold || pad.RightTrigger > TriggerThreshold ||
            Outside(pad.ThumbLX, pad.ThumbLY, LeftDeadZone) || Outside(pad.ThumbRX, pad.ThumbRY, RightDeadZone);
    }
    // XInput's documented dead zones (XINPUT_GAMEPAD_*_THUMB_DEADZONE, XINPUT_GAMEPAD_TRIGGER_THRESHOLD).
    private const int LeftDeadZone = 7849, RightDeadZone = 8689, TriggerThreshold = 30;
    private static bool Outside(short x, short y, int deadZone) => (long)x * x + (long)y * y > (long)deadZone * deadZone;

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] internal struct XInputGamepad
    {
        public ushort Buttons; public byte LeftTrigger, RightTrigger; public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct XInputState { public uint Packet; public XInputGamepad Pad; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint slot, out XInputState state);
}
