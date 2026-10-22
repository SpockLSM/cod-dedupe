# CoD Shared Install

Keep Call of Duty installed through the **Xbox app, Steam and Battle.net** at the same time while storing the game data on disk **once**.

COD Points stay on the store you bought them in, so some players keep CoD installed through more than one store. Each install is well over 100 GB, and almost all of it is the same files. This tool finds your installs and makes them share one copy. Each store still sees a complete, normal install.

On the PC it was built on, three installs that would take about 470 GB use about 230 GB.

## How it works

Each store keeps its own install folder. The tool finds game data files that are **byte-for-byte identical** across stores and turns them into NTFS [hard links](https://learn.microsoft.com/windows/win32/fileio/hard-links-and-junctions): one copy of the data on disk that appears as a real file in every store's folder.

- **You pick a main store.** Its files are never modified. The other stores' copies are replaced with links to it.
- **Only static game data is shared** (`.xsub`, `.xpak`, `.ff`, `.fp`). Executables, store files, shader caches and settings stay separate.
- **Nothing is linked without a full byte-by-byte compare first.** If one store is on a different patch, whatever differs keeps its own copy.
- **Hard links aren't shortcuts.** No store can tell the difference, and uninstalling one store leaves the other installs complete.

## Requirements

- Windows 10 or 11.
- The installs must be on the **same drive**, because hard links can't cross drives. Installs on other drives are listed and left alone.
- An Xbox app install in a normal games folder (`XboxGames`), which is where the Xbox app installs games today. Installs inside the protected `WindowsApps` folder can't be shared.
- No admin rights needed.

## Use it

1. Download this repository (**Code > Download ZIP**) and extract it anywhere.
2. Close Call of Duty, Steam, Battle.net and the Xbox app, and make sure nothing is downloading.
3. Double-click **`Share CoD files.cmd`**.
4. The first time, pick your main store: the one you play from most.
5. Confirm. The first run reads every file, which takes a few minutes per 100 GB.

The tool finds your installs automatically from Steam's library list, Battle.net's install records and the Xbox app's library folders, on any drive.

### After every Call of Duty update

Updates can split shared files back into separate copies. That costs space but never breaks anything. Once **every** store has finished updating, run `Share CoD files.cmd` again to merge them back.

If a store ever says files are damaged, run that store's repair, then run the tool again:

- **Steam:** Properties > Installed Files > Verify integrity of game files.
- **Battle.net:** the cog next to Play > Scan and Repair.
- **Xbox app:** Manage > Files > Verify and repair.

This can happen when stores are on different patches and one of them updates a shared file in place.

## Options

Add options after the file name (`"Share CoD files.cmd" -DryRun`) or run the script directly (`powershell -ExecutionPolicy Bypass -File cod-shared-install.ps1 -DryRun`).

| Option | What it does |
|---|---|
| `-DryRun` | Report what would be shared. Changes nothing. |
| `-ChooseMain` | Pick the main store again. |
| `-Main Xbox` / `Steam` / `Battle.net` | Set the main store without being asked. |
| `-Yes` | Don't ask for confirmation. |
| `-Unshare Steam` | Give one store its own separate copies again. Needs free space equal to its shared data. |
| `-XboxPath`, `-SteamPath`, `-BattleNetPath` | Use this folder instead of searching (if an install isn't found). |
| `-Force` | Run even if the game or a launcher is open. Not recommended. |

Your main-store choice is saved in `%LOCALAPPDATA%\CodSharedInstall\settings.json`, and every run writes a log to `%LOCALAPPDATA%\CodSharedInstall\logs`.

## Undo

There's nothing to undo to keep things working: every store already has complete files.

To give a store its own copies again, for example before moving it to another drive, run it with `-Unshare <store>`. To stop sharing entirely, unshare every store except your main one.

## FAQ

**Can this get me banned?**
The tool doesn't modify, patch or inject anything. Every game file keeps its exact original bytes; only how the files are stored on disk changes. Still, it isn't made or endorsed by Activision, so use it at your own risk.

**Why do Explorer and the store apps still show each install at full size?**
They count every name of a file, so shared data is counted once per store. The real saving shows up in your drive's free space, and the tool prints how much it freed.

**One store is a patch behind. Is that a problem?**
No. Files that differ aren't linked, so each store keeps the version it needs. Update every store, then run the tool again to share the new files.

**Is my main store's install ever changed?**
No. Only the other stores' folders are written to.

**My installs are on different drives.**
Move one of them with its store's own move option so they're on the same drive, then run the tool again.

## How it finds your installs

| Store | Where it looks |
|---|---|
| Steam | Steam's install path in the registry, then every library in `libraryfolders.vdf`, then `appmanifest_1938090.acf` (the Call of Duty app) |
| Battle.net | Windows' installed-programs list (product code `auks`) and Battle.net's own `product.db`, then the `_retail_` game folder |
| Xbox app | Each drive's `.GamingRoot` marker (the Xbox app's library folders), then the game whose `MicrosoftGame.Config` names `38985CA0.COREBase` |

## Disclaimer

Not affiliated with or endorsed by Activision, Microsoft, Valve or Blizzard. Call of Duty and the store names are trademarks of their owners. Provided as is, without warranty. See [LICENSE](LICENSE).
