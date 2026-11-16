# DriveSync

DriveSync backs up folders on your Windows PC to an external drive and keeps the drive up to date as you work.
It tells apart the two reasons a file can disappear from your PC:

- **Offloaded**: you removed it to free space. The backup keeps it.
- **Deleted on purpose**: you don't want it anymore. The backup removes it too.

Files that vanish any other way are never removed from the backup without asking you.

**DriveSync never erases anything.** Whatever it removes from the PC goes to the PC's Recycle Bin. Whatever it removes
from the backup goes to the backup's own trash folder (`.drivesync\trash`) or the drive's Recycle Bin, your choice per
backup. Only you erase files for good, in File Explorer.

> Version 2 is a rewrite in C# on .NET 10. Version 1 (Java, "Mirror Directories") is available at the `v1.0` tag.

## Status

v2 is being built in milestones on the `vbu/v2` branch.

| Milestone | Contents | Status |
|---|---|---|
| 1 | Backup engine and tests | Done |
| 2 | App basics: setup wizard, dashboard, sync with preview, missing-files decisions | Done |
| 2.5 | Several backups in one app: overview, add, edit, move to another drive, remove | Done |
| 3 | Backup browser, restore, version history, free up space, storage and trash | Done (`vbu/v3`) |
| 3.5 | Use a backup on another PC, health checks after every sync, Health screen, BitLocker hint | Done (`vbu/v3`) |
| 4 | Tray agent, notifications, schedule, sync on plug-in, Explorer menu | Next |
| 5 | Settings, installer | |

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
- **Restore** copies files back from the current backup, an old version, or the trash. It first works out what it
  would do (to restore, already there, in the way), checks the PC has room, and reports progress.
  A different file in the way is renamed, never replaced. A backup copy that fails its checksum is reported.
- **Removed copies** go to the drive's Recycle Bin or the backup's trash folder. When the bin is full, too small
  for the item or missing, the trash folder takes it, so a removal never gets stuck. New backups use the trash
  folder from the start: it has no size limit and is the same on every PC.
- **Old versions** are listed in the catalog. The app shows what they take and suggests clean-ups (older than a
  year, keep the newest 5, thin out). Nothing is removed until you confirm, and then it goes to the trash.
- **Health**: every sync ends with a quick check that each backup copy exists with the right size. A rolling check
  then checksums the copies that were checked longest ago, for a few minutes, so the whole drive is covered over a
  few weeks. A full check can be run at any time, and repairs damaged copies that the PC still has.
- **The drive carries its settings** (`.drivesync\backup.json`), so a backup can be picked up on another PC with the
  same folders. Files not on that PC can be restored in one go, or left on the drive marked as offloaded.

### Several backups

The app can hold any number of backups. Each is a set of folders going to one destination, with its own name,
records, missing files and rules. Several backups can share a drive (each gets its own folder, such as
`DriveSyncBackup-photos-archive`), and the same folder can be in two backups to keep two copies on two drives.

- **Removing a folder from a backup** keeps its files on the backup under "Removed folders"; they're never treated
  as missing. They can be added back, or sent to the backup drive's Recycle Bin.
- **Moving a backup to another drive** copies everything (files, old versions, offloaded files, records), checks
  every file against its checksum, and only then switches. The old drive is left exactly as it was.
- **Removing a backup** only makes the app forget it; nothing on the drive changes. Add backup reconnects it later.
- A backup can never be placed inside a folder that any backup copies, and no backed-up folder may contain a backup.
- The PC keeps a list of files offloaded through DriveSync (`offloaded.db`). When a file is in two backups and is
  offloaded through one, the other keeps its copy instead of asking whether it was deleted.

Before sending anything to a Recycle Bin, DriveSync checks that the bin has room. When a bin is full, Windows erases
its oldest items to make space, so DriveSync stops and tells you instead. Drives without a Recycle Bin
(USB sticks, network folders) get a `.drivesync\trash` folder that DriveSync never empties.

## The app

A Windows 11-style desktop app (WPF with [WPF-UI](https://github.com/lepoco/wpfui)), in light or dark to match Windows.

- **Setup wizard**: name the backup, pick the folders (nothing is pre-selected), the backup drive, and what to do
  with files that disappear from the PC, then start the first backup. **Add backup** opens the same wizard later.
- **Overview**: a card per backup with its status and **Sync now**, plus **Sync all connected**, which runs every
  backup whose drive is plugged in, one after another.
- **A page per backup**: status, **Sync now**, live progress with pause and cancel, totals, recent activity, its
  missing files, and **Edit backup** (name, folders, removed folders, move to another drive, missing-files rule,
  remove the backup).
- **Review changes**: every sync shows what's new, changed, moved, back on the PC and missing before anything is copied.
- **Files**: browse everything on the backup, search it, and filter to offloaded or missing files, also while the
  drive is unplugged. **Restore** (back where it was, or into a folder you pick), **Free up space**,
  **Delete everywhere**, and each file's **older versions**.
- **Storage**: what takes the space on the drive, old versions with suggested clean-ups, and files removed from
  the backup, which can be restored until you erase them yourself.
- **Health**: what the checks have found, **Check everything now**, and **Repair**.
- **Add backup** recognises a drive that already holds a backup and offers to use it on this PC.
- A drive that isn't protected by BitLocker gets one note in the wizard, with a link to turn it on.
- **Missing files**: files that left the PC without going through DriveSync, grouped by folder, with a
  Recycle Bin hint. For each group or file: **Deleted on purpose**, **Keep on backup**, or **Decide later**.
  Removing anything from the backup asks for confirmation first.

The list of backups lives in `%APPDATA%\DriveSync\backups.json` (an older single-backup `profile.json` becomes the
first backup, named "My backup", and is left in place), along with a copy of each backup's catalog so totals show
while a drive is unplugged.

### Trying it safely

Two environment variables move everything the app writes into folders you choose, and swap the real
Recycle Bin for an ordinary folder:

| Variable | Effect |
|---|---|
| `DRIVESYNC_DATA_DIR` | Where settings and the catalog copy are kept, instead of `%APPDATA%\DriveSync` |
| `DRIVESYNC_RECYCLE_BIN_DIR` | A folder that stands in for the Recycle Bin |

With both set, two developer commands are available (they refuse to run otherwise):

```
DriveSync.exe --dev-demo <folder>         builds sample PC folders, three backups and some history in <folder>
DriveSync.exe --dev-screenshots <folder>  renders every screen, light and dark, to PNG files in <folder>,
                                          then runs the main flows on the demo data
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
