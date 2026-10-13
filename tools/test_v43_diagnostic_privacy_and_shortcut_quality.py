#!/usr/bin/env python3
"""Non-destructive structural safeguards for v43 diagnostics. Does not run WPF."""
from pathlib import Path
r = Path(__file__).resolve().parents[1]
text = lambda name: (r / name).read_text(encoding='utf-8-sig')
bundle = text('src/CyanDeck/Services/DiagnosticBundleService.cs')
shortcut = text('src/CyanDeck/Services/ShortcutLaunchService.cs')
report = text('src/CyanDeck/Services/ProblemReportService.cs')
log = text('src/CyanDeck/Services/DiagnosticsLog.cs')
checks = {
  'diagnostic ZIP created via bounded temporary file and validated before replacing': 'ZipFile.OpenRead(tempZip)' in bundle and 'File.Replace(tempZip, destinationPath, null)' in bundle and 'File.Delete(destinationPath)' not in bundle,
  'diagnostic exporter reads at most a limited log tail': 'const int maxReadBytes = 3_000_000' in bundle and 'file.Seek(-maxReadBytes, SeekOrigin.End)' in bundle,
  'audit log included only via redaction': 'WriteRedactedLog(SettingsAuditService.AuditPath' in bundle,
  'log exporter redacts resolved user profile': 'Regex.Escape(profile)' in bundle and '%USERPROFILE%' in bundle,
  'log exporter redacts other user profile paths': 'Users\\\\' in bundle and 'body = Regex.Replace(body, @"(?i)\\b[A-Z]:' in bundle,
  'authorization header masked': 'Proxy-Authorization' in bundle and 'Bearer' in bundle and '[REDACTED]' in bundle,
  'OAuth sensitive fields masked': all(x in bundle for x in ['refresh_token','access_token','client_secret','client_assertion','id_token','password']),
  'OAuth query parameters masked': all(x in bundle for x in ['client_secret|code|key', '[?&]', '[^&#\\s]+']),
  'Google API key free text masked': 'AIza[0-9A-Za-z_-]{20,}' in bundle,
  'OAuth token free text masked': 'ya29\\.' in bundle,
  'JWT free text masked': 'eyJ[A-Za-z0-9_-]' in bundle,
  'email addresses masked': '[REDACTED:EMAIL]' in bundle,
  'only sanitized export included; no layout or token cache': 'ZipFile.CreateFromDirectory(tempRoot, tempZip' in bundle and 'layout.json' not in bundle.split('    private static string BuildSummary')[0] and 'token.json' not in bundle,
  'shortcut Explorer launch never treated as proof of target launch': 'target launch unverified.' in shortcut and 'ProblemReportService.Reset("shortcut-open:" + shortcut)' not in shortcut,
  'shortcut failure records HRESULT and exception class': 'errorType={launchFailure.GetType().Name}' in shortcut and 'hresult=0x{launchFailure.HResult:X8}' in shortcut,
  'shortcut reveal failure logged without file path': 'errorType={revealFailure.GetType().Name}' in shortcut and 'hresult=0x{revealFailure.HResult:X8}' in shortcut,
  'shortcut diagnostics stay with user; not uploaded': 'ProblemReportService.ShowOnce' in shortcut and 'Clipboard' not in shortcut and 'nothing is uploaded' in report,
  'no blanket log wipe introduced': 'File.AppendAllText(CurrentPath' in log and 'File.Delete(CurrentPath)' not in log,
  'state schema stays 35': 'Current = 35' in text('src/CyanDeck/Services/StateSchema.cs'),
  'OAuth secrets not part of the source': not (r/'json/client_secret.json').exists(),
}
for label, ok in checks.items(): print(('PASS' if ok else 'FAIL') + ': ' + label)
print(f'v43 diagnostic/privacy static checks: {sum(checks.values())}/{len(checks)}')
if not all(checks.values()): raise SystemExit(1)
