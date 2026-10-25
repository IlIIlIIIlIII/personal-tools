using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace PersonalTools;

internal enum TaskbarHdrKind { Off, On, Busy, Auto, Restricted }
// What one taskbar button shows. Lit is the badge's fill: during Busy it is the state being switched to.
internal readonly record struct TaskbarHdrLook(TaskbarHdrKind Kind, bool Lit, bool Hover = false, bool Pressed = false, double Phase = 0);
// Light follows SystemUsesLightTheme (the taskbar's theme, not the apps'). Accent is 0xRRGGBB: the shade Windows
// puts on that theme's accent fills, AccentLight2 on dark and AccentDark1 on light.
internal readonly record struct TaskbarHdrTheme(bool Light, uint Accent)
{
    public static TaskbarHdrTheme Read()
    {
        bool light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value != 0;
        uint accent = light ? 0x0067C0u : 0x4CC2FFu;
        try
        {
            var color = new UISettings().GetColorValue(light ? UIColorType.AccentDark1 : UIColorType.AccentLight2);
            accent = (uint)(color.R << 16 | color.G << 8 | color.B);
        }
        catch { }
        return new(light, accent);
    }
}

// Draws the taskbar HDR badge (design canvas https://claude.ai/artifact/M8LZqXcTbkkArBSbxrbSPG, variant A) as
// premultiplied BGRA for UpdateLayeredWindow. Shapes are signed distances, so every scale gets clean edges;
// only the "HDR" lettering comes from GDI.
internal static class TaskbarHdrPaint
{
    public const int SizeDip = 40;
    private static readonly Dictionary<(int, int, int), byte[]> Masks = [];
    public static int Size(double scale) => (int)Math.Round(SizeDip * scale);

    public static int[] Render(TaskbarHdrLook look, TaskbarHdrTheme theme, double scale)
    {
        int size = Size(scale);
        var px = new float[size * size * 4];
        float s = (float)scale;
        var fore = theme.Light ? (0f, 0f, 0f) : (1f, 1f, 1f);
        var accent = Rgb(theme.Accent);
        // The taskbar's own hover and pressed fills.
        float fill = look.Pressed ? theme.Light ? .0241f : .0419f : look.Hover ? theme.Light ? .0373f : .0605f : 0;
        if (fill > 0) Shape(px, size, (x, y) => RoundRect(x, y, 0, 0, size, size, 4 * s), fore, fill);

        // A 30×18 badge at (5, 11) with its outer edge on whole pixels.
        float x0 = MathF.Round(5 * s), y0 = MathF.Round(11 * s), x1 = x0 + MathF.Round(30 * s), y1 = y0 + MathF.Round(18 * s);
        float radius = 4 * s, stroke = Math.Max(1, (int)(1.25 * scale + .25));
        float opacity = look.Kind switch { TaskbarHdrKind.Busy => .55f, TaskbarHdrKind.Restricted => .38f, _ => look.Pressed ? .79f : 1f };
        bool lit = look.Lit && look.Kind != TaskbarHdrKind.Restricted;
        bool dot = look.Kind is TaskbarHdrKind.Auto or TaskbarHdrKind.Restricted;
        float cx = x1 + 1.5f * s, cy = y0 - .5f * s, dotRadius = 3.5f * s;
        // A ring around the dot is cut out of the badge so the taskbar shows through, whatever its color.
        float Keep(float x, float y) => dot ? Clamp(.5f + Circle(x, y, cx, cy, dotRadius + 2 * s)) : 1;
        if (lit) Shape(px, size, (x, y) => RoundRect(x, y, x0, y0, x1, y1, radius) * Keep(x, y), accent, opacity);
        else Shape(px, size, (x, y) => (RoundRect(x, y, x0, y0, x1, y1, radius)
            - RoundRect(x, y, x0 + stroke, y0 + stroke, x1 - stroke, y1 - stroke, Math.Max(0, radius - stroke))) * Keep(x, y), fore, .86f * opacity);
        int mw = (int)(x1 - x0), mh = (int)(y1 - y0);
        var mask = TextMask(scale, mw, mh);
        var ink = lit ? theme.Light ? (1f, 1f, 1f) : (0f, 0f, 0f) : fore;
        Shape(px, size, (x, y) =>
        {
            int mx = (int)(x - x0), my = (int)(y - y0);
            return x < x0 || y < y0 || mx >= mw || my >= mh ? 0 : mask[my * mw + mx] / 255f * Keep(x, y);
        }, ink, (lit ? 1f : .86f) * opacity);
        if (dot)
        {
            var color = look.Kind == TaskbarHdrKind.Restricted ? Rgb(theme.Light ? 0x9D5D00u : 0xFCE100u) : theme.Light ? (.1f, .1f, .1f) : (1f, 1f, 1f);
            Shape(px, size, (x, y) => Clamp(.5f - Circle(x, y, cx, cy, dotRadius)), color, 1);
        }
        if (look.Kind == TaskbarHdrKind.Busy)
        {
            // An indeterminate bar under the badge that breathes once a second.
            float width = 16 * s * (float)(.25 + .75 * (.5 - .5 * Math.Cos(2 * Math.PI * look.Phase)));
            float left = size / 2f - width / 2, top = 34 * s, bottom = 37 * s;
            Shape(px, size, (x, y) => RoundRect(x, y, left, top, left + width, bottom, Math.Min(width, bottom - top) / 2), accent, 1);
        }
        var result = new int[size * size];
        for (int i = 0; i < result.Length; i++)
        {
            float a = px[i * 4 + 3];
            // Alpha 0 lets clicks fall through to the taskbar; 1/255 keeps the whole square clickable and stays invisible.
            result[i] = a < 1 / 255f ? 0x01010101 : Byte(px[i * 4 + 2]) | Byte(px[i * 4 + 1]) << 8 | Byte(px[i * 4]) << 16 | Byte(a) << 24;
        }
        return result;
    }

