using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CyanDeck.Models;

namespace CyanDeck.Services;

public sealed record SelfTestEntry(string Name, bool Passed, string Detail, bool Warning = false);

public sealed record SelfTestReport(IReadOnlyList<SelfTestEntry> Entries)
{
    public int Failed => Entries.Count(e => !e.Passed && !e.Warning);
    public int Warnings => Entries.Count(e => e.Warning);
    public bool Passed => Failed == 0;
    public string Summary => $"{(Passed ? "통과" : "실패")} · 통과 {Entries.Count - Failed - Warnings} · 경고 {Warnings} · 실패 {Failed}";

    public string ToDisplayString() => Summary + "\n\n" + string.Join("\n", Entries.Select(e => $"{(e.Passed ? "✓" : e.Warning ? "!" : "✗")} {e.Name}: {e.Detail}"));
}

public static class SelfTestService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SelfTestReport Run(DeckState state, bool safeMode)
    {
        var entries = new List<SelfTestEntry>();
        void Test(string name, Action action, string success)
        {
            try { action(); entries.Add(new SelfTestEntry(name, true, success)); }
            catch (Exception ex) { entries.Add(new SelfTestEntry(name, false, ex.Message)); }
        }

        Test("설정 직렬화 왕복", () =>
        {
            var payload = JsonSerializer.Serialize(state, Json);
            var clone = JsonSerializer.Deserialize<DeckState>(payload, Json) ?? throw new InvalidDataException("직렬화 왕복 결과가 비어 있습니다.");
            if (clone.Version > StateSchema.Current) throw new InvalidDataException($"설정 스키마 v{clone.Version}은(는) 지원 버전 v{StateSchema.Current}보다 새롭습니다.");
            var repair = StateIntegrityService.Repair(clone);
            if (repair.Repairs > 0) throw new InvalidDataException($"현재 설정에는 {repair.Repairs}개의 복구가 필요합니다.");
        }, "JSON 직렬화와 무결성 검사를 통과했습니다.");

        Test("설정 폴더 쓰기", () => ProbeWrite(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck")), "쓰기 가능.");
        Test("로컬 업데이트 폴더 쓰기", () => ProbeWrite(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyanDeck", "updates")), "쓰기 가능.");

        var windowsRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var powershell = Path.Combine(windowsRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        entries.Add(new SelfTestEntry("PowerShell 업데이트 도구", File.Exists(powershell), File.Exists(powershell) ? powershell : "표준 Windows 위치에서 powershell.exe를 찾지 못했습니다.", Warning: !File.Exists(powershell)));
        entries.Add(new SelfTestEntry("모니터 검색", MonitorConfigurationService.CaptureMonitors().Count > 0, $"모니터 {MonitorConfigurationService.CaptureMonitors().Count}개를 찾았습니다."));
        entries.Add(new SelfTestEntry("바탕화면 Known Folder", DesktopDirectoryService.GetRoots().Count > 0, $"바탕화면 루트 {DesktopDirectoryService.GetRoots().Count}개를 확인했습니다."));

        if (!safeMode)
        {
            var shellOkay = DesktopHost.CanLocateDesktopShell();
            entries.Add(new SelfTestEntry("Explorer 바탕화면 셸", shellOkay, shellOkay ? "바탕화면 셸 부모와 아이콘 보기를 찾을 수 있습니다." : "Explorer 바탕화면 셸을 찾지 못했습니다.", Warning: !shellOkay));
        }
        else
        {
            entries.Add(new SelfTestEntry("Explorer 바탕화면 셸", false, "안전 모드에서는 건너뜁니다.", Warning: true));
        }

        // RSA-PSS signing is mandatory for actual updates. A local-only build
        // with no update channel needs no key and must not fail the whole self-test.
        if (string.IsNullOrWhiteSpace(UpdateService.ResolveManifestSource(state.Settings.UpdateManifestSource)))
        {
            entries.Add(new SelfTestEntry("업데이트 서명 키", true, "업데이트 채널 미설정 — 업데이트 설치 기능은 사용되지 않습니다."));
        }
        else
        {
            try
            {
                var keyPath = UpdateService.ResolvePublicKeyPath(state.Settings.UpdatePublicKeyPath);
                if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath)) throw new FileNotFoundException("신뢰할 수 있는 업데이트 공개 키가 필요하지만 찾지 못했습니다.");
                using var rsa = RSA.Create();
                rsa.ImportFromPem(File.ReadAllText(keyPath));
                var fingerprint = UpdateService.GetPublicKeyFingerprint(rsa);
                entries.Add(new SelfTestEntry("업데이트 서명 키", true, $"신뢰 키 {fingerprint[..Math.Min(16, fingerprint.Length)]}… 을(를) 불러왔습니다."));
            }
            catch (Exception ex)
            {
                entries.Add(new SelfTestEntry("업데이트 서명 키", false, ex.Message));
            }
        }

        foreach (var helper in new[] { "apply-update.ps1" })
        {
            var exists = File.Exists(Path.Combine(AppContext.BaseDirectory, helper));
            entries.Add(new SelfTestEntry($"배포 도우미 {helper}", exists, exists ? "있음." : "실행 파일 옆에 없습니다.", Warning: !exists));
        }

        DiagnosticsLog.Info("Self-test: " + new SelfTestReport(entries).Summary);
        return new SelfTestReport(entries);
    }

    private static void ProbeWrite(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $".selftest-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(path, "CyanDeck self-test"); File.Delete(path); }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
