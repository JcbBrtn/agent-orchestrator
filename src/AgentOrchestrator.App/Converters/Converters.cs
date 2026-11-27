using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AgentOrchestrator.App.ViewModels;

namespace AgentOrchestrator.App.Converters;

public sealed class TranscriptKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        TranscriptKind.Input => Brushes.SlateGray,
        TranscriptKind.Thinking => Brushes.DarkCyan,
        TranscriptKind.Assistant => Brushes.CornflowerBlue,
        TranscriptKind.ToolCall => Brushes.DarkOrange,
        TranscriptKind.ToolResult => Brushes.Peru,
        TranscriptKind.Denied => Brushes.IndianRed,
        TranscriptKind.Routing => Brushes.MediumPurple,
        TranscriptKind.Warning => Brushes.Goldenrod,
        TranscriptKind.Error => Brushes.Crimson,
        TranscriptKind.Final => Brushes.MediumSeaGreen,
        TranscriptKind.Question => Brushes.Orchid,
        TranscriptKind.Answer => Brushes.Turquoise,
        _ => Brushes.Gray,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullOrEmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PauseLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "▶ Resume" : "⏸ Pause";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
