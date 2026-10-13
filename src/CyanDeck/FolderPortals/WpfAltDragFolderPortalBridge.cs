using System;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace CyanDeck.FolderPortals;

public interface IAltDragFolderPortalHost
{
    bool MasterEnabled { get; }
    bool IsSafeMode { get; }
    bool CanCreateFromDesktopAltDrop { get; }

    bool IsBlankDesktopTarget(DependencyObject? originalSource);
    void SetAltDragFolderPortalPreview(bool active);
    AltDragFolderPortalCreateResult CreateFolderPortal(in AltDragFolderPortalCreateRequest request);
}

public sealed class WpfAltDragFolderPortalBridge : IDisposable
{
    private readonly FrameworkElement _surface;
    private readonly IAltDragFolderPortalHost _host;
    private readonly AltDragFolderPortalOptions _options;
    private readonly DragEventHandler _dragEnterHandler;
    private readonly DragEventHandler _dragOverHandler;
    private readonly DragEventHandler _dragLeaveHandler;
    private readonly DragEventHandler _dropHandler;
    private bool _disposed;

    public WpfAltDragFolderPortalBridge(
        FrameworkElement surface,
        IAltDragFolderPortalHost host,
        AltDragFolderPortalOptions? options = null)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _options = options ?? new AltDragFolderPortalOptions();

        // CyanDeck already has a desktop drop pipeline. v7.15 deliberately does
        // not mutate AllowDrop or replace that pipeline.
        _dragEnterHandler = OnPreviewDragEnter;
        _dragOverHandler = OnPreviewDragOver;
        _dragLeaveHandler = OnPreviewDragLeave;
        _dropHandler = OnPreviewDrop;

        _surface.AddHandler(UIElement.PreviewDragEnterEvent, _dragEnterHandler, true);
        _surface.AddHandler(UIElement.PreviewDragOverEvent, _dragOverHandler, true);
        _surface.AddHandler(UIElement.PreviewDragLeaveEvent, _dragLeaveHandler, true);
        _surface.AddHandler(UIElement.PreviewDropEvent, _dropHandler, true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _host.SetAltDragFolderPortalPreview(false);
        _surface.RemoveHandler(UIElement.PreviewDragEnterEvent, _dragEnterHandler);
        _surface.RemoveHandler(UIElement.PreviewDragOverEvent, _dragOverHandler);
        _surface.RemoveHandler(UIElement.PreviewDragLeaveEvent, _dragLeaveHandler);
        _surface.RemoveHandler(UIElement.PreviewDropEvent, _dropHandler);
    }

    private void OnPreviewDragEnter(object sender, DragEventArgs e) => UpdatePreview(e);

    private void OnPreviewDragOver(object sender, DragEventArgs e) => UpdatePreview(e);

    private void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        _host.SetAltDragFolderPortalPreview(false);
    }

    private void UpdatePreview(DragEventArgs e)
    {
        if (_disposed)
            return;

        var decision = Evaluate(e, out _);
        // Do not set Effects or Handled here. That preserves the host desktop-drop
        // cursor/semantics and avoids filesystem access during pointer movement.
        _host.SetAltDragFolderPortalPreview(decision.Candidate);
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (_disposed)
            return;

        _host.SetAltDragFolderPortalPreview(false);

        var decision = Evaluate(e, out var candidate);
        if (!decision.Candidate || candidate is null)
            return;

        if (!AltDragFolderPortalPolicy.TryNormalizeCandidatePath(candidate, out var normalized))
            return;

        // The only filesystem probe in this overlay. Never do this in DragOver.
        if (!Directory.Exists(normalized))
            return;

        var localPoint = e.GetPosition(_surface);
        var screenPoint = _surface.PointToScreen(localPoint);

        AltDragFolderPortalCreateResult result;
        try
        {
            result = _host.CreateFolderPortal(
                new AltDragFolderPortalCreateRequest(normalized, screenPoint));
        }
        catch
        {
            result = AltDragFolderPortalCreateResult.Failed;
        }

        // Once a real directory has been recognized as an Alt-Portal gesture,
        // this bridge owns the drop. Do not fall through to a file operation.
        e.Handled = true;
        e.Effects = result is AltDragFolderPortalCreateResult.Created or AltDragFolderPortalCreateResult.ReusedExisting
            ? DragDropEffects.Link
            : DragDropEffects.None;
    }

    private AltDragFolderPortalDecision Evaluate(DragEventArgs e, out string? candidate)
    {
        candidate = null;
        var hasFileDrop = false;
        var itemCount = 0;

        try
        {
            hasFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop, false);
            if (hasFileDrop && e.Data.GetData(DataFormats.FileDrop, false) is string[] paths)
            {
                itemCount = paths.Length;
                if (paths.Length == 1)
                    candidate = paths[0];
            }
        }
        catch
        {
            hasFileDrop = false;
            itemCount = 0;
            candidate = null;
        }

        var probe = new AltDragFolderPortalProbe(
            _options.Enabled,
            _host.MasterEnabled,
            _host.IsSafeMode,
            _host.CanCreateFromDesktopAltDrop,
            _host.IsBlankDesktopTarget(e.OriginalSource as DependencyObject),
            Keyboard.Modifiers,
            hasFileDrop,
            itemCount,
            candidate,
            e.AllowedEffects);

        return AltDragFolderPortalPolicy.EvaluatePreview(
            probe,
            _options.RequireExactAltModifier);
    }
}
