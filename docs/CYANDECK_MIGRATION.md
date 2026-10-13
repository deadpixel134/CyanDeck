# CyanDeck identity and data migration contract

The current release intentionally retains DesktopDeck technical identifiers. The
Windows EXE, mutex, IPC, OAuth resource, signed update protocol and update asset
names **must not be renamed independently**.

## Current and planned locations

| Resource | Current | Planned after explicit cutover |
|---|---|---|
| Executable | DesktopDeck.exe | CyanDeck.exe (requires packaging/updater changes) |
| Per-user installation | %LOCALAPPDATA%\\Programs\\DesktopDeck | %LOCALAPPDATA%\\Programs\\CyanDeck |
| User layout/settings | %APPDATA%\\DesktopDeck | %APPDATA%\\CyanDeck |
| Local update cache | %LOCALAPPDATA%\\DesktopDeck | %LOCALAPPDATA%\\CyanDeck |
| Windows shortcut | DesktopDeck.lnk plus CyanDeck.lnk | CyanDeck.lnk |
| Internal schema | 35 | Preserve 35 unless the data format actually changes |

## Preparation (implemented, opt-in)

Run `pwsh -File tools/prepare-cyandeck-data-migration.ps1` for a dry run.
Use `-Apply` only after exiting the application. The script rejects existing
destinations and reparse points, copies to a temporary sibling, verifies every
file by SHA-256, and publishes the copy only after verification. It does not
delete, rename, merge or mutate the legacy data. It does not change runtime
paths or activate the new copy. Treat the new folder as a **migration staging
copy**, not the live data store. Do not edit both folders independently.

## Recovering an interrupted preparation (implemented, explicit)

If `-Apply` published `%APPDATA%\\CyanDeck` but stopped before writing the
preparation journal, **do not run `-Apply` again or delete either folder**.
Close DesktopDeck/CyanDeck completely, preserve a separate backup and run:

```powershell
pwsh -File tools/prepare-cyandeck-data-migration.ps1 -RecoverPreparation
pwsh -File tools/prepare-cyandeck-data-migration.ps1 -VerifyPrepared
```

Recovery is allowed **only for the canonical CyanDeck destination**, with a
missing or explicitly aborted preparation journal. It validates the existing
copy's full file and directory set, SHA-256 file hashes, primary JSON and
recovery candidates against the current legacy profile. Verification likewise
rejects corrupt JSON even if both profile copies are byte-for-byte identical. Any difference, unsafe
link, malformed journal, running application, or conflicting switch aborts the
operation without replacing or deleting either profile. It restores only the
non-activating `prepared` journal phase; the separate `-VerifyPrepared` step
is still required. If verification fails, preserve both folders for review
rather than removing the candidate. `-AbortPreparation` only changes metadata;
it **does not** clean up directories.

An existing `CyanDeck` folder by itself is *not proof* of an interrupted
preparation. Use recovery only if you intentionally prepared that exact copy;
the tool never adopts a different folder or activates it automatically.

## Legacy-profile writer lease (implemented; cutover still disabled)

Current DesktopDeck builds now hold **two named Windows mutexes** for the
entire active writer lifetime:

- `Local\\DesktopDeck.SingleInstance` retains compatibility with older
  DesktopDeck executables.
- `Local\\CyanDeck.LegacyProfileWriter.v1` is a stable cross-executable
  legacy-profile **writer lease**. A future `CyanDeck.exe` must acquire the
  same lease before reading/writing any shared legacy data.

Normal application exit explicitly releases ownership of the legacy
singleton mutex before closing its handle. Otherwise the next migration
would mistake a clean shutdown for an abandoned writer. The domain harness
checks both exclusivity and reacquisition after normal disposal.

The migration tool holds **both** leases while checking and copying a
profile, including during `-RecoverPreparation`, `-VerifyPrepared`, and
journal updates. The application acquires the new lease before opening the
state/calendar controller and releases it only *after* controller shutdown,
so another cooperative writer cannot start during a pending save.

**Important limitation:** Older published DesktopDeck binaries do not
acquire the *new* writer lease. The migration tool also holds the original
singleton mutex to exclude those older running processes during preparation,
but a stray portable older executable could be launched after a future
profile cutover. A complete safe activation still requires a coordinated
installer/updater handoff, downgrade prevention, old-binary compatibility
strategy, and tested rollback. A preparation journal or a writer lease is
not permission to activate CyanDeck storage.

## Legacy distribution and downgrade protection (implemented, limited)

The current `install.ps1`, `apply-update.ps1`, and `uninstall.ps1`
now acquire BOTH legacy writer mutexes before changing managed installation
files, and release them when the operation ends. The updater releases
them **before** relaunching the app. This excludes cooperating binaries
during install, signed update, application-file rollback, crash recovery,
and uninstall.

