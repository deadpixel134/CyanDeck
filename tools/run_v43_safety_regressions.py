#!/usr/bin/env python3
"""Explicit, non-destructive source regression gate for v43. Never invokes build/update scripts."""
import argparse, json, subprocess, sys
from pathlib import Path
root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--json-report', type=Path, help='Optional report path; do not put inside user data directories')
args = parser.parse_args()
scripts = [
  'test_v43_diagnostic_privacy_and_shortcut_quality.py',
  'test_v42_pc_recovery_durability.py',
  'test_v41_calendar_helper_band_and_standalone.py',
  'test_v40_wallpaper_alignment.py',
  'test_v38_calendar_visibility_recovery.py',
  'test_v37_portal_clipboard_installer_safety.py',
  'test_v36_pc_safety_hardening.py',
  'test_v33_collection_desktop_delete_choice.py',
  'test_v32_desktop_recycle_boundary.py',
  'test_v31_original_source_protection_factory_reset.py',
  'test_v46_reset_safety.py',
  'test_v47_complete_reset.py',
  'test_v48_tray_ux.py',
  'test_v49_surface_recovery.py',
  'test_v50_monitor_explorer_safety.py',
  'test_full_source_structure.py',
]
results = []
for name in scripts:
    path = root / 'tools' / name
    try:
        completed = subprocess.run([sys.executable, str(path)], cwd=root, capture_output=True,
            text=True, timeout=60, check=False)
        output = completed.stdout + '\n' + completed.stderr
        result = {'script':name, 'passed':completed.returncode == 0, 'exitCode':completed.returncode,
                  'lastLines':output.splitlines()[-5:]}
    except subprocess.TimeoutExpired:
        result = {'script':name, 'passed':False, 'exitCode':None, 'lastLines':['Timed out after 60 seconds']}
    results.append(result)
    print(('PASS' if result['passed'] else 'FAIL'), name, '|', ' / '.join(result['lastLines'][-2:]))
    if not result['passed']:
        # Keep actionable assertions in CI logs instead of only the final count.
        failure_lines = [line for line in output.splitlines()
                         if line.startswith(('FAIL:', 'FAILURES:', 'Traceback', 'ValueError:', 'AssertionError:'))] if result['exitCode'] is not None else result['lastLines']
        for line in failure_lines[:40]:
            print('  ', line)
report = {'suite':'v43-safe-source-regression', 'windowsRuntimeVerified':False,
          'passed':sum(x['passed'] for x in results), 'total':len(results), 'results':results}
if args.json_report:
    args.json_report.parent.mkdir(parents=True,exist_ok=True)
    args.json_report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
print('SUITE:',report['passed'],'/',report['total'])
if report['passed'] != report['total']: raise SystemExit(1)
