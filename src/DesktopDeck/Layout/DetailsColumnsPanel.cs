using System.Windows;
using System.Windows.Controls;
using DesktopDeck.Models;

namespace DesktopDeck.Layout;

/// <summary>
/// Lightweight semantic Details-row/header panel. The layout is inherited from the owning
/// ListBox/header shell so changing Explorer column order/width does not rebuild Portal items.
/// </summary>
public sealed class DetailsColumnsPanel : Panel
{
    private const double IconColumnWidth = 34;
    private const double DefaultNameWidth = 240;
    private const double DefaultModifiedWidth = 126;
    private const double DefaultTypeWidth = 82;
    private const double DefaultSizeWidth = 78;

    public static readonly DependencyProperty LayoutProperty = DependencyProperty.RegisterAttached(
        "Layout",
        typeof(IReadOnlyList<DeckDetailsColumnState>),
        typeof(DetailsColumnsPanel),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.Inherits,
            static (d, _) =>
            {
                if (d is DetailsColumnsPanel panel)
                {
                    panel.InvalidateMeasure();
                    panel.InvalidateArrange();
                }
            }));

    public static readonly DependencyProperty ColumnKindProperty = DependencyProperty.RegisterAttached(
        "ColumnKind",
        typeof(DetailsPanelColumnKind),
        typeof(DetailsColumnsPanel),
        new FrameworkPropertyMetadata(DetailsPanelColumnKind.Icon, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static void SetLayout(DependencyObject element, IReadOnlyList<DeckDetailsColumnState>? value) =>
        element.SetValue(LayoutProperty, value);

    public static IReadOnlyList<DeckDetailsColumnState>? GetLayout(DependencyObject element) =>
        (IReadOnlyList<DeckDetailsColumnState>?)element.GetValue(LayoutProperty);

    public static void SetColumnKind(DependencyObject element, DetailsPanelColumnKind value) =>
        element.SetValue(ColumnKindProperty, value);

    public static DetailsPanelColumnKind GetColumnKind(DependencyObject element) =>
        (DetailsPanelColumnKind)element.GetValue(ColumnKindProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = ResolveLayout();
        var widths = ResolveWidths(layout, availableSize.Width);
        double desiredHeight = 0;
        double desiredWidth = 0;

        foreach (UIElement child in InternalChildren)
        {
            var kind = GetColumnKind(child);
            double width = kind == DetailsPanelColumnKind.Icon
                ? IconColumnWidth
                : widths.TryGetValue(ToModelKind(kind), out var resolved) ? resolved : 0;

            bool visible = kind == DetailsPanelColumnKind.Icon || IsColumnVisible(layout, ToModelKind(kind));
            if (!visible) width = 0;
            child.Measure(new Size(Math.Max(0, width), availableSize.Height));
            desiredHeight = Math.Max(desiredHeight, child.DesiredSize.Height);
        }

        desiredWidth = IconColumnWidth + layout.Where(x => x.IsVisible).Sum(x => widths[x.Kind]);
        if (!double.IsInfinity(availableSize.Width) && !double.IsNaN(availableSize.Width))
            desiredWidth = Math.Max(desiredWidth, availableSize.Width);

        return new Size(desiredWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = ResolveLayout();
        var widths = ResolveWidths(layout, finalSize.Width);
        var positions = new Dictionary<DeckDetailsColumnKind, double>();
        double x = IconColumnWidth;
        foreach (DeckDetailsColumnState state in layout.Where(x => x.IsVisible).OrderBy(x => x.DisplayOrder))
        {
            positions[state.Kind] = x;
            x += widths[state.Kind];
        }

        foreach (UIElement child in InternalChildren)
        {
            var kind = GetColumnKind(child);
            if (kind == DetailsPanelColumnKind.Icon)
            {
                child.Arrange(new Rect(0, 0, IconColumnWidth, finalSize.Height));
                continue;
            }

            var modelKind = ToModelKind(kind);
            if (!IsColumnVisible(layout, modelKind) || !positions.TryGetValue(modelKind, out double left))
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            child.Arrange(new Rect(left, 0, widths[modelKind], finalSize.Height));
        }

        return finalSize;
    }

    private IReadOnlyList<DeckDetailsColumnState> ResolveLayout() =>
        DeckDetailsColumnState.Normalize(GetLayout(this));

    private static Dictionary<DeckDetailsColumnKind, double> ResolveWidths(
        IReadOnlyList<DeckDetailsColumnState> layout,
        double availableWidth)
    {
        var result = new Dictionary<DeckDetailsColumnKind, double>();
        var visible = layout.Where(x => x.IsVisible).OrderBy(x => x.DisplayOrder).ToArray();
        double fixedWidth = IconColumnWidth;
        DeckDetailsColumnState? flexibleName = null;

        foreach (DeckDetailsColumnState state in visible)
        {
            if (state.Kind == DeckDetailsColumnKind.Name && !state.WidthDip.HasValue)
            {
                flexibleName = state;
                continue;
            }

            double width = ResolveConcreteWidth(state);
            result[state.Kind] = width;
            fixedWidth += width;
        }

        if (flexibleName is not null)
        {
            double remaining = (!double.IsInfinity(availableWidth) && !double.IsNaN(availableWidth) && availableWidth > 0)
                ? availableWidth - fixedWidth
                : DefaultNameWidth;
            result[DeckDetailsColumnKind.Name] = Math.Max(48, remaining);
        }

        foreach (DeckDetailsColumnState state in visible)
        {
            if (!result.ContainsKey(state.Kind))
                result[state.Kind] = ResolveConcreteWidth(state);
        }

        return result;
    }

    private static double ResolveConcreteWidth(DeckDetailsColumnState state)
    {
        if (state.WidthDip is double width && double.IsFinite(width) && width > 0)
            return Math.Clamp(width, 48, 640);

        return state.Kind switch
        {
            DeckDetailsColumnKind.Modified => DefaultModifiedWidth,
            DeckDetailsColumnKind.Type => DefaultTypeWidth,
            DeckDetailsColumnKind.Size => DefaultSizeWidth,
            _ => DefaultNameWidth,
        };
    }

    private static bool IsColumnVisible(IReadOnlyList<DeckDetailsColumnState> layout, DeckDetailsColumnKind kind) =>
        layout.FirstOrDefault(x => x.Kind == kind)?.IsVisible == true;

    private static DeckDetailsColumnKind ToModelKind(DetailsPanelColumnKind kind) => kind switch
    {
        DetailsPanelColumnKind.Modified => DeckDetailsColumnKind.Modified,
        DetailsPanelColumnKind.Type => DeckDetailsColumnKind.Type,
        DetailsPanelColumnKind.Size => DeckDetailsColumnKind.Size,
        _ => DeckDetailsColumnKind.Name,
    };
}

public enum DetailsPanelColumnKind
{
    Icon,
    Name,
    Modified,
    Type,
    Size,
}
