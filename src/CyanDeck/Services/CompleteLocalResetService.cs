using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CyanDeck.Services;

/// <summary>
/// Only an explicitly confirmed, one-time request may purge the local user
/// profile, in a second process after all calendar writers have exited.
/// This class has no Google API or Windows Desktop file operations.
/// </summary>
public static class CompleteLocalResetService
{
    public const string RequestFileName = ".complete-local-reset.pending.json";
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(15);

    private sealed class ResetRequest
    {
        public ResetRequest() { }
        public string Ticket { get; set; } = "";
        public DateTimeOffset IssuedUtc { get; set; }
    }

    public static string CreateRequest(string root)
    {
        CheckRoot(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("CyanDeck 사용자 데이터 폴더가 없습니다.");
        RejectReparsePoints(root);
        var ticket = Guid.NewGuid().ToString("N");
        var path = Path.Combine(root, RequestFileName);
        // Existing pending requests must never be overwritten.
        using var stream = new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.Write(JsonSerializer.Serialize(new ResetRequest
        {
            Ticket = ticket,
            IssuedUtc = DateTimeOffset.UtcNow
        }));
        writer.Flush();
        stream.Flush(flushToDisk: true);
        return ticket;
    }

    public static void CancelRequest(string root, string ticket)
    {
        try
        {
            var file = Path.Combine(root, RequestFileName);
            if (!File.Exists(file)) return;
            var request = JsonSerializer.Deserialize<ResetRequest>(File.ReadAllText(file));
            if (request is not null && string.Equals(request.Ticket, ticket, StringComparison.Ordinal))
                File.Delete(file);
        }
        catch (Exception) { /* Preserve an unexpected state for manual recovery. */ }
    }

    public static void ExecutePending(string root, string ticket)
    {
        if (!IsTicket(ticket))
            throw new InvalidDataException("완전 초기화 요청 토큰이 올바르지 않습니다.");
        CheckRoot(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("초기화할 CyanDeck 사용자 데이터가 없습니다.");
        RejectReparsePoints(root);
        var requestPath = Path.Combine(root, RequestFileName);
        if (!File.Exists(requestPath))
            throw new InvalidDataException("확인된 완전 초기화 요청 파일이 없습니다.");
        var request = JsonSerializer.Deserialize<ResetRequest>(File.ReadAllText(requestPath));
        var now = DateTimeOffset.UtcNow;
        if (request is null ||
            !string.Equals(request.Ticket, ticket, StringComparison.Ordinal) ||
            request.IssuedUtc > now.AddMinutes(1) ||
            now - request.IssuedUtc > TicketLifetime)
            throw new InvalidDataException("완전 초기화 요청이 만료되었거나 일치하지 않습니다. 데이터는 삭제하지 않았습니다.");

        var full = Path.GetFullPath(root);
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe) &&
            Path.GetFullPath(exe).StartsWith(full + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("실행 파일이 사용자 데이터 폴더 안에 있으므로 초기화할 수 없습니다.");

        // Same-volume rename prevents a partially deleted profile from being
        // treated as a valid first run. Purge failure aborts further startup.
        var quarantine = Path.Combine(Path.GetDirectoryName(full)!,
            "CyanDeck-reset-incomplete-" + ticket);
        if (File.Exists(quarantine) || Directory.Exists(quarantine))
            throw new IOException("초기화 임시 폴더가 이미 존재합니다. 다른 데이터를 삭제하지 않습니다.");
        Directory.Move(full, quarantine);
        try
        {
            Directory.Delete(quarantine, recursive: true);
        }
        catch (Exception ex)
        {
            throw new IOException("초기화 후 정리를 완료하지 못했습니다. 남아 있는 자료를 확인하세요: " +
                quarantine, ex);
        }
    }

    private static bool IsTicket(string? ticket)
    {
        if (ticket is null || ticket.Length != 32) return false;
        foreach (var ch in ticket)
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
        return true;
    }

    private static void CheckRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("초기화 경로가 비어 있습니다.", nameof(root));
        var full = Path.GetFullPath(root);
        if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(full)),
            "CyanDeck", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CyanDeck 전용 사용자 데이터 폴더만 초기화할 수 있습니다.");
    }

    private static void RejectReparsePoints(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("연결된 폴더(심볼릭 링크/정션)는 삭제할 수 없습니다.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("연결된 파일/정션이 있어 초기화를 중지했습니다.");
            if ((attributes & FileAttributes.Directory) != 0)
                RejectReparsePoints(entry);
        }
    }
}
