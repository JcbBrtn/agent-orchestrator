using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AgentOrchestrator.App.Services;

/// <summary>A colour palette. Every string property (other than <see cref="Name"/>) becomes a SolidColorBrush resource with the same key.</summary>
public sealed record ThemePalette(
    string Name, bool IsDark,
    string WindowBackground, string WindowForeground, string PanelBackground, string ToolbarBackground,
    string ControlBackground, string ControlHoverBackground, string ControlPressedBackground, string ControlBorder,
    string ControlForeground, string DisabledForeground, string Accent, string AccentForeground,
    string SelectionBackground, string SelectionForeground, string HintForeground, string SplitterBackground,
    string ScrollThumb, string ScrollThumbHover,
    string CanvasBackground, string NodeBackground, string NodeHeader, string NodeForeground, string NodeSubtleForeground,
    string Connector, string ArrowStroke, string ArrowBackEdge, string ArrowLabelForeground, string ArrowSelected, string ArrowActive);

public static class ThemeManager
{
    public static IReadOnlyList<ThemePalette> Themes { get; } =
    [
        new("Dark", true,
            "#1F2126", "#E4E6EB", "#25282E", "#2B2E35",
            "#2F333B", "#3A3F49", "#454B57", "#4A4F5A",
            "#E4E6EB", "#7C818C", "#3D8BFD", "#FFFFFF",
            "#264F78", "#FFFFFF", "#9AA0AB", "#33363E",
            "#4A4F5A", "#5E6470",
            "#181A1F", "#2A2D34", "#3C3F48", "#E4E6EB", "#9AA0AB",
            "#3D8BFD", "#7A8090", "#B08A50", "#FFFFFF", "#FFA500", "#00BFFF"),
        new("Light", false,
            "#F5F6F8", "#1F2328", "#FFFFFF", "#EAECEF",
            "#FFFFFF", "#E8EEF7", "#D6E2F3", "#C3C8D0",
            "#1F2328", "#8C959F", "#0969DA", "#FFFFFF",
            "#CFE3FF", "#0B1F3A", "#6E7781", "#D0D4DA",
            "#C3C8D0", "#A6ADB8",
            "#E9ECF1", "#FFFFFF", "#5B6B85", "#1F2328", "#6E7781",
            "#0969DA", "#7D8696", "#B7791F", "#1F2328", "#E07B00", "#0284C7"),
        new("Midnight", true,
            "#0F1629", "#DCE3F2", "#131C33", "#18223D",
            "#1B2643", "#24335A", "#2C3E6C", "#2E3D63",
            "#DCE3F2", "#6B7794", "#5B8CFF", "#FFFFFF",
            "#2A4A8C", "#FFFFFF", "#8C99B8", "#1E2A47",
            "#2E3D63", "#3F5285",
            "#0B1120", "#16203A", "#263A66", "#DCE3F2", "#8C99B8",
            "#5B8CFF", "#6F7FA6", "#C9A227", "#FFFFFF", "#FFB347", "#38BDF8"),
        new("Sand", false,
            "#F4EFE6", "#3B3228", "#FBF8F2", "#EAE2D3",
            "#FFFDF8", "#F0E6D4", "#E6D7BC", "#CDBFA6",
            "#3B3228", "#A0927C", "#B5651D", "#FFFFFF",
            "#EBD3B0", "#3B3228", "#8A7B66", "#DDD2BF",
            "#CDBFA6", "#B5A587",
            "#EDE5D6", "#FFFDF8", "#7A6A55", "#3B3228", "#8A7B66",
            "#B5651D", "#968872", "#A0522D", "#3B3228", "#D35400", "#2E86C1"),
    ];

    public static ThemePalette Current { get; private set; } = Themes[0];

    private static ResourceDictionary? _active;
    private static bool _initialized;

    public static void Apply(string? name)
    {
        var theme = Themes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
        var app = Application.Current;
        if (!_initialized)
        {
            _initialized = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => ApplyTitleBar((Window)s)));
        }

        var dict = Build(theme);
        var nodify = app.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Nodify;component/Themes/") == true);
        var nodifyUri = new Uri($"pack://application:,,,/Nodify;component/Themes/{(theme.IsDark ? "Dark" : "Light")}.xaml");
        if (nodify is not null && nodify.Source != nodifyUri)
            app.Resources.MergedDictionaries[app.Resources.MergedDictionaries.IndexOf(nodify)] = new ResourceDictionary { Source = nodifyUri };
        if (_active is not null) app.Resources.MergedDictionaries.Remove(_active);
        app.Resources.MergedDictionaries.Add(dict);
        _active = dict;
        Current = theme;

        foreach (Window w in app.Windows) ApplyTitleBar(w);
    }

    private static ResourceDictionary Build(ThemePalette theme)
    {
        var dict = new ResourceDictionary();
        var brushes = new Dictionary<string, SolidColorBrush>();
        foreach (var p in typeof(ThemePalette).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.PropertyType != typeof(string) || p.Name == nameof(ThemePalette.Name)) continue;
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)p.GetValue(theme)!));
            brush.Freeze();
            brushes[p.Name] = brush;
            dict[p.Name] = brush;
        }

        // Map system colours too, so stock control parts (scroll corners, DataGrid cells, menus…) follow the theme.
        dict[SystemColors.WindowBrushKey] = brushes["ControlBackground"];
        dict[SystemColors.WindowTextBrushKey] = brushes["ControlForeground"];
        dict[SystemColors.ControlBrushKey] = brushes["PanelBackground"];
        dict[SystemColors.ControlTextBrushKey] = brushes["WindowForeground"];
        dict[SystemColors.ControlLightBrushKey] = brushes["ControlHoverBackground"];
        dict[SystemColors.ControlLightLightBrushKey] = brushes["ControlHoverBackground"];
        dict[SystemColors.ControlDarkBrushKey] = brushes["ControlBorder"];
        dict[SystemColors.ControlDarkDarkBrushKey] = brushes["ControlBorder"];
        dict[SystemColors.HighlightBrushKey] = brushes["SelectionBackground"];
        dict[SystemColors.HighlightTextBrushKey] = brushes["SelectionForeground"];
        dict[SystemColors.InactiveSelectionHighlightBrushKey] = brushes["SelectionBackground"];
        dict[SystemColors.InactiveSelectionHighlightTextBrushKey] = brushes["SelectionForeground"];
        dict[SystemColors.GrayTextBrushKey] = brushes["DisabledForeground"];
        dict[SystemColors.MenuBrushKey] = brushes["ControlBackground"];
        dict[SystemColors.MenuTextBrushKey] = brushes["ControlForeground"];
        dict[SystemColors.InfoBrushKey] = brushes["ControlBackground"];
        dict[SystemColors.InfoTextBrushKey] = brushes["ControlForeground"];
        return dict;
    }

    /// <summary>The stock ToolBar overflow grip ignores our palette; our toolbar never overflows, so hide it.</summary>
    public static void HideToolBarOverflow(System.Windows.Controls.ToolBar bar)
    {
        bar.ApplyTemplate();
        HideOverflow(bar, bar);
    }

    private static void HideOverflow(DependencyObject node, System.Windows.Controls.ToolBar bar)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is System.Windows.Controls.Primitives.ToggleButton toggle && toggle.TemplatedParent == bar)
            {
                ((toggle.Parent as FrameworkElement) ?? toggle).Visibility = Visibility.Collapsed;
                return;
            }
            HideOverflow(child, bar);
        }
    }

    private static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = Current.IsDark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE (20 on Windows 10 20H1+ / 11). Best effort; ignored on older systems.
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
