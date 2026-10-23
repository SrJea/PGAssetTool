using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace PGAssetTool.Gui.Controls;

/// Where a preview's controls sit: beside the model where the pane has width to spare, under it
/// where it has not.
///
/// Under the model they cost it their height — a clip picker, a slider, a texture picker, two rows
/// of named angles and a caption — and in the editor that is most of what a side-by-side half has,
/// in a pane several times wider than the model is drawn. Beside it they cost width, which is what
/// there is too much of there. The browse pane is the other case: a 420 column by default, where a
/// sidebar would leave the model two hundred pixels and nothing to look at.
///
/// Read from the pane's own width rather than from which tab it is, because both are one template
/// and the panes are resizable.
public sealed class Beside(Func<bool, object> answer) : IValueConverter
{
    /// Room for the controls, the gap, and a model still worth looking at.
    private const double Enough = 560;
    private const double Column = 210;

    public static Beside Side { get; } = new(beside => beside ? Dock.Right : Dock.Bottom);
    public static Beside Room { get; } = new(beside => beside ? Column : double.NaN);
    public static Beside Gap { get; } =
        new(beside => beside ? new Thickness(10, 0, 0, 0) : new Thickness(0, 8, 0, 0));

    public object Convert(object? value, Type type, object? parameter, CultureInfo culture)
        => answer(value is double width && width >= Enough);

    public object ConvertBack(object? value, Type type, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
