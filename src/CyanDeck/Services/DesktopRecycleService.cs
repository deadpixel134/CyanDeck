using Microsoft.VisualBasic.FileIO;

namespace CyanDeck.Services;

/// <summary>
/// Narrow file-system deletion boundary for real Windows Desktop entries.
/// Only immediate children of the current user's or Public Desktop known folder
/// may be moved to the Recycle Bin. External Collection references are never
/// eligible. Never fall back to permanent deletion if recycling fails.
/// </summary>
internal static class DesktopRecycleService
{
    internal static bool IsActualDesktopItem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || DesktopSystemItems.IsShellNamespacePath(path)) return false;
        try
        {
            var normalized = Path.GetFullPath(path);
            if (!DesktopDirectoryService.IsDirectChildOfAnyRoot(normalized, DesktopDirectoryService.GetRoots()))
                return false;
            // Junctions and symbolic links could redirect destructive operations
            // outside the desktop. Exclude them instead of trying to resolve targets.
            if (!File.Exists(normalized) && !Directory.Exists(normalized)) return false;
            return (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }

    /// <summary>Called only after the user explicitly confirms a real deletion.</summary>
    internal static (IReadOnlyList<string> Recycled, IReadOnlyList<string> Errors) RecycleConfirmed(
        IEnumerable<string> paths, Func<string, bool>? confirmPermanentDeletionRisk = null)
    {
        var candidatePaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var recycled = new List<string>();
        var errors = new List<string>();
        // The entire batch is assessed before the first filesystem mutation.
        // If the UI did not supply a callback, a risky/unknown operation fails closed.
        var risk = RecycleBinPreflight.Assess(candidatePaths);
        if (risk.NeedsAdditionalConfirmation && confirmPermanentDeletionRisk?.Invoke(risk.Warning) != true)
        {
            errors.Add("휴지통 용량 초과 또는 미확인 위험으로 인해 파일 삭제를 취소했습니다.");
            return (recycled, errors);
        }
        foreach (var path in candidatePaths)
        {
            if (!IsActualDesktopItem(path))
            {
                errors.Add($"바탕화면의 실제 항목이 아니거나 보호된 경로입니다: {path}");
                continue;
            }
            try
            {
                if (File.Exists(path))
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else if (Directory.Exists(path))
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else
                    throw new FileNotFoundException("삭제 대상이 존재하지 않습니다.", path);
                // Check again: do not mark a target as removed from the layout if
                // the Shell operation returned without moving the original.
                if (File.Exists(path) || Directory.Exists(path))
                    throw new IOException("휴지통 이동을 확인할 수 없습니다. 원본은 유지됩니다.");
                recycled.Add(path);
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }
        return (recycled, errors);
    }
}
