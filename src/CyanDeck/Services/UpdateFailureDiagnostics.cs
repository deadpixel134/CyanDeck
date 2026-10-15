using System;
using System.IO;
using System.Linq;

namespace CyanDeck.Services;

/// <summary>
/// Read-only, bounded diagnostic summary for Settings > Updates. Never sends
/// logs anywhere or prints filesystem paths/OAuth content to the settings UI.
/// Full logs remain available locally via an explicit user action.
/// </summary>
public sealed record UpdateFailureSummary(string Text, bool HasLog, int RecentFailedPackages);

public static class UpdateFailureDiagnostics
{
    public static string LogPath(string localRoot) =>
        Path.Combine(localRoot, "updates", "last-update-error.log");

    public static UpdateFailureSummary Inspect(string localRoot, DateTime nowUtc)
    {
        try
        {
            var path = LogPath(localRoot);
            var parent = Path.GetDirectoryName(path)!;
            var recentCount = 0;
            if (Directory.Exists(parent) &&
                (new DirectoryInfo(parent).Attributes & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var marker in Directory.EnumerateFiles(parent, "failed-*.txt").Take(101))
                {
                    var filename = Path.GetFileName(marker);
                    if (!filename.StartsWith("failed-", StringComparison.Ordinal) ||
                        !filename.EndsWith(".txt", StringComparison.Ordinal) ||
                        filename.Length != "failed-".Length + 64 + ".txt".Length ||
                        !filename.AsSpan(7, 64).ToString().All(Uri.IsHexDigit))
                        continue;
                    var info = new FileInfo(marker);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var elapsed = nowUtc.ToUniversalTime() - info.LastWriteTimeUtc;
                    if (elapsed >= TimeSpan.Zero && elapsed < FailedUpdateRetryPolicy.RetryDelay)
                        recentCount++;
                }
            }
            if (!File.Exists(path)) return new UpdateFailureSummary(
                recentCount > 0 ? "최근 자동 업데이트 실패 기록이 있습니다. 12시간 자동 재시도 보류 중입니다."
                    : "저장된 업데이트 실패 로그가 없습니다.", false, recentCount);
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                return new UpdateFailureSummary("업데이트 실패 로그가 연결 파일입니다. 안전을 위해 열지 않습니다.", false, recentCount);
            // Read only the bounded tail even if repeated failures made the
            // local log huge. A classification never includes private paths.
            byte[] buffer;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                var length = (int)Math.Min(input.Length, 16 * 1024);
                input.Seek(-length, SeekOrigin.End);
                buffer = new byte[length];
                var done = 0;
                while (done < length)
                {
                    var n = input.Read(buffer, done, length - done);
                    if (n == 0) break;
                    done += n;
                }
                if (done != buffer.Length) Array.Resize(ref buffer, done);
            }
            var tail = System.Text.Encoding.UTF8.GetString(buffer);
            var last = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?.Trim() ?? "";
            var category = Categorize(last);
            var stamp = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            var status = $"마지막 설치 오류: {stamp} · {category}";
            if (recentCount > 0) status += $" · 재시도 보류 패키지 {recentCount}개";
            return new UpdateFailureSummary(status, true, recentCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            return new UpdateFailureSummary("업데이트 진단 정보를 읽지 못했습니다. 로그 폴더 접근 권한을 확인하세요.",
                false, 0);
        }
    }

    private static string Categorize(string lastError)
    {
        var value = lastError.ToLowerInvariant();
        if (value.Contains("sha-256") || value.Contains("sha256") ||
            value.Contains("signature") || value.Contains("digest"))
            return "패키지 무결성 또는 서명 검증 실패";
        if (value.Contains("access denied") || value.Contains("unauthorized") ||
            value.Contains("being used") || value.Contains("sharing violation") ||
            value.Contains("locked"))
            return "파일 사용 중 또는 접근 권한 문제";
        if (value.Contains("journal") || value.Contains("recovery") ||
            value.Contains("snapshot") || value.Contains("rollback"))
            return "백업 또는 이전 업데이트 복구 확인 필요";
        if (value.Contains("package") || value.Contains("archive") ||
            value.Contains("zip") || value.Contains("payload"))
            return "설치 패키지 준비·검사 실패";
        if (value.Contains("process") || value.Contains("closing") ||
            value.Contains("wait"))
            return "실행 중인 프로그램 종료 대기 실패";
        return "설치 오류 발생 (세부 원인은 로그 파일에서 확인)";
    }
}
