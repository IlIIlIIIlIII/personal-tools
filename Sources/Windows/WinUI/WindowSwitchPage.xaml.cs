using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PersonalTools;

public sealed partial class WindowSwitchPage : UserControl
{
    private WindowSwitchController? controller;
    private bool updating;
    private readonly Grid[] tiles = new Grid[WindowSlotKeys.Keys.Length];
    public WindowSwitchPage()
    {
        InitializeComponent();
        AddKeys(RegisterKeys, "Win", "Shift", "키");
        AddKeys(FocusKeys, "Win", "키");
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new Grid { Height = 120 };
            Grid.SetColumn(tiles[i], i % 4);
            (i < 4 ? NumberRow : LetterRow).Children.Add(tiles[i]);
        }
    }
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];
    private static void AddKeys(Panel panel, params string[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0) panel.Children.Add(new TextBlock { Text = "+", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Resource("TextFillColorTertiaryBrush") });
            panel.Children.Add(Keycap(keys[i], false));
        }
    }
    private static Border Keycap(string text, bool accent) => new()
    {
        Style = StyleOf(accent ? "AccentKeycap" : "Keycap"),
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Resource(accent ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush") }
    };
    internal void Configure(WindowSwitchController value)
    {
        controller = value;
        controller.Changed += Refresh;
        Refresh();
    }
    private void Refresh()
    {
        if (controller is null) return;
        updating = true;
        EnabledSwitch.IsOn = controller.Settings.Enabled;
        updating = false;
        ErrorBar.IsOpen = controller.Error is not null;
        ErrorBar.Message = controller.Error ?? "";
        var slots = controller.Slots.Snapshot();
        CountText.Text = controller.Running ? $"{slots.Count(x => x is not null)} / 8 등록" : "꺼짐";
        CountDot.Fill = Resource(controller.Running ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush");
        for (int i = 0; i < tiles.Length; i++) Fill(tiles[i], i, slots[i]);
    }
    private void Fill(Grid tile, int index, WindowSlot? slot)
    {
        tile.Children.Clear();
        string key = WindowSlotKeys.Keys[index].ToString();
        var content = new Grid { Padding = new Thickness(14, 12, 12, 14) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        var top = new Grid();
        var cap = Keycap(key, slot is not null); cap.MinWidth = 30; cap.Height = 30; cap.CornerRadius = new CornerRadius(6);
        cap.HorizontalAlignment = HorizontalAlignment.Left;
        ((TextBlock)cap.Child).FontSize = 14;
        top.Children.Add(cap);
        content.Children.Add(top);
        if (slot is null)
        {
            tile.Children.Add(new Rectangle { RadiusX = 8, RadiusY = 8, StrokeThickness = 1, StrokeDashArray = [4, 3], Stroke = Resource("ControlStrongStrokeColorDefaultBrush"), Opacity = 0.6 });
            var empty = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Bottom };
            empty.Children.Add(new TextBlock { Text = "비어 있음", FontSize = 13, Foreground = Resource("TextFillColorSecondaryBrush") });
            empty.Children.Add(new TextBlock { Text = $"Win+Shift+{key}로 등록", FontSize = 12, Foreground = Resource("TextFillColorTertiaryBrush") });
            Grid.SetRow(empty, 1); content.Children.Add(empty);
            AutomationPropertiesName(tile, $"Win+{key}, 비어 있음");
        }
        else
        {
            tile.Children.Add(new Border { Style = StyleOf("TileCard") });
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 10 }, Style = StyleOf("SubtleIconButton"), HorizontalAlignment = HorizontalAlignment.Right };
            ToolTipService.SetToolTip(remove, "등록 해제");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Win+{key} 등록 해제");
            int captured = index;
            remove.Click += (_, _) => controller?.Unregister(captured);
            top.Children.Add(remove);
            var app = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Bottom };
            app.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            app.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = new Image { Width = 36, Height = 36 };
            var fallback = new FontIcon { Glyph = "", FontSize = 22, Foreground = Resource("TextFillColorSecondaryBrush") };
            var iconHost = new Grid { Width = 36, Height = 36 };
            iconHost.Children.Add(fallback); iconHost.Children.Add(icon);
            app.Children.Add(iconHost);
            var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = slot.Title.Length > 0 ? slot.Title : slot.AppName, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
            text.Children.Add(new TextBlock { Text = slot.AppName, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Foreground = Resource("TextFillColorSecondaryBrush") });
            Grid.SetColumn(text, 1); app.Children.Add(text);
            Grid.SetRow(app, 1); content.Children.Add(app);
            ToolTipService.SetToolTip(tile, slot.Title);
            AutomationPropertiesName(tile, $"Win+{key}, {slot.Title}, {slot.AppName}");
            _ = LoadIconAsync(icon, fallback, slot);
        }
        tile.Children.Add(content);
    }
    private static void AutomationPropertiesName(DependencyObject target, string name) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(target, name);
    private static async Task LoadIconAsync(Image image, FontIcon fallback, WindowSlot slot)
    {
        var source = await ClipboardAppIcons.GetAsync(slot.AppPath, System.IO.Path.GetFileName(slot.AppPath ?? slot.AppName));
        if (source is null) return;
        image.Source = source; fallback.Visibility = Visibility.Collapsed;
    }
    private void EnabledToggled(object sender, RoutedEventArgs e)
    {
        if (updating || controller is null) return;
        controller.SetEnabled(EnabledSwitch.IsOn);
    }
}
