using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace PersonalTools;

// Places a popup at a rectangle already computed in the destination monitor's pixels.
// When that monitor has another DPI, Windows sends WM_DPICHANGED with the old pixel size
// rescaled for the new DPI (550×124 became 1100×248 going to 200 %, 138×31 going to
// 100 %) and WinUI applies it, so popups opened doubled or cut in half after switching
// between the 200 % and 100 % displays. The subclass hands on the intended rectangle.
internal sealed class WindowDpiPlacement : IDisposable
{
    private const nuint SubclassId = 0x50445049; // "PDPI"
    private readonly nint window;
    private readonly AppWindow appWindow;
    private readonly SubclassProc proc;
    private readonly bool noFrame;
    private RectInt32 target;
    private uint targetDpi;
    // noFrame drops the non-client area. SetBorderAndTitleBar(false, false) leaves
    // WS_DLGFRAME, which Windows drew as a classic white frame inside the DWM border that
    // also took 3 px off each side of the content (the panel got 298 of its 304 DIP).
    // DWM still draws the rounded corners and the themed border.
    internal WindowDpiPlacement(nint window, AppWindow appWindow, bool noFrame = false)
    {
        this.window = window; this.appWindow = appWindow; this.noFrame = noFrame;
        proc = OnMessage;
        SetWindowSubclass(window, proc, SubclassId, 0);
        if (noFrame) SetWindowPos(window, 0, 0, 0, 0, 0, 0x0037); // SWP_FRAMECHANGED | NOMOVE | NOSIZE | NOZORDER | NOACTIVATE
    }
    internal static uint DpiOf(nint monitor) => GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 && x > 0 ? x : 96;
    // rect is in the pixels of the monitor that will hold most of it.
    internal void MoveAndResize(RectInt32 rect)
    {
        target = rect;
        targetDpi = DpiOf(MonitorFromRect(new Rect { Left = rect.X, Top = rect.Y, Right = rect.X + rect.Width, Bottom = rect.Y + rect.Height }, 2));
        appWindow.MoveAndResize(rect);
        Restore();
    }
    // Puts the window back on the last target if something resized it meanwhile.
    internal void Restore() { if (targetDpi != 0 && !AtTarget()) appWindow.MoveAndResize(target); }
    private bool AtTarget() => GetWindowRect(window, out var now) && now.Left == target.X && now.Top == target.Y
        && now.Right - now.Left == target.Width && now.Bottom - now.Top == target.Height;
    private nint OnMessage(nint hwnd, uint message, nint w, nint l, nuint id, nuint data)
    {
        if (noFrame && message == 0x0083 && w != 0) return 0; // WM_NCCALCSIZE: client = window
        if (message != 0x02E0 || targetDpi == 0) return DefSubclassProc(hwnd, message, w, l);
        // On the target's monitor take the target; anywhere else (an animation passing over
        // another monitor) keep the current pixels, since every placement sets its own size.
        bool arriving = (uint)(w & 0xFFFF) == targetDpi;
        var keep = new Rect { Left = target.X, Top = target.Y, Right = target.X + target.Width, Bottom = target.Y + target.Height };
        if (!arriving && !GetWindowRect(hwnd, out keep)) return DefSubclassProc(hwnd, message, w, l);
        Marshal.StructureToPtr(keep, l, false);
        nint result = DefSubclassProc(hwnd, message, w, l);
        if (arriving && !AtTarget()) SetWindowPos(hwnd, 0, target.X, target.Y, target.Width, target.Height, 0x0014); // SWP_NOZORDER | SWP_NOACTIVATE
        return result;
    }
    public void Dispose() => RemoveWindowSubclass(window, proc, SubclassId);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint SubclassProc(nint window, uint message, nint w, nint l, nuint id, nuint data);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(in Rect rect, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint w, nint l);
}
