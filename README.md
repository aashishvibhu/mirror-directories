# DriveSync

DriveSync backs up folders on your Windows PC to an external drive and keeps the drive up to date as you work.
It tells apart the two reasons a file can disappear from your PC:

- **Offloaded**: you removed it to free space. The backup keeps it.
- **Deleted on purpose**: you don't want it anymore. The backup removes it too.

Files that vanish any other way are never removed from the backup without asking you.

**DriveSync never deletes anything.** Whatever it removes, on the PC or on the drive, goes to the Recycle Bin.
Only you erase files for good, by emptying the Recycle Bin.

> Version 2 is a rewrite in C# on .NET 10. Version 1 (Java, "Mirror Directories") is available at the `v1.0` tag.

## Status

v2 is being built in milestones on the `vbu/v2` branch.

| Milestone | Contents | Status |
|---|---|---|
| 1 | Backup engine and tests | Done |
| 2 | App basics: setup wizard, dashboard, sync with preview, missing-files decisions | Done |
| 3 | Backup browser, restore, version history, free up space, trash | Next |
| 4 | Tray agent, notifications, schedule, sync on plug-in, Explorer menu | |
| 5 | Health check screen, settings, installer | |

## How the engine works

Every file DriveSync backs up gets a record in a catalog on the drive (`.drivesync\catalog.db`):
its size, modified time, SHA-256 checksum and one of four states.

| State | Meaning |
|---|---|
| Active | On the PC and backed up |
| Archived | Only on the drive, kept there on purpose |
| Pending | Missing from the PC, waiting for your decision; still on the drive |
| Deleted | Removed from both, into a Recycle Bin |

- **Sync** compares the PC with the catalog using only names, sizes and dates, so unchanged files are never re-read.
  New and changed files are copied through a temporary file, with the checksum computed in the same pass.
  The older version of a changed file moves to `.drivesync\versions` and is kept forever.
  Renamed or moved files are recognised by their checksum and moved on the drive instead of being copied again.
- **Offload** backs a file up if needed, checks that the PC copy and the drive copy have the same checksum,
  then sends the PC copy to the Recycle Bin and marks the file Archived.
- **Delete everywhere** sends the PC copy to the PC's Recycle Bin and the drive copy to the drive's Recycle Bin.
- **Missing files** (deleted by hand) are handled by your chosen policy. The default is to ask.
  If many files vanish at once (30% and at least 50), nothing is removed from the backup, because that usually means
  a disconnected folder, a cloud-sync problem or ransomware.
- **Restore** copies files back from the current backup, an old version, or the Recycle Bin.
  A different file in the way is renamed, never replaced.
- **Health check** confirms every backup copy exists with the right size, or, in a full check, the right checksum.
  It can recopy damaged files that the PC still has.

Before sending anything to a Recycle Bin, DriveSync checks that the bin has room. When a bin is full, Windows erases
its oldest items to make space, so DriveSync stops and tells you instead. Drives without a Recycle Bin
(USB sticks, network folders) get a `.drivesync\trash` folder that DriveSync never empties.

## The app

A Windows 11-style desktop app (WPF with [WPF-UI](https://github.com/lepoco/wpfui)), in light or dark to match Windows.

- **Setup wizard**: pick the folders (nothing is pre-selected), the backup drive, and what to do with files
  that disappear from the PC, then start the first backup.
- **Home**: backup status, **Sync now**, live progress with pause and cancel, totals, and recent activity.
- **Review changes**: every sync shows what's new, changed, moved, back on the PC and missing before anything is copied.
- **Missing files**: files that left the PC without going through DriveSync, grouped by folder, with a
  Recycle Bin hint. For each group or file: **Deleted on purpose**, **Keep on backup**, or **Decide later**.
  Removing anything from the backup asks for confirmation first.

Settings live in `%APPDATA%\DriveSync`, along with a copy of the catalog so totals show while the drive is unplugged.

### Trying it safely

Two environment variables move everything the app writes into folders you choose, and swap the real
Recycle Bin for an ordinary folder:

| Variable | Effect |
|---|---|
| `DRIVESYNC_DATA_DIR` | Where settings and the catalog copy are kept, instead of `%APPDATA%\DriveSync` |
| `DRIVESYNC_RECYCLE_BIN_DIR` | A folder that stands in for the Recycle Bin |

With both set, two developer commands are available (they refuse to run otherwise):

```
DriveSync.exe --dev-demo <folder>         builds sample PC folders, a backup and some history in <folder>
DriveSync.exe --dev-screenshots <folder>  renders every screen, light and dark, to PNG files in <folder>
```

## Layout

```
DriveSync.slnx
src/DriveSync.App/           the desktop app
  Views/                     windows and screens (XAML)
  ViewModels/                screen logic
  Services/                  shared app services and formatting
src/DriveSync.Core/          backup engine (no UI)
  Engine/                    sync, offload, delete, restore, health check
  Scanning/                  reading the PC's folders, exclusions
  Storage/                   catalog, backup location, lock
  RecycleBin/                Windows Recycle Bin access
  Setup/                     creating a backup profile
tests/DriveSync.Core.Tests/  engine tests
```

## Building and testing

Requires Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build
dotnet test
```

The tests write only to a `testdata` folder next to the test binaries, and use a folder that stands in for
the Recycle Bin, so they never touch your files or your real Recycle Bin.
