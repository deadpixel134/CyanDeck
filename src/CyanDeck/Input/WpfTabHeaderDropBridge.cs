using System;
using System.Windows;

namespace CyanDeck.Input;

/// <summary>
/// Adds drag/drop targeting to one rendered tab header while delegating all payload
/// interpretation and mutation to the host's existing drop commands.
/// </summary>
public sealed class WpfTabHeaderDropBridge : IDisposable
{
    private readonly UIElement _header;
    private readonly TabHeaderDropTarget _target;
    private readonly Func<bool> _isEnabled;
    private readonly Func<IDataObject, TabHeaderDropTarget, TabHeaderDropContext, TabHeaderDropEvaluation> _evaluate;
    private readonly Func<IDataObject, TabHeaderDropTarget, TabHeaderDropEvaluation, bool> _commit;
    private readonly Action<TabHeaderDropTarget, bool> _preview;
    private readonly Action<TabHeaderDropTarget> _activateTab;

    private readonly DragEventHandler _previewDragEnterHandler;
    private readonly DragEventHandler _previewDragOverHandler;
    private readonly DragEventHandler _previewDragLeaveHandler;
    private readonly DragEventHandler _previewDropHandler;
    private readonly RoutedEventHandler _unloadedHandler;
    private readonly object _originalAllowDropLocalValue;

    private bool _previewing;
    private bool _disposed;

    public WpfTabHeaderDropBridge(
        UIElement header,
        TabHeaderDropTarget target,
        Func<bool> isEnabled,
        Func<IDataObject, TabHeaderDropTarget, TabHeaderDropContext, TabHeaderDropEvaluation> evaluate,
        Func<IDataObject, TabHeaderDropTarget, TabHeaderDropEvaluation, bool> commit,
        Action<TabHeaderDropTarget, bool> preview,
        Action<TabHeaderDropTarget> activateTab)
    {
        _header = header ?? throw new ArgumentNullException(nameof(header));
        _target = target.IsValid ? target : throw new ArgumentException("Target must have stable group/tab IDs.", nameof(target));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _activateTab = activateTab ?? throw new ArgumentNullException(nameof(activateTab));

        _previewDragEnterHandler = OnPreviewDragEnter;
        _previewDragOverHandler = OnPreviewDragOver;
        _previewDragLeaveHandler = OnPreviewDragLeave;
        _previewDropHandler = OnPreviewDrop;
        _unloadedHandler = OnUnloaded;

        _originalAllowDropLocalValue = _header.ReadLocalValue(UIElement.AllowDropProperty);
        _header.AllowDrop = true;

        _header.AddHandler(DragDrop.PreviewDragEnterEvent, _previewDragEnterHandler, true);
        _header.AddHandler(DragDrop.PreviewDragOverEvent, _previewDragOverHandler, true);
        _header.AddHandler(DragDrop.PreviewDragLeaveEvent, _previewDragLeaveHandler, true);
        _header.AddHandler(DragDrop.PreviewDropEvent, _previewDropHandler, true);
        _header.AddHandler(FrameworkElement.UnloadedEvent, _unloadedHandler);
    }

    private void OnPreviewDragEnter(object sender, DragEventArgs e) => UpdatePreview(e);

    private void OnPreviewDragOver(object sender, DragEventArgs e) => UpdatePreview(e);