All three scripts inspect the legacy cutover fence at
`%LOCALAPPDATA%\\DesktopDeck\\migration\\legacy-writer-fenced.json`.
Once that fence exists, the current **legacy-only** distribution scripts
refuse to reinstall, downgrade, recover or uninstall the obsolete executable.
Malformed or directory-shaped fence paths fail closed. This fence is not
created by the current release and **must not be created manually**; it is
reserved for a future coordinated, signed transition.

A `-PurgeData -ConfirmPurgeData` uninstall is also rejected while an
`%APPDATA%\\CyanDeck` candidate exists, because removing only the
DesktopDeck source would silently orphan an additional copy of settings
and possibly Google credentials. The candidate must never be silently
deleted. A compatible future uninstaller needs its own explicit data
retention/erasure workflow.

Windows CI exercises these guards with disposable user-profile directories
in PowerShell 7 and Windows PowerShell 5.1. **Older already-distributed
scripts and standalone portable EXEs do not understand the new fence or
mutexes.** These safeguards do not yet permit a safe live profile cutover;
the future transition must also handle older executables and downgrades
without trusting that every old copy participates.

## Signed ZIP handoff safety and rollback (implemented; no EXE rename)

The update staging service and PowerShell installer now agree on the signed
legacy ZIP layout: one flat set of allowlisted files, **or** one wrapper
directory containing only those files. Multiple sibling roots, root/nested
mixtures, duplicate case-insensitive paths, links, unsafe paths, and a second
uncoordinated `CyanDeck.exe` are rejected before replacing the installed EXE.
The original `DesktopDeck.exe` remains mandatory during this compatibility
phase. The updater keeps an allowlisted rollback snapshot and a recovery
journal before any replacement; no unrelated installation files are deleted.

PowerShell 5.1 and 7 CI now execute real disposable ZIP installs, adversarial
package rejection, and an actual application-file rollback. These tests do
**not** activate the new profile or constitute a safe cross-EXE cutover.

## CyanDeck.exe compatibility entry point (preview only)

A separate `src/CyanDeck.Launcher` Windows project now builds **`CyanDeck.exe`**
using the original application icon. It launches only the `DesktopDeck.exe`
beside itself, forwards approved startup options without a shell, and exits.
The DesktopDeck process continues to own the two writer mutexes, account
credentials, local calendars, updater, taskbar identity and single-instance IPC.
`--activate-cyandeck` remains explicitly unsupported.

Build a self-contained preview on Windows:

```powershell
pwsh -File tools/build-cyandeck-launcher-preview.ps1 -Runtime win-x64
```

For manual checks, put `CyanDeck.exe` beside a compatible `DesktopDeck.exe`
in a **separate portable test folder**. Do not overwrite the installed EXE.
Windows CI tests the real executable forwarding to a disposable fake
`DesktopDeck.exe`, including Korean/spaced policy paths, precise argument
boundaries, independent working-directory resolution, and a non-launching
read-only diagnostic. It publishes unsigned preview-only CI artifacts for
win-x64 and win-arm64, retained for 7 days. The preview derives its
file version from the DesktopDeck project and includes an explicit
`PREVIEW-README.txt` declaring it unsigned and non-production.

The legacy installer, signed `package.ps1`, `apply-update.ps1` and GitHub
Releases intentionally **do not include the launcher**. Old updaters reject
additional executables; a later signed handoff/rollback protocol must be
implemented and verified before an installed executable cutover is enabled.

## Future cutover (not yet implemented)

1. Freeze writes and stop all DesktopDeck processes.
2. Back up the original user settings, local calendar data, account metadata,
   update cache and installed application separately. Never include private
   OAuth tokens or credentials in diagnostics or a published release.
3. Verify staged data integrity and schema compatibility; retain any newer
   unsupported schema rather than downgrading it.
4. Update StateStore, calendar persistence, token storage, diagnostics and
   updater to resolve one canonical root, with read-only legacy fallback.
5. Migrate installer, uninstaller, shortcuts, startup entry, process name,
   single-instance mutex, activation IPC, and AppUserModelID together.
6. Publish a signed update with an explicit legacy-to-new executable handoff
   and rollback path. The old updater must be able to verify and launch the
   transition package; do not rename signed manifest identity in place.
7. Confirm local-only events are not uploaded to Google, validate OAuth
   sign-in and update recovery, then test clean install, upgrade, rollback
   and uninstall on Windows x64 and ARM64.
8. Delete legacy data **only with separate explicit user consent** after a
   successful cutover and verified backup.

This document is a contract for a future release, not a claim that the
runtime or installed executable has already moved to CyanDeck paths.