    // A Segoe Fluent Icons glyph as a premultiplied 32-bit bitmap, for menu items. The caller deletes it.
    public static nint Glyph(char glyph, int size, uint rgb)
    {
        var coverage = Draw(size, size, "Segoe Fluent Icons", size, 400, 0, (dc, width, height) =>
        {
            var area = new Rect { Right = width, Bottom = height };
            DrawText(dc, glyph.ToString(), 1, ref area, 0x25); // DT_CENTER | DT_VCENTER | DT_SINGLELINE
        });
        var (r, g, b) = Rgb(rgb);
        var pixels = new int[size * size];
        for (int i = 0; i < pixels.Length; i++)
        {
            float a = coverage[i] / 255f;
            pixels[i] = Byte(b * a) | Byte(g * a) << 8 | Byte(r * a) << 16 | coverage[i] << 24;
        }
        var info = new BitmapInfo { Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32 };
        nint bitmap = CreateDIBSection(0, ref info, 0, out nint bits, 0, 0);
        if (bitmap != 0) Marshal.Copy(pixels, 0, bits, pixels.Length);
        return bitmap;
    }

    // "HDR" in Segoe UI Bold 10 DIP with .04 em tracking, its cap height centred in the badge.
    private static byte[] TextMask(double scale, int width, int height)
    {
        int em = (int)Math.Round(10 * scale);
        lock (Masks)
        {
            if (Masks.TryGetValue((em, width, height), out var cached)) return cached;
            var mask = Draw(width, height, "Segoe UI", em, 700, (int)Math.Round(.04 * em), (dc, w, h) =>
            {
                int extra = (int)Math.Round(.04 * em);
                GetTextExtentPoint32(dc, "HDR", 3, out var extent);
                GetTextMetrics(dc, out var metrics);
                float cap = .7f * em;
                TextOut(dc, (int)Math.Round((w - extent.Cx + extra) / 2f), (int)Math.Round((h - cap) / 2 - (metrics.Ascent - cap)), "HDR", 3);
            });
            Masks[(em, width, height)] = mask;
            return mask;
        }
    }
    // Draws white text on black with grayscale antialiasing and returns the coverage per pixel.
    private static byte[] Draw(int width, int height, string face, int em, int weight, int tracking, Action<nint, int, int> draw)
    {
        var mask = new byte[width * height];
        nint dc = CreateCompatibleDC(0);
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        nint bitmap = CreateDIBSection(dc, ref info, 0, out nint bits, 0, 0);
        nint font = CreateFont(-em, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0, face); // ANTIALIASED_QUALITY
        try
        {
            if (bitmap == 0) return mask;
            nint oldBitmap = SelectObject(dc, bitmap), oldFont = SelectObject(dc, font);
            SetTextColor(dc, 0xFFFFFF);
            SetBkMode(dc, 1);
            SetTextCharacterExtra(dc, tracking);
            draw(dc, width, height);
            GdiFlush();
            var pixels = new int[width * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(pixels[i] >> 8);
            SelectObject(dc, oldFont);
            SelectObject(dc, oldBitmap);
        }
        finally { DeleteObject(font); if (bitmap != 0) DeleteObject(bitmap); DeleteDC(dc); }
        return mask;
    }

    private static void Shape(float[] px, int size, Func<float, float, float> coverage, (float R, float G, float B) color, float alpha)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float a = Clamp(coverage(x + .5f, y + .5f)) * alpha;
                if (a <= 0) continue;
                int i = (y * size + x) * 4;
                float keep = 1 - a;
                px[i] = color.R * a + px[i] * keep;
                px[i + 1] = color.G * a + px[i + 1] * keep;
                px[i + 2] = color.B * a + px[i + 2] * keep;
                px[i + 3] = a + px[i + 3] * keep;
            }
    }
    // Coverage of a rounded rectangle at a pixel centre, from its signed distance.
    internal static float RoundRect(float x, float y, float x0, float y0, float x1, float y1, float r)
    {
        float hx = (x1 - x0) / 2, hy = (y1 - y0) / 2;
        if (hx <= 0 || hy <= 0) return 0;
        r = Math.Min(r, Math.Min(hx, hy));
        float qx = Math.Abs(x - x0 - hx) - (hx - r), qy = Math.Abs(y - y0 - hy) - (hy - r);
        float outside = MathF.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
        return Clamp(.5f - (outside + Math.Min(Math.Max(qx, qy), 0) - r));
    }
    private static float Circle(float x, float y, float cx, float cy, float r) => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;
    private static float Clamp(float value) => Math.Clamp(value, 0, 1);
    private static int Byte(float value) => (int)MathF.Round(Clamp(value) * 255);
    private static (float R, float G, float B) Rgb(uint rgb) => ((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f);

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Extent { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TextMetric
    {
        public int Height, Ascent, Descent, InternalLeading, ExternalLeading, AveCharWidth, MaxCharWidth, Weight, Overhang, AspectX, AspectY;
        public char First, Last, Default, Break;
        public byte Italic, Underlined, StruckOut, PitchAndFamily, Charset;
    }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike,
        uint charset, uint precision, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextCharacterExtra(nint dc, int extra);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")] private static extern bool GetTextExtentPoint32(nint dc, string text, int length, out Extent size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextMetricsW")] private static extern bool GetTextMetrics(nint dc, out TextMetric metrics);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW")] private static extern bool TextOut(nint dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")] private static extern int DrawText(nint dc, string text, int length, ref Rect rect, uint format);
}
