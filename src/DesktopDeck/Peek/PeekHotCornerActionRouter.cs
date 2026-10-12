using System;

namespace DesktopDeck.Peek;

public sealed class PeekHotCornerActionRouter
{
    private readonly Action _togglePeek;
    private readonly Action _toggleDesktopDisplay;
    private readonly Action<Exception>? _reportError;

    public PeekHotCornerActionRouter(
        Action togglePeek,
        Action toggleDesktopDisplay,
        Action<Exception>? reportError = null)
    {
        _togglePeek = togglePeek ?? throw new ArgumentNullException(nameof(togglePeek));
        _toggleDesktopDisplay = toggleDesktopDisplay ?? throw new ArgumentNullException(nameof(toggleDesktopDisplay));
        _reportError = reportError;
    }

    public bool Execute(PeekHotCornerAction action)
    {
        try
        {
            switch (action)
            {
                case PeekHotCornerAction.ShowFences:
                    _togglePeek();
                    return true;

                case PeekHotCornerAction.ShowFullDesktop:
                    _toggleDesktopDisplay();
                    return true;

                case PeekHotCornerAction.DoNothing:
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _reportError?.Invoke(ex);
            return false;
        }
    }
}