    private void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        ClearPreview();
        if (_isEnabled())
        {
            e.Handled = true;
        }
    }

    private void UpdatePreview(DragEventArgs e)
    {
        if (_disposed || !_isEnabled())
        {
            ClearPreview();
            return;
        }

        var context = CreateContext(e);
        var evaluation = SafeEvaluate(e.Data, context);
        var normalized = TabHeaderDropPolicy.NormalizeEvaluation(_target, context, evaluation);
        var effect = ToWpfEffect(normalized.PreferredOperation);

        e.Effects = effect;
        e.Handled = true;

        if (normalized.Accepted)
        {
            SetPreview(true);
        }
        else
        {
            ClearPreview();
        }
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (_disposed || !_isEnabled())
        {
            ClearPreview();
            return;
        }

        var context = CreateContext(e);
        var evaluation = TabHeaderDropPolicy.NormalizeEvaluation(
            _target,
            context,
            SafeEvaluate(e.Data, context));

        if (!evaluation.Accepted)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            ClearPreview();
            return;
        }

        var succeeded = false;
        try
        {
            succeeded = _commit(e.Data, _target, evaluation);
        }
        catch
        {
            // A drop target must fail closed. Host callbacks may log their own details.
            succeeded = false;
        }

        e.Effects = succeeded ? ToWpfEffect(evaluation.PreferredOperation) : DragDropEffects.None;
        e.Handled = true;
        ClearPreview();

        if (TabHeaderDropPolicy.ShouldActivateAfterCommit(evaluation, succeeded))
        {
            try
            {
                _activateTab(_target);
            }
            catch
            {
                // The data operation already succeeded; tab activation is best-effort UI only.
            }
        }
    }

    private TabHeaderDropEvaluation SafeEvaluate(IDataObject data, TabHeaderDropContext context)
    {
        try
        {
            return _evaluate(data, _target, context);
        }
        catch
        {
            return TabHeaderDropEvaluation.Reject;
        }
    }

    private static TabHeaderDropContext CreateContext(DragEventArgs e)
    {
        var allowed = TabHeaderDropOperation.None;
        if ((e.AllowedEffects & DragDropEffects.Copy) != 0)
        {
            allowed |= TabHeaderDropOperation.Copy;
        }
        if ((e.AllowedEffects & DragDropEffects.Move) != 0)
        {
            allowed |= TabHeaderDropOperation.Move;
        }
        if ((e.AllowedEffects & DragDropEffects.Link) != 0)
        {
            allowed |= TabHeaderDropOperation.Link;
        }

        var modifiers = TabHeaderDropModifiers.None;
        if ((e.KeyStates & DragDropKeyStates.LeftMouseButton) != 0)
        {
            modifiers |= TabHeaderDropModifiers.LeftMouseButton;
        }
        if ((e.KeyStates & DragDropKeyStates.RightMouseButton) != 0)
        {
            modifiers |= TabHeaderDropModifiers.RightMouseButton;
        }
        if ((e.KeyStates & DragDropKeyStates.MiddleMouseButton) != 0)
        {
            modifiers |= TabHeaderDropModifiers.MiddleMouseButton;
        }
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
        {
            modifiers |= TabHeaderDropModifiers.Shift;
        }
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
        {
            modifiers |= TabHeaderDropModifiers.Control;
        }
        if ((e.KeyStates & DragDropKeyStates.AltKey) != 0)
        {
            modifiers |= TabHeaderDropModifiers.Alt;
        }

        return new TabHeaderDropContext(allowed, modifiers);
    }

    private static DragDropEffects ToWpfEffect(TabHeaderDropOperation operation) => operation switch
    {
        TabHeaderDropOperation.Copy => DragDropEffects.Copy,
        TabHeaderDropOperation.Move => DragDropEffects.Move,
        TabHeaderDropOperation.Link => DragDropEffects.Link,
        _ => DragDropEffects.None,
    };

    private void SetPreview(bool enabled)
    {
        if (_previewing == enabled)
        {
            return;
        }

        _previewing = enabled;
        try
        {
            _preview(_target, enabled);
        }
        catch
        {
            // Preview is non-authoritative and must never make the drag operation fail.
        }
    }

    private void ClearPreview() => SetPreview(false);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ClearPreview();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearPreview();

        _header.RemoveHandler(DragDrop.PreviewDragEnterEvent, _previewDragEnterHandler);
        _header.RemoveHandler(DragDrop.PreviewDragOverEvent, _previewDragOverHandler);
        _header.RemoveHandler(DragDrop.PreviewDragLeaveEvent, _previewDragLeaveHandler);
        _header.RemoveHandler(DragDrop.PreviewDropEvent, _previewDropHandler);
        _header.RemoveHandler(FrameworkElement.UnloadedEvent, _unloadedHandler);

        if (ReferenceEquals(_originalAllowDropLocalValue, DependencyProperty.UnsetValue))
        {
            _header.ClearValue(UIElement.AllowDropProperty);
        }
        else
        {
            _header.SetValue(UIElement.AllowDropProperty, _originalAllowDropLocalValue);
        }
    }
}
