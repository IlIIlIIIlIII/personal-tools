using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Text.Json;
using Windows.UI;

namespace PersonalTools;

// 조명 page (design canvas https://claude.ai/artifact/6EANCaKQMaEjWsb5MN7Gz7). The preview is drawn
// from the key matrix the firmware reports and painted with the live LED colors.
public sealed partial class LightingPage : UserControl
{
    private static readonly (string Name, LightingColor Color)[] Swatches =
    [
        ("흰색", new(0xFF, 0xFF, 0xFF)), ("빨강", new(0xE8, 0x11, 0x23)), ("주황", new(0xFF, 0x8C, 0x00)),
        ("노랑", new(0xFF, 0xD8, 0x00)), ("초록", new(0x10, 0xC4, 0x5C)), ("하늘색", LightingColor.LegionBlue),
        ("파랑", new(0x33, 0x55, 0xFF)), ("보라", new(0x8E, 0x3C, 0xF7)), ("분홍", new(0xFF, 0x3D, 0xA8)),
    ];
    private static readonly string[] Speeds = ["느리게", "보통", "빠르게"];
    // Preview geometry, in the 640×200 canvas of the design.
    private const double KeyLeft = 264, KeyTop = 22, KeyPitchX = 16, KeyPitchY = 14, DeckTop = 12, DeckHeight = 172;
    private readonly Dictionary<LightingZone, bool> expanded = new() { [LightingZone.Keyboard] = true, [LightingZone.Bar] = true, [LightingZone.Logo] = false };
    private readonly Dictionary<ushort, List<SolidColorBrush>> lights = [];
    private IReadOnlyDictionary<ushort, LightingColor>? lastColors;
    private LightingController? controller;
    private LightingSnapshot snapshot = LightingSnapshot.Empty;
    private string? rendered, zonesRendered;
    private SpectrumLayout? drawnLayout;
    private ElementTheme drawnTheme;
    private bool drawn, updating, settingUp;
    // An open effect list or color flyout: the zone cards are not rebuilt under it.
    private int holding;
    // The brightness the slider sent; the slider is not moved back while that write is on its way.
    private int? sentBrightness;

