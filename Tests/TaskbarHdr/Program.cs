using System.Runtime.InteropServices;
using System.Text.Json;
using PersonalTools;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

// Taskbar HDR buttons. No arguments: drawing, placement and settings checks plus read-only readings of this PC's
// taskbars. --show [--out <dir>]: real buttons on every taskbar for about 10 s, driven by a fake HDR service so no
// monitor changes, with a PNG of each taskbar's right end at every step.
if (args.Contains("--show")) { await Show(args); return; }

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action run) { run(); Console.WriteLine("PASS " + name); passed++; }
static int Alpha(int pixel) => pixel >>> 24;
static (int R, int G, int B) Rgb(int pixel) => (pixel >> 16 & 255, pixel >> 8 & 255, pixel & 255);
var dark = new TaskbarHdrTheme(false, 0x4CC2FF);
// At 150 % the badge spans x 8..53 and y 16..43 with a 2 px stroke.
const double scale = 1.5;
int size = TaskbarHdrPaint.Size(scale);
int At(int[] pixels, int x, int y) => pixels[y * size + x];

Test("every look and scale is a square that takes clicks everywhere", () =>
{
    foreach (double s in new[] { 1, 1.25, 1.5, 1.75, 2, 2.5 })
        foreach (var kind in Enum.GetValues<TaskbarHdrKind>())
            foreach (bool hover in new[] { false, true })
            {
                var pixels = TaskbarHdrPaint.Render(new(kind, true, hover), dark, s);
                Check(pixels.Length == TaskbarHdrPaint.Size(s) * TaskbarHdrPaint.Size(s), $"{kind} at {s}: size");
                Check(pixels.All(p => Alpha(p) >= 1), $"{kind} at {s}: a pixel lets clicks through");
            }
});
Test("off is an outline and on is filled with the accent", () =>
{
    var off = TaskbarHdrPaint.Render(new(TaskbarHdrKind.Off, false), dark, scale);
    var on = TaskbarHdrPaint.Render(new(TaskbarHdrKind.On, true), dark, scale);
    Check(Alpha(At(off, 8, 29)) is > 200 and < 235 && Rgb(At(off, 8, 29)).R == Alpha(At(off, 8, 29)), "outline is not white at 86 %");
    Check(Alpha(At(off, 12, 20)) == 1, "the off badge is filled");
    Check(Alpha(At(on, 12, 20)) == 255 && Rgb(At(on, 12, 20)) == (0x4C, 0xC2, 0xFF), "the on badge is not the accent");
    Check(Alpha(At(off, 2, 2)) == 1, "the corner is drawn without hover");
    var hover = TaskbarHdrPaint.Render(new(TaskbarHdrKind.Off, false, Hover: true), dark, scale);
    Check(Alpha(At(hover, 2, 2)) is > 10 and < 20, "hover fill is not the taskbar's 6 %");
});
Test("the lettering is centred in the badge", () =>
{
    var off = TaskbarHdrPaint.Render(new(TaskbarHdrKind.Off, false), dark, scale);
    double sum = 0, sx = 0, sy = 0;
    for (int y = 19; y < 40; y++)
        for (int x = 11; x < 50; x++) { int a = Alpha(At(off, x, y)) - 1; sum += a; sx += a * (x + .5); sy += a * (y + .5); }
    Console.WriteLine($"     text centre {sx / sum:F2}, {sy / sum:F2}; badge centre 30.50, 29.50");
    Check(sum > 255 * 40, "no lettering");
    Check(Math.Abs(sx / sum - 30.5) < 1.5 && Math.Abs(sy / sum - 29.5) < 1.5, "lettering is off centre");
});
Test("the auto dot sits in a ring cut out of the badge", () =>
{
    var auto = TaskbarHdrPaint.Render(new(TaskbarHdrKind.Auto, true), dark, scale);
    var on = TaskbarHdrPaint.Render(new(TaskbarHdrKind.On, true), dark, scale);
    Check(Alpha(At(auto, 55, 15)) == 255 && Rgb(At(auto, 55, 15)) == (255, 255, 255), "no white dot");
    Check(Alpha(At(on, 51, 19)) == 255 && Alpha(At(auto, 51, 19)) < 40, "the ring is not cut out");
    var limited = TaskbarHdrPaint.Render(new(TaskbarHdrKind.Restricted, false), dark, scale);
    Check(Rgb(At(limited, 55, 15)) == (0xFC, 0xE1, 0x00), "restricted dot is not the caution colour");
});
Test("the busy bar breathes", () =>
{
    int Width(double phase)
    {
        var busy = TaskbarHdrPaint.Render(new(TaskbarHdrKind.Busy, true, Phase: phase), dark, scale);
        return Enumerable.Range(0, size).Count(x => Alpha(At(busy, x, 53)) > 128);
    }
    Console.WriteLine($"     bar {Width(0)} px at rest, {Width(.5)} px at full");
    Check(Width(0) is >= 4 and <= 8 && Width(.5) is >= 22 and <= 26, "bar width");
});
Test("placement left of the tray, centred, and nowhere on a vertical taskbar", () =>
{
    Check(TaskbarHdrButtons.Place(new(0, 2088, 3840, 2160), 3423, 1.5) == new TaskbarHdrButtons.Rect(3354, 6, 3414, 66), "primary at 150 %");
    Check(TaskbarHdrButtons.Place(new(3840, 144, 7680, 240), 7524, 2) == new TaskbarHdrButtons.Rect(3592, 8, 3672, 88), "secondary at 200 %");
    Check(TaskbarHdrButtons.Place(new(0, 0, 72, 2000), 50, 1.5) == default, "vertical taskbar");
    Check(TaskbarHdrButtons.Place(new(0, 0, 3840, 72), 1000, 1.5) == default, "tray past the middle");
});
Test("settings written before the taskbar buttons keep them on", () =>
{
    var old = JsonSerializer.Deserialize<HdrSettings>("{\"Enabled\":false,\"Games\":true,\"DisabledGames\":[\"steam:1\"]}")!;
    Check(old is { TaskbarButtons: true, TaskbarHidden: null, Enabled: false }, "defaults lost");
    var round = JsonSerializer.Deserialize<HdrSettings>(JsonSerializer.Serialize(new HdrSettings(TaskbarButtons: false, TaskbarHidden: ["a"])))!;
    Check(round is { TaskbarButtons: false, TaskbarHidden: ["a"] }, "round trip");
    var wire = JsonSerializer.Deserialize<HdrSnapshot>("{\"Monitors\":[],\"Programs\":[],\"Error\":null}")!;
    Check(wire.TaskbarButtons, "a snapshot from an older app hides the buttons");
});
Test("every monitor's GDI name maps to its id", () =>
{
    var names = HdrDisplay.GdiNames();
    var monitors = new HdrDisplay().Read();
    foreach (var (gdi, id) in names) Console.WriteLine($"     {gdi} → {monitors.FirstOrDefault(m => m.Id == id)?.Name ?? id}");
    Check(monitors.All(m => names.ContainsValue(m.Id)), "a monitor has no GDI name");
});
Test("UI Automation finds the tray on every taskbar", () =>
{
    nint primary = FindWindowEx(0, 0, "Shell_TrayWnd", null);
    nint tray = FindWindowEx(primary, 0, "TrayNotifyWnd", null);
    GetWindowRect(tray, out var area);
    int? left = TaskbarUia.TrayLeft(primary);
    Console.WriteLine($"     primary: TrayNotifyWnd {area.Left}, UI Automation {left}");
    Check(left is { } l && Math.Abs(l - area.Left) <= 6, "primary tray");
    for (nint bar = 0; (bar = FindWindowEx(0, bar, "Shell_SecondaryTrayWnd", null)) != 0;)
    {
        GetWindowRect(bar, out var bounds);
        int? clock = TaskbarUia.TrayLeft(bar);
        double s = GetDpiForWindow(bar) / 96.0;
        Console.WriteLine($"     secondary {bounds.Left},{bounds.Top}: clock at {clock}, {(bounds.Right - clock) / s:F1} DIP from the right");
        Check(clock is { } c && c > (bounds.Left + bounds.Right) / 2 && c < bounds.Right, "secondary clock");
    }
});
Console.WriteLine($"{passed} tests passed.");

