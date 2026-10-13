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
recovery candidates against the current legacy profile. Any difference, unsafe
link, malformed journal, running application, or conflicting switch aborts the
operation without replacing or deleting either profile. It restores only the
non-activating `prepared` journal phase; the separate `-VerifyPrepared` step
is still required. If verification fails, preserve both folders for review
rather than removing the candidate. `-AbortPreparation` only changes metadata;
it **does not** clean up directories.

An existing `CyanDeck` folder by itself is *not proof* of an interrupted
preparation. Use recovery only if you intentionally prepared that exact copy;
the tool never adopts a different folder or activates it automatically.

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