    public LightingPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => { drawn = false; DrawPreview(snapshot.Layout); };
    }
    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];
    private Style Local(string key) => (Style)Resources[key];

    internal void Configure(LightingController service)
    {
        controller = service;
        service.Changed += value => DispatcherQueue.TryEnqueue(() => Render(value));
        service.Colors += colors => DispatcherQueue.TryEnqueue(() => Paint(colors));
        Render(service.Snapshot);
    }
    // MainWindow calls these as the page is shown and hidden; the device is read only in between.
    public void Shown() => controller?.Shown();
    public void Hidden() => controller?.Hidden();

    private void Render(LightingSnapshot value, bool force = false)
    {
        string key = value.RenderKey;
        if (!force && rendered == key) return;
        rendered = key;
        snapshot = value;
        updating = true;
        bool ready = value.Ready && value.Found;
        bool own = ready && !value.WindowsControls;
        bool zonesLoaded = value.Zones.Length > 0;

        RenderState(value);
        Show(WindowsBar, ready && value.WindowsControls);
        string? problem = value.Error ?? (value.Ready && !value.Found
            ? "노트북 조명 장치(ITE 048D:C909)를 찾지 못했습니다. 키보드 드라이버가 다시 잡히면 자동으로 다시 찾습니다." : null);
        Show(ErrorBar, problem is not null);
        ErrorBar.Message = problem ?? "";

        BrightnessSlider.IsEnabled = own;
        if (sentBrightness is null || sentBrightness == value.Brightness || value.Error is not null)
        {
            sentBrightness = null;
            BrightnessSlider.Value = value.Brightness;
            BrightnessValue.Text = ready ? value.Brightness.ToString() : "";
        }
        BrightnessDescription.Text = value.WindowsControls
            ? "동적 조명이 켜져 있는 동안은 Windows 설정의 밝기를 따릅니다."
            : "Fn + ↑/↓로 바꿔도 여기에 바로 보입니다. 0이면 키보드, 라이트바, 로고가 모두 꺼집니다.";
        Dim(!own, BrightnessIcon, BrightnessTitle);
        ProfileBox.IsEnabled = own;
        ProfileBox.SelectedIndex = value.Profile is >= 1 and <= SpectrumPacket.Profiles ? value.Profile - 1 : -1;
        Dim(!own, ProfileIcon, ProfileTitle);
        ResetText.Text = value.Profile > 0 ? $"프로필 {value.Profile} 공장 설정으로" : "공장 설정으로";
        ResetButton.IsEnabled = own && zonesLoaded;

        RenderZones(value, own);
        ZonesPending.Visibility = zonesLoaded ? Visibility.Collapsed : Visibility.Visible;
        ZonesPending.Text = value.Ready && !value.Found ? "조명 장치를 찾으면 구역 설정이 여기에 나옵니다." : "노트북 조명을 읽고 있습니다.";
        RenderLegend(value);
        RenderPower(value.PowerLed);
        updating = false;
        DrawPreview(value.Layout);
    }
    private static void Show(InfoBar bar, bool open) { bar.IsOpen = open; bar.Visibility = open ? Visibility.Visible : Visibility.Collapsed; }
    private void Dim(bool dim, params FrameworkElement[] elements)
    {
        foreach (var element in elements)
        {
            if (element is TextBlock text) { if (dim) text.Style = Local("DimText"); else text.ClearValue(TextBlock.StyleProperty); }
            else if (element is FontIcon icon) { if (dim) icon.Style = Local("DimIcon"); else icon.ClearValue(FrameworkElement.StyleProperty); }
        }
    }

    private void RenderState(LightingSnapshot value)
    {
        string text;
        bool ok = false;
        if (!value.Ready) text = "확인 중";
        else if (!value.Found) text = "조명 장치 없음";
        else if (value.WindowsControls) text = "Windows 동적 조명이 제어 중";
        else if (value.Brightness == 0) text = "꺼짐 · 밝기 0";
        else { text = $"노트북 효과 · 프로필 {value.Profile}"; ok = true; }
        StateText.Text = text;
        StateDot.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        StateDotOk.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
    }

    // The zone cards are rebuilt only when what they show changes, never under an open list or
    // flyout, and the keyboard focus goes back to the same control afterwards.
    private void RenderZones(LightingSnapshot value, bool own, bool force = false)
    {
        string key = JsonSerializer.Serialize(new { value.Zones, value.LogoOn, value.WindowsControls, value.Profile, value.Revision, own, expanded = expanded.Values.ToArray() });
        if (!force && key == zonesRendered) return;
        if (holding > 0) return;
        zonesRendered = key;
        string? focusId = null;
        var focusState = FocusState.Programmatic;
        if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is Control focused && Inside(focused, ZoneList))
        {
            focusId = AutomationProperties.GetAutomationId(focused);
            if (focused.FocusState != FocusState.Unfocused) focusState = focused.FocusState;
        }
        bool was = updating;
        updating = true;
        ZoneList.Children.Clear();
        foreach (var zone in value.Zones) ZoneList.Children.Add(ZoneCard(zone, value, own));
        updating = was;
        if (!string.IsNullOrEmpty(focusId) && Find(ZoneList, focusId) is { } again) again.Focus(focusState);
    }
    private static bool Inside(DependencyObject element, DependencyObject container)
    {
        for (var at = element; at is not null; at = VisualTreeHelper.GetParent(at)) if (at == container) return true;
        return false;
    }
    private static Control? Find(DependencyObject root, string id)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Control control && AutomationProperties.GetAutomationId(control) == id) return control;
            if (Find(child, id) is { } found) return found;
        }
        return null;
    }
    private void Hold() => holding++;
    private void Release()
    {
        holding = Math.Max(0, holding - 1);
        if (holding == 0) RenderZones(snapshot, snapshot.Ready && snapshot.Found && !snapshot.WindowsControls);
    }

    // One zone: a card with the current effect, and below it (when open) the effect, color and speed.
    private StackPanel ZoneCard(LightingZoneState zone, LightingSnapshot value, bool own)
    {
        bool open = own && expanded[zone.Zone] && zone.Kind is not null;
        string id = zone.Zone.ToString();
        var panel = new StackPanel();
        var grid = new Grid { ColumnSpacing = 16 };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var icon = new FontIcon { Glyph = zone.Zone switch { LightingZone.Keyboard => "", LightingZone.Bar => "", _ => "" }, FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = LightingController.Name(zone.Zone) };
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Style = StyleOf("CardDescription"),
            Text = zone.Zone switch
            {
                LightingZone.Keyboard => $"키 {zone.KeyCount}개",
                LightingZone.Bar => $"앞 모서리와 양옆 앞쪽 · {zone.KeyCount}구간",
                _ => "LEGION 글자",
            },
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Dim(!own, icon, title);

        var summary = new TextBlock
        {
            Text = (value.WindowsControls ? "저장됨: " : "") + Summary(zone),
            Style = Local("DimText"),
            MaxWidth = 240,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(summary, 2);
        grid.Children.Add(summary);

        if (zone.Zone == LightingZone.Logo)
        {
            var toggle = new ToggleSwitch { IsOn = value.LogoOn, IsEnabled = own, OnContent = "켬", OffContent = "끔", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, "덮개 로고 켜기");
            AutomationProperties.SetAutomationId(toggle, "Logo-switch");
            toggle.Toggled += (_, _) => { if (!updating) controller?.SetLogo(toggle.IsOn); };
            Grid.SetColumn(toggle, 3);
            grid.Children.Add(toggle);
        }

        var chevron = new Button
        {
            Style = StyleOf("SubtleIconButton"),
            Width = 32,
            Height = 32,
            IsEnabled = own && zone.Kind is not null,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = open ? "" : "", FontSize = 12 },
        };
        AutomationProperties.SetName(chevron, LightingController.Name(zone.Zone) + (open ? " 설정 접기" : " 설정 펼치기"));
        AutomationProperties.SetAutomationId(chevron, id + "-chevron");
        chevron.Click += (_, _) => { expanded[zone.Zone] = !expanded[zone.Zone]; RenderZones(snapshot, own); };
        Grid.SetColumn(chevron, 4);
        grid.Children.Add(chevron);

        var header = new Border { Style = StyleOf("SettingCard"), Padding = new Thickness(16, 12, 12, 12), Child = grid };
        if (open) header.CornerRadius = new CornerRadius(4, 4, 0, 0);
        panel.Children.Add(header);
        if (open) panel.Children.Add(ZoneBody(zone, value.Profile));
        return panel;
    }

    // Each control sends only what it changes; the controller fills in the rest from the zone as
    // the firmware has it at that moment.
    private Border ZoneBody(LightingZoneState zone, int profile)
    {
        var kind = zone.Kind!.Value;
        string id = zone.Zone.ToString();
        var rows = new StackPanel();

        var choices = LightingEffects.Choices(zone.Zone).ToList();
        if (!choices.Contains(kind)) choices.Insert(0, kind);
        var effectBox = new ComboBox { MinWidth = 160 };
        foreach (var choice in choices) effectBox.Items.Add(new ComboBoxItem { Content = LightingEffects.Name(choice), Tag = choice });
        effectBox.SelectedIndex = choices.IndexOf(kind);
        AutomationProperties.SetName(effectBox, LightingController.Name(zone.Zone) + " 효과");
        AutomationProperties.SetAutomationId(effectBox, id + "-effect");
        effectBox.DropDownOpened += (_, _) => Hold();
        effectBox.DropDownClosed += (_, _) => Release();
        effectBox.SelectionChanged += (_, _) =>
        {
            if (updating || effectBox.SelectedItem is not ComboBoxItem { Tag: LightingEffectKind next } || next == kind) return;
            controller?.SetZone(zone.Zone, profile, kind: next);
        };
        rows.Children.Add(Row("효과", effectBox, first: true));
        if (LightingEffects.NeedsLenovoApp(kind))
            rows.Children.Add(Row("", new TextBlock { Style = StyleOf("CardDescription"), Text = "이 효과는 Lenovo Legion Space가 소리나 화면을 보내 줄 때만 움직입니다." }));

        if (LightingEffects.HasColor(kind)) rows.Children.Add(Row("색", ColorChoices(zone, profile)));
        if (LightingEffects.HasSpeed(kind))
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < Speeds.Length; i++)
            {
                int speed = i + 1;
                var button = new ToggleButton
                {
                    Content = Speeds[i],
                    IsChecked = zone.Speed == speed,
                    MinWidth = 72,
                    CornerRadius = i == 0 ? new CornerRadius(4, 0, 0, 4) : i == Speeds.Length - 1 ? new CornerRadius(0, 4, 4, 0) : new CornerRadius(0),
                };
                AutomationProperties.SetName(button, $"{LightingController.Name(zone.Zone)} 속도 {Speeds[i]}");
                AutomationProperties.SetAutomationId(button, $"{id}-speed-{speed}");
                button.Click += (_, _) =>
                {
                    foreach (var other in group.Children.OfType<ToggleButton>()) other.IsChecked = ReferenceEquals(other, button);
                    if (zone.Speed != speed) controller?.SetZone(zone.Zone, profile, speed: speed);
                };
                group.Children.Add(button);
            }
            rows.Children.Add(Row("속도", group));
        }
        return new Border { Style = StyleOf("ExpanderBody"), Padding = new Thickness(52, 4, 16, 4), Child = rows };
    }

    private Grid Row(string label, FrameworkElement control, bool first = false)
    {
        var grid = new Grid { Style = Local(first ? "FirstZoneRow" : "ZoneRow") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        control.Margin = new Thickness(0, 6, 0, 6);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    // Preset swatches, the zone's own color when it is not a preset, "+" for any color, 무작위.
    // They wrap onto a second line in a narrow window.
    private VariableSizedWrapGrid ColorChoices(LightingZoneState zone, int profile)
    {
        string id = zone.Zone.ToString();
        var row = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 38, ItemHeight = 38 };
        var colors = Swatches.ToList();
        if (zone.Color is { } own && !zone.Random && !colors.Any(s => s.Color == own)) colors.Add((own.Hex, own));
        foreach (var (name, color) in colors)
        {
            bool chosen = !zone.Random && zone.Color == color;
            bool light = color.R + color.G + color.B > 600;
            var swatch = new Button
            {
                Style = Local(chosen ? light ? "ChosenLightSwatchButton" : "ChosenSwatchButton" : light ? "LightSwatchButton" : "SwatchButton"),
                Background = new SolidColorBrush(ToColor(color)),
            };
            AutomationProperties.SetName(swatch, chosen ? name + ", 선택됨" : name);
            AutomationProperties.SetAutomationId(swatch, $"{id}-color-{color.Hex}");
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) => { if (!chosen) controller?.SetZone(zone.Zone, profile, color: color, random: false); };
            row.Children.Add(swatch);
        }

        var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            Color = ToColor(zone.Color ?? LightingColor.LegionBlue),
        };
        var flyout = new Flyout();
        flyout.Opened += (_, _) => Hold();
        flyout.Closed += (_, _) => Release();
        var apply = new Button { Content = "적용", Style = StyleOf("AccentButtonStyle"), HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (_, _) =>
        {
            var c = picker.Color;
            controller?.SetZone(zone.Zone, profile, color: new LightingColor(c.R, c.G, c.B), random: false);
            flyout.Hide();
        };
        flyout.Content = new StackPanel { Spacing = 12, Children = { picker, apply } };
        var more = new Button { Style = Local("MoreSwatchButton"), Content = new FontIcon { Glyph = "", FontSize = 12 }, Flyout = flyout };
        AutomationProperties.SetName(more, "다른 색 고르기");
        AutomationProperties.SetAutomationId(more, id + "-more");
        ToolTipService.SetToolTip(more, "다른 색 고르기");
        row.Children.Add(more);

        var random = new ToggleButton { Content = "무작위", IsChecked = zone.Random, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(random, LightingController.Name(zone.Zone) + " 무작위 색");
        AutomationProperties.SetAutomationId(random, id + "-random");
        VariableSizedWrapGrid.SetColumnSpan(random, 2);
        random.Click += (_, _) =>
        {
            bool on = random.IsChecked == true;
            if (on != zone.Random) controller?.SetZone(zone.Zone, profile, random: on);
        };
        row.Children.Add(random);
        return row;
    }

    private static string Summary(LightingZoneState zone)
    {
        if (zone.Kind is not { } kind) return "효과 없음";
        if (LightingEffects.NeedsLenovoApp(kind)) return LightingEffects.Name(kind) + " · Lenovo 앱 필요";
        if (!LightingEffects.HasColor(kind)) return LightingEffects.Name(kind);
        return LightingEffects.Name(kind) + " · " + (zone.Random ? "무작위" : zone.Color is { } c ? ColorName(c) : "색 없음");
    }
    private static string ColorName(LightingColor color) => Swatches.FirstOrDefault(s => s.Color == color).Name ?? color.Hex;

    private void RenderLegend(LightingSnapshot value)
    {
        Legend.Children.Clear();
        if (value.Found && value.WindowsControls)
        {
            Legend.Children.Add(LegendItem(new Ellipse { Style = Local("AccentLegendDot") }, "모든 구역 · Windows 동적 조명"));
            return;
        }
        foreach (var zone in value.Zones)
        {
            bool dark = zone.Kind is null || (zone.Zone == LightingZone.Logo && !value.LogoOn) || value.Brightness == 0;
            Brush fill = dark ? new SolidColorBrush(ToColor(0xFF45474Du))
                : zone.Kind is { } kind && LightingEffects.HasColor(kind) && !zone.Random && zone.Color is { } c ? new SolidColorBrush(ToColor(c)) : Rainbow();
            string effect = zone.Zone == LightingZone.Logo && !value.LogoOn ? "꺼짐" : zone.Kind is { } k ? LightingEffects.Name(k) : "효과 없음";
            Legend.Children.Add(LegendItem(new Ellipse { Style = Local("LegendDot"), Fill = fill }, $"{LightingController.Name(zone.Zone)} · {effect}"));
        }
    }
    private static StackPanel LegendItem(Ellipse dot, string text) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children = { dot, new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center } },
    };
    private static LinearGradientBrush Rainbow()
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        uint[] hues = [0xFFFF0000, 0xFFFFD800, 0xFF00FF40, 0xFF00C8FF, 0xFF3355FF, 0xFFFF3DA8];
        for (int i = 0; i < hues.Length; i++) brush.GradientStops.Add(new GradientStop { Color = ToColor(hues[i]), Offset = i / (double)(hues.Length - 1) });
        return brush;
    }

    private void RenderPower(PowerLedState power)
    {
        PowerSetup.Visibility = power.Installed ? Visibility.Collapsed : Visibility.Visible;
        PowerSetup.IsEnabled = !settingUp;
        PowerSwitch.Visibility = power.Installed ? Visibility.Visible : Visibility.Collapsed;
        // While a request runs the switch shows what was asked for, not the state before it.
        PowerSwitch.IsOn = power.Pending && power.Requested is { } asked ? asked : power.On == true;
        PowerSwitch.IsEnabled = power.Installed && !power.Pending;
        bool busy = power.Pending || settingUp;
        PowerBusy.IsActive = busy;
        PowerBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PowerDescription.Text = "색은 전원 모드를 따릅니다: 흰색 균형, 파랑 저소음, 빨강 성능, 보라 사용자 지정."
            + (power.Installed ? "" : " 켜고 끄려면 처음 한 번 관리자 권한으로 설정해야 합니다.");
        if (!settingUp)
        {
            PowerError.Text = power.Error ?? "";
            PowerError.Visibility = power.Error is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // The deck and lid outlines once per layout and theme; key brushes are recolored by Paint.
    private void DrawPreview(SpectrumLayout? layout)
    {
        if (drawn && ReferenceEquals(layout, drawnLayout) && drawnTheme == ActualTheme) return;
        drawn = true;
        drawnLayout = layout;
        drawnTheme = ActualTheme;
        Preview.Children.Clear();
        lights.Clear();
        bool dark = ActualTheme == ElementTheme.Dark;
        var body = ToColor(dark ? 0xFF17181Bu : 0xFF2C2E33u);
        var edge = dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0, 0, 0, 0);
        AddRect(8, 22, 208, 150, 10, new SolidColorBrush(body), edge);
        AddRect(248, DeckTop, 384, DeckHeight, 12, new SolidColorBrush(body), edge);
        AddRect(400, 136, 80, 36, 5, new SolidColorBrush(ToColor(dark ? 0xFF26282Cu : 0xFF3A3C42u)), default);

        var lid = new Grid { Width = 208, Height = 150 };
        lid.Children.Add(new TextBlock
        {
            Text = "LEGION",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = 270,
            Foreground = Light(SpectrumCodes.Logo),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Canvas.SetLeft(lid, 8);
        Canvas.SetTop(lid, 22);
        Preview.Children.Add(lid);

        if (layout is null || layout.Rows.Length == 0)
        {
            // Until the matrix is read: a plain key grid.
            for (int r = 0; r < 6; r++)
                for (int c = 0; c < 21; c++)
                    AddRect(KeyLeft + 8 + c * KeyPitchX + 1.5, KeyTop + 6 + r * KeyPitchY + 1.5, KeyPitchX - 3, KeyPitchY - 3, 2.5, Light(0), default);
            return;
        }
        int rows = layout.Rows.Length;
        int columns = layout.Rows.Max(r => r.Length);
        for (int r = 0; r < rows; r++)
        {
            var row = layout.Rows[r];
            for (int c = 0; c < row.Length;)
            {
                ushort code = row[c];
                int span = 1;
                while (c + span < row.Length && row[c + span] == code) span++;
                var zone = SpectrumCodes.ZoneOf(code);
                if (zone == LightingZone.Keyboard)
                    AddRect(KeyLeft + c * KeyPitchX + 1.5, KeyTop + (r - 1) * KeyPitchY + 1.5, span * KeyPitchX - 3, KeyPitchY - 3, 2.5, Light(code), default);
                else if (zone == LightingZone.Bar && c > 0 && c + span < columns)
                    AddRect(KeyLeft + c * KeyPitchX + 0.5, DeckTop + DeckHeight - 4, span * KeyPitchX - 1, 4, 2, Light(code), default);
                c += span;
            }
        }
        // The bar's side pieces sit in the first and last columns, one row each.
        foreach (int c in new[] { 0, columns - 1 })
        {
            for (int r = 0; r < rows;)
            {
                ushort code = c < layout.Rows[r].Length ? layout.Rows[r][c] : (ushort)0;
                int span = 1;
                while (r + span < rows && c < layout.Rows[r + span].Length && layout.Rows[r + span][c] == code) span++;
                if (SpectrumCodes.ZoneOf(code) == LightingZone.Bar)
                {
                    double top = DeckTop + DeckHeight * r / rows, bottom = DeckTop + DeckHeight * (r + span) / rows;
                    AddRect(c == 0 ? 246 : 630, top + 0.5, 4, bottom - top - 1, 2, Light(code), default);
                }
                r += span;
            }
        }
        if (lastColors is not null) Paint(lastColors);
    }
    private SolidColorBrush Light(ushort code)
    {
        var brush = new SolidColorBrush(ToColor(0xFF45474Du));
        if (!lights.TryGetValue(code, out var list)) lights[code] = list = [];
        list.Add(brush);
        return brush;
    }
    private void AddRect(double x, double y, double width, double height, double radius, Brush fill, Color stroke)
    {
        var rect = new Rectangle { Width = Math.Max(width, 1), Height = Math.Max(height, 1), RadiusX = radius, RadiusY = radius, Fill = fill };
        if (stroke.A > 0) { rect.Stroke = new SolidColorBrush(stroke); rect.StrokeThickness = 1; }
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        Preview.Children.Add(rect);
    }
    private void Paint(IReadOnlyDictionary<ushort, LightingColor> colors)
    {
        lastColors = colors;
        var off = ToColor(0xFF45474Du);
        foreach (var (code, brushes) in lights)
        {
            if (!colors.TryGetValue(code, out var color)) continue;
            var paint = color == LightingColor.Black ? off : ToColor(color);
            foreach (var brush in brushes) brush.Color = paint;
        }
    }
    private static Color ToColor(LightingColor c) => Color.FromArgb(0xFF, c.R, c.G, c.B);
    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private void BrightnessChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (updating) return;
        int level = (int)Math.Round(e.NewValue);
        sentBrightness = level;
        BrightnessValue.Text = level.ToString();
        controller?.SetBrightness(level);
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && ProfileBox.SelectedIndex >= 0) controller?.SetProfile(ProfileBox.SelectedIndex + 1);
    }
    private async void ResetClick(object sender, RoutedEventArgs e)
    {
        int profile = snapshot.Profile;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            // 프로필 1·3·6을, 2·4·5를 (the digit's final sound).
            Title = $"프로필 {profile}{(profile is 2 or 4 or 5 ? "를" : "을")} 공장 설정으로 되돌릴까요?",
            Content = "키보드, 앞쪽 라이트바, 덮개 로고의 효과가 Lenovo 기본값으로 바뀝니다. 다른 프로필과 밝기는 그대로입니다.",
            PrimaryButtonText = "되돌리기",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) controller?.ResetProfile(profile);
    }
    private void WindowsOffClick(object sender, RoutedEventArgs e) => controller?.TurnOffWindowsLighting();
    private void PowerToggled(object sender, RoutedEventArgs e)
    {
        if (updating) return;
        // One request at a time: the switch stays off until the helper has answered.
        PowerSwitch.IsEnabled = false;
        controller?.SetPowerLed(PowerSwitch.IsOn);
    }
    private async void PowerSetupClick(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        settingUp = true;
        RenderPower(snapshot.PowerLed);
        try
        {
            await controller.SetupPowerLedAsync();
            PowerError.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            PowerError.Text = error.Message;
            PowerError.Visibility = Visibility.Visible;
        }
        finally
        {
            settingUp = false;
            PowerSetup.IsEnabled = true;
            PowerBusy.IsActive = snapshot.PowerLed.Pending;
            PowerBusy.Visibility = snapshot.PowerLed.Pending ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