static async Task Show(string[] args)
{
    string folder = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "captures");
    Directory.CreateDirectory(folder);
    var monitors = new HdrDisplay().Read().Select(m => m with { Enabled = false }).ToArray();
    var fake = new FakeSource(new HdrSnapshot(monitors, [], null, Active: []));
    using var buttons = new TaskbarHdrButtons(fake, card => Console.WriteLine(card ? "open: taskbar card" : "open: HDR page"));
    buttons.Start();
    await Task.Delay(2500);
    var windows = buttons.Windows;
    nint primaryBar = FindWindowEx(0, 0, "Shell_TrayWnd", null);
    Console.WriteLine($"{windows.Length} buttons for {monitors.Length} monitors");
    foreach (var window in windows)
    {
        GetWindowRect(window, out var r);
        Console.WriteLine($"  parent {(GetParent(window) == primaryBar ? "Shell_TrayWnd" : "Shell_SecondaryTrayWnd")} visible {IsWindowVisible(window)} at {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}");
    }
    await Capture(folder, "1-off");
    // Primary turned on by "a game", one more on, the laptop panel held back by Windows.
    var primary = monitors.First(m => m.Primary);
    var other = monitors.FirstOrDefault(m => !m.Primary && !m.Internal);
    var laptop = monitors.FirstOrDefault(m => m.Internal);
    fake.Publish(fake.Snapshot with
    {
        Monitors = monitors.Select(m => m.Id == primary.Id || m.Id == other?.Id ? m with { Enabled = true }
            : m.Id == laptop?.Id ? m with { Error = "Windows 정책 또는 전원 상태가 HDR을 제한합니다." } : m).ToArray(),
        Active = [new(primary.Id, false, false, DateTimeOffset.Now, ["테스트 게임"])],
    });
    await Task.Delay(800);
    await Capture(folder, "2-states");
    int main = Array.FindIndex(windows, w => GetParent(w) == primaryBar);
    buttons.Preview(main, 1);
    await Task.Delay(700);
    await Capture(folder, "3-tooltip");
    buttons.Preview(main, 0);
    // A click on every secondary button: the fake service takes 1.5 s, like a real switch.
    foreach (var window in windows.Where(w => GetParent(w) != primaryBar))
    {
        PostMessage(window, 0x201, 1, 12 << 16 | 12);
        PostMessage(window, 0x202, 0, 12 << 16 | 12);
    }
    await Task.Delay(500);
    await Capture(folder, "4-busy");
    await Task.Delay(1800);
    await Capture(folder, "5-after");
    buttons.Preview(main, 2);
    await Task.Delay(700);
    await Capture(folder, "6-menu");
    await Task.Delay(1300);
    Console.WriteLine($"captures in {folder}");
}
static async Task Capture(string folder, string name)
{
    int index = 0;
    var bars = new List<nint> { FindWindowEx(0, 0, "Shell_TrayWnd", null) };
    for (nint bar = 0; (bar = FindWindowEx(0, bar, "Shell_SecondaryTrayWnd", null)) != 0;) bars.Add(bar);
    foreach (var bar in bars)
    {
        GetWindowRect(bar, out var r);
        double s = GetDpiForWindow(bar) / 96.0;
        int width = Math.Min(r.Right - r.Left, (int)(560 * s)), above = (int)(230 * s);
        int x = r.Right - width, y = r.Top - above, height = r.Bottom - r.Top + above;
        nint screen = GetDC(0), dc = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        nint bitmap = CreateDIBSection(dc, ref info, 0, out nint bits, 0, 0);
        var bytes = new byte[width * height * 4];
        nint old = SelectObject(dc, bitmap);
        BitBlt(dc, 0, 0, width, height, screen, x, y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT, for layered windows
        Marshal.Copy(bits, bytes, 0, bytes.Length);
        SelectObject(dc, old);
        DeleteObject(bitmap); DeleteDC(dc); ReleaseDC(0, screen);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bytes);
        await encoder.FlushAsync();
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var png = new byte[stream.Size];
        reader.ReadBytes(png);
        await File.WriteAllBytesAsync(Path.Combine(folder, $"{name}-{index++}.png"), png);
    }
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindowEx(nint parent, nint after, string type, string? name);
[DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out TaskbarHdrButtons.Rect rect);
[DllImport("user32.dll")] static extern nint GetParent(nint window);
[DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
[DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
[DllImport("user32.dll", EntryPoint = "PostMessageW")] static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
[DllImport("user32.dll")] static extern nint GetDC(nint window);
[DllImport("user32.dll")] static extern int ReleaseDC(nint window, nint dc);
[DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
[DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
[DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
[DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint item);
[DllImport("gdi32.dll")] static extern bool DeleteObject(nint item);
[DllImport("gdi32.dll")] static extern bool BitBlt(nint dc, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);

[StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, Used, Important; }

sealed class FakeSource(HdrSnapshot snapshot) : ITaskbarHdrSource
{
    public HdrSnapshot Snapshot { get; private set; } = snapshot;
    public event Action<HdrSnapshot>? Changed;
    public void Publish(HdrSnapshot value) { lock (this) { Snapshot = value; Changed?.Invoke(value); } }
    // Serialized like the real controller's gate, so clicks on several taskbars do not undo each other.
    public async Task<HdrSnapshot> RequestAsync(HdrRequest request)
    {
        await Task.Delay(1500);
        lock (this) Publish(Snapshot with { Monitors = Snapshot.Monitors.Select(m => m.Id == request.MonitorId ? m with { Enabled = request.Enabled } : m).ToArray() });
        return Snapshot;
    }
}
