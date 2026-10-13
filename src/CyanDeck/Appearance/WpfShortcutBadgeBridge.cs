using System;
using System.Windows;
using System.Windows.Documents;

namespace CyanDeck.Appearance;

public sealed class WpfShortcutBadgeBridge : IDisposable
{
    private readonly FrameworkElement _iconElement;
    private readonly Func<ShortcutBadgeContext> _getContext;
    private readonly Func<ShortcutBadgeOptions> _getOptions;
    private readonly Action<bool> _setHostShortcutOverlaySuppressed;
    private WpfShortcutBadgeAdorner? _adorner;
    private AdornerLayer? _layer;
    private bool _disposed;

    public WpfShortcutBadgeBridge(
        FrameworkElement iconElement,
        Func<ShortcutBadgeContext> getContext,
        Func<ShortcutBadgeOptions> getOptions,
        Action<bool> setHostShortcutOverlaySuppressed)
    {
        ArgumentNullException.ThrowIfNull(iconElement);
        ArgumentNullException.ThrowIfNull(getContext);
        ArgumentNullException.ThrowIfNull(getOptions);
        ArgumentNullException.ThrowIfNull(setHostShortcutOverlaySuppressed);

        _iconElement = iconElement;
        _getContext = getContext;
        _getOptions = getOptions;
        _setHostShortcutOverlaySuppressed = setHostShortcutOverlaySuppressed;

        _iconElement.Loaded += OnLoaded;
        _iconElement.Unloaded += OnUnloaded;
        _iconElement.SizeChanged += OnSizeChanged;

        Refresh();
    }

    public ShortcutBadgeDecision Refresh()
    {
        ThrowIfDisposed();
        var decision = ShortcutBadgePolicy.Evaluate(_getContext(), _getOptions());
        _setHostShortcutOverlaySuppressed(decision.SuppressHostShortcutOverlay);

        if (!_iconElement.IsLoaded || !decision.ShowBadge)
        {
            RemoveAdorner();
            return decision;
        }

        EnsureAdorner();
        _adorner?.InvalidateVisual();
        return decision;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _iconElement.Loaded -= OnLoaded;
        _iconElement.Unloaded -= OnUnloaded;
        _iconElement.SizeChanged -= OnSizeChanged;
        RemoveAdorner();
        _setHostShortcutOverlaySuppressed(false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        RemoveAdorner();
        _setHostShortcutOverlaySuppressed(false);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => _adorner?.InvalidateVisual();

    private void EnsureAdorner()
    {
        var layer = AdornerLayer.GetAdornerLayer(_iconElement);
        if (layer is null)
        {
            RemoveAdorner();
            return;
        }

        if (_adorner is not null && ReferenceEquals(_layer, layer))
        {
            return;
        }

        RemoveAdorner();
        _layer = layer;
        _adorner = new WpfShortcutBadgeAdorner(_iconElement, _getOptions);
        _layer.Add(_adorner);
    }

    private void RemoveAdorner()
    {
        if (_adorner is not null && _layer is not null)
        {
            _layer.Remove(_adorner);
        }

        _adorner = null;
        _layer = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
