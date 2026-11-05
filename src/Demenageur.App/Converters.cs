using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Demenageur.Core.Safety;

namespace Demenageur.App;

/// <summary>Octets → « 12,3 Go ».</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            long bytes when bytes >= 0 => MovePlanner.Bytes(bytes),
            null => "…",
            _ => "?",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Booléen → visibilité (paramètre « inverse » pour l'inverse).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (parameter is "inverse")
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Non null / non vide → visible.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string s => s.Length == 0,
            System.Collections.ICollection c => c.Count == 0,
            _ => false,
        };
        if (parameter is "inverse")
        {
            empty = !empty;
        }

        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Gravité d'une règle → couleur.</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    private static readonly Brush Block = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly Brush Confirm = new SolidColorBrush(Color.FromRgb(0xD8, 0x6B, 0x00));
    private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0x9D, 0x7A, 0x00));
    private static readonly Brush Info = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD));
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            RuleLevel.Block => Block,
            RuleLevel.Confirm => Confirm,
            RuleLevel.Warning => Warning,
            RuleLevel.Info => Info,
            _ => Ok,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Gravité d'une règle → symbole.</summary>
public sealed class LevelToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            RuleLevel.Block => Wpf.Ui.Controls.SymbolRegular.DismissCircle24,
            RuleLevel.Confirm => Wpf.Ui.Controls.SymbolRegular.Warning24,
            RuleLevel.Warning => Wpf.Ui.Controls.SymbolRegular.Warning24,
            RuleLevel.Info => Wpf.Ui.Controls.SymbolRegular.Info24,
            _ => Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Taux d'occupation (0–1) → couleur de la barre (rouge au-delà de 90 %).</summary>
public sealed class UsageToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double ratio && ratio >= 0.9
            ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C))
            : new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
