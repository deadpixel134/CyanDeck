using System.Windows;

namespace CyanDeck.Appearance;

/// <summary>
/// Per-group bridge for the global header-control master gate.
/// The host supplies apply/clear callbacks so the overlay does not overwrite existing WPF bindings.
/// </summary>
internal sealed class WpfHeaderControlMasterBridge : IDisposable
{
    private readonly FrameworkElement _headerRoot;
    private readonly Func<HeaderControlMasterContext> _readContext;
    private readonly Func<HeaderControlMasterOptions> _readOptions;
    private readonly Action _applySuppression;
    private readonly Action _clearSuppression;
    private readonly RoutedEventHandler _loadedHandler;
    private readonly RoutedEventHandler _unloadedHandler;

    private bool _suppressed;
    private bool _disposed;

    internal WpfHeaderControlMasterBridge(
        FrameworkElement headerRoot,
        Func<HeaderControlMasterContext> readContext,
        Func<HeaderControlMasterOptions> readOptions,
        Action applySuppression,
        Action clearSuppression)
    {
        _headerRoot = headerRoot ?? throw new ArgumentNullException(nameof(headerRoot));
        _readContext = readContext ?? throw new ArgumentNullException(nameof(readContext));
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _applySuppression = applySuppression ?? throw new ArgumentNullException(nameof(applySuppression));
        _clearSuppression = clearSuppression ?? throw new ArgumentNullException(nameof(clearSuppression));

        _loadedHandler = OnLoaded;
        _unloadedHandler = OnUnloaded;
        _headerRoot.Loaded += _loadedHandler;
        _headerRoot.Unloaded += _unloadedHandler;

        if (_headerRoot.IsLoaded)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Call whenever the global setting, managed policy, header presentation, or group
    /// interactivity changes. No timer/polling is required.
    /// </summary>
    internal void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        HeaderControlMasterDecision decision = HeaderControlMasterPolicy.Decide(
            SafeReadContext(),
            SafeReadOptions());

        SetSuppressed(decision == HeaderControlMasterDecision.HideAllControls);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A recycled/unloaded header must not retain a local master override.
        SetSuppressed(false);
    }

    private HeaderControlMasterContext SafeReadContext()
    {
        try
        {
            return _readContext();
        }
        catch
        {
            // Fail open to the established v1.9/v7.8 policies rather than permanently hiding controls.
            return new HeaderControlMasterContext(
                IsHeaderPresented: false,
                IsGroupInteractive: false,
                DeferToHostPresentation: true);
        }
    }

    private HeaderControlMasterOptions SafeReadOptions()
    {
        try
        {
            return _readOptions();
        }
        catch
        {
            return HeaderControlMasterOptions.Default;
        }
    }

    private void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed)
        {
            return;
        }

        _suppressed = suppressed;
        if (suppressed)
        {
            _applySuppression();
        }
        else
        {
            _clearSuppression();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _headerRoot.Loaded -= _loadedHandler;
        _headerRoot.Unloaded -= _unloadedHandler;

        if (_suppressed)
        {
            _suppressed = false;
            _clearSuppression();
        }
    }
}
