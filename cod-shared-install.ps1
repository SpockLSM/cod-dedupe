<#
.SYNOPSIS
  Keeps one copy of Call of Duty's game data on disk for the Xbox app, Steam and Battle.net.

.DESCRIPTION
  COD Points stay on the store you bought them in, so some players keep Call of Duty installed
  through more than one store. Each install is well over 100 GB of the same data.

  This tool finds every Call of Duty install on the PC, asks which store is your main one, and
  turns game data files that are byte-for-byte identical across stores into NTFS hard links:
  one copy on disk that appears as a normal file in every store's folder.

  - Your main store's files are never modified. Only the other stores' copies are replaced.
  - Only static game data is shared (.xsub .xpak .ff .fp). Executables, store files, caches
    and settings stay separate.
  - Every file is compared byte by byte before it is linked. A store on a different patch
    keeps its own copy of whatever differs.
  - Safe to re-run. Run it again after every game update to re-merge what the update split.

.PARAMETER Main
  The store whose copy is kept: Xbox, Steam or Battle.net. Asked for on the first run and
  remembered after that.

.PARAMETER ChooseMain
  Ask for the main store again.

.PARAMETER DryRun
  Compare and report only. Changes nothing.

.PARAMETER Yes
  Don't ask for confirmation before linking.

.PARAMETER Unshare
  Give one store (Xbox, Steam or Battle.net) its own separate copies again. Needs free space
  equal to the data being copied.

.PARAMETER XboxPath
  Use this Xbox app install instead of searching for one (the game folder or its parent).

.PARAMETER SteamPath
  Use this Steam install instead of searching for one.

.PARAMETER BattleNetPath
  Use this Battle.net install instead of searching for one.

.PARAMETER Force
  Run even if the game or a launcher is open.

.EXAMPLE
  .\cod-shared-install.ps1
  Find the installs, ask for the main store the first time, then share.

.EXAMPLE
  .\cod-shared-install.ps1 -DryRun
  Show what would be shared without changing anything.
#>
param(
  [ValidateSet('Xbox', 'Steam', 'Battle.net')]
  [string]$Main,
  [switch]$ChooseMain,
  [switch]$DryRun,
  [switch]$Yes,
  [ValidateSet('Xbox', 'Steam', 'Battle.net')]
  [string]$Unshare,
  [string]$XboxPath,
  [string]$SteamPath,
  [string]$BattleNetPath,
  [switch]$Force,
  [string]$Filter = '*'
)

$ErrorActionPreference = 'Stop'
$ToolVersion = '1.0.0'

$Stores = 'Xbox', 'Steam', 'Battle.net'
$StoreLabel = @{ 'Xbox' = 'Xbox app'; 'Steam' = 'Steam'; 'Battle.net' = 'Battle.net' }
$SteamAppId = '1938090'               # "Call of Duty" (the HQ) on Steam
$BattleNetProduct = 'auks'            # "Call of Duty" (the HQ) on Battle.net
$XboxIdentity = '38985CA0.COREBase'   # "Call of Duty" (the HQ) on the Xbox app
$ShareExtensions = '.xsub', '.xpak', '.ff', '.fp'
# Written by the game at run time, or store-specific: never shared.
$SkipFolders = 'main', 'shadercache', 'xpak_cache', 'D3D12', 'BlizzardBrowser'
$TmpSuffix = '.cod-shared.tmp'
$CopySuffix = '.cod-shared.copy'
$DataDir = Join-Path $env:LOCALAPPDATA 'CodSharedInstall'
$SettingsPath = Join-Path $DataDir 'settings.json'

if (-not ('CodSharedLink' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class CodSharedLink {
  [StructLayout(LayoutKind.Sequential)]
  struct FileInfoByHandle {
    public uint Attributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
    public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
  }

  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool GetFileInformationByHandle(SafeFileHandle h, out FileInfoByHandle info);
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern bool CreateHardLinkW(string newName, string existing, IntPtr sa);
  [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
  static extern int memcmp(byte[] a, byte[] b, UIntPtr count);

  static FileInfoByHandle Info(string path) {
    // FILE_READ_ATTRIBUTES, share read/write/delete, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS (so folders open too)
    using (SafeFileHandle h = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
      if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
      FileInfoByHandle info;
      if (!GetFileInformationByHandle(h, out info)) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
      return info;
    }
  }

  // Same key = same data on disk (the names are already hard links of one file).
  public static string FileKey(string path) {
    FileInfoByHandle i = Info(path);
    return i.VolumeSerial.ToString("X8") + ":" + i.IndexHigh.ToString("X8") + i.IndexLow.ToString("X8");
  }

  public static string VolumeKey(string path) { return Info(path).VolumeSerial.ToString("X8"); }

  public static uint LinkCount(string path) { return Info(path).Links; }

  public static void CreateHardLink(string newName, string existing) {
    if (!CreateHardLinkW(newName, existing, IntPtr.Zero))
      throw new Win32Exception(Marshal.GetLastWin32Error(), "link " + newName + " -> " + existing);
  }

  public static void Delete(string path) {
    FileAttributes a = File.GetAttributes(path);
    if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
    File.Delete(path);
  }

  const int Chunk = 8 << 20;
  static readonly byte[] BufA = new byte[Chunk];
  static readonly byte[] BufB = new byte[Chunk];

  static int ReadFull(Stream s, byte[] buf) {
    int total = 0;
    while (total < buf.Length) {
      int n = s.Read(buf, total, buf.Length - total);
      if (n == 0) break;
      total += n;
    }
    return total;
  }

  // Byte-for-byte compare. Refuses (throws) if another program has either file open for writing.
  public static bool ContentEqual(string a, string b) {
    using (FileStream fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan))
    using (FileStream fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan)) {
      if (fa.Length != fb.Length) return false;
      while (true) {
        int na = ReadFull(fa, BufA);
        int nb = ReadFull(fb, BufB);
        if (na != nb) return false;
        if (na == 0) return true;
        if (memcmp(BufA, BufB, (UIntPtr)na) != 0) return false;
      }
    }
  }
}
'@
}

function GB([double]$bytes) { '{0:N1} GB' -f ($bytes / 1GB) }

# ---------------------------------------------------------------------------------------------
# Finding installs

# The folder that holds cod.exe, given that folder or a store's install folder above it.
function Resolve-GameFolder([string]$dir) {
  if (-not $dir) { return $null }
  $dir = $dir.TrimEnd('\')
  foreach ($candidate in $dir, (Join-Path $dir 'Content'), (Join-Path $dir '_retail_')) {
    if (Test-Path -LiteralPath (Join-Path $candidate 'cod.exe')) { return (Resolve-Path -LiteralPath $candidate).ProviderPath }
  }
  # Battle.net flavours other than _retail_ (_beta_, _ptr_ ...)
  if (Test-Path -LiteralPath $dir) {
    foreach ($sub in Get-ChildItem -LiteralPath $dir -Directory -Filter '_*_' -ErrorAction SilentlyContinue) {
      if (Test-Path -LiteralPath (Join-Path $sub.FullName 'cod.exe')) { return $sub.FullName }
    }
  }
  return $null
}

function Find-XboxInstalls {
  $found = @()
  foreach ($drive in [IO.DriveInfo]::GetDrives()) {
    if ($drive.DriveType -ne 'Fixed' -or -not $drive.IsReady) { continue }
    $driveRoot = $drive.RootDirectory.FullName
    $libraries = @('XboxGames')
    # The Xbox app marks each drive it installs to with .GamingRoot: 'RGBX', a 32-bit count,
    # then null-terminated UTF-16 library folder paths.
    $marker = Join-Path $driveRoot '.GamingRoot'
    if (Test-Path -LiteralPath $marker) {
      try {
        $bytes = [IO.File]::ReadAllBytes($marker)
        if ($bytes.Length -gt 8) { $libraries += [Text.Encoding]::Unicode.GetString($bytes, 8, $bytes.Length - 8).Split([char]0) | Where-Object { $_ } }
      }
      catch {}
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
      $libDir = if ($lib -match '^[A-Za-z]:') { $lib } else { Join-Path $driveRoot $lib.TrimStart('\') }
      if (-not (Test-Path -LiteralPath $libDir)) { continue }
      foreach ($game in Get-ChildItem -LiteralPath $libDir -Directory -ErrorAction SilentlyContinue) {
        $content = Join-Path $game.FullName 'Content'
        $config = Join-Path $content 'MicrosoftGame.Config'
        if (-not (Test-Path -LiteralPath (Join-Path $content 'cod.exe'))) { continue }
        if (-not (Test-Path -LiteralPath $config)) { continue }
        if (Select-String -LiteralPath $config -Pattern ('Name="' + [regex]::Escape($XboxIdentity) + '"') -Quiet -ErrorAction SilentlyContinue) { $found += $content }
      }
    }
  }
  $found
}

function Find-SteamInstalls {
  $steamRoots = @()
  foreach ($k in @(@('HKCU:\Software\Valve\Steam', 'SteamPath'), @('HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath'), @('HKLM:\SOFTWARE\Valve\Steam', 'InstallPath'))) {
    $v = (Get-ItemProperty -LiteralPath $k[0] -Name $k[1] -ErrorAction SilentlyContinue).($k[1])
    if ($v) { $steamRoots += ($v -replace '/', '\') }
  }
  $libraries = @()
  foreach ($root in ($steamRoots | Select-Object -Unique)) {
    $libraries += $root
    foreach ($vdf in (Join-Path $root 'steamapps\libraryfolders.vdf'), (Join-Path $root 'config\libraryfolders.vdf')) {
      if (-not (Test-Path -LiteralPath $vdf)) { continue }
      foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) { $libraries += ($m.Groups[1].Value -replace '\\\\', '\') }
    }
  }
  $found = @()
  foreach ($lib in ($libraries | ForEach-Object { $_.TrimEnd('\') } | Sort-Object -Unique)) {
    $manifest = Join-Path $lib "steamapps\appmanifest_$SteamAppId.acf"
    if (-not (Test-Path -LiteralPath $manifest)) { continue }
    $m = [regex]::Match((Get-Content -LiteralPath $manifest -Raw), '"installdir"\s+"([^"]+)"')
    if ($m.Success) { $found += Resolve-GameFolder (Join-Path $lib ('steamapps\common\' + $m.Groups[1].Value)) }
  }
  # Fallback: Windows' list of installed programs.
  foreach ($e in Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue) {
    if ($e.PSChildName -eq "Steam App $SteamAppId" -and $e.InstallLocation) { $found += Resolve-GameFolder $e.InstallLocation }
  }
  $found | Where-Object { $_ }
}

function Find-BattleNetInstalls {
  $dirs = @()
  foreach ($e in Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue) {
    if ($e.UninstallString -match ('--uid=' + $BattleNetProduct + '\b') -and $e.InstallLocation) { $dirs += $e.InstallLocation }
  }
  # Battle.net's own install list stores paths as plain text inside a binary file.
  $db = Join-Path $env:ProgramData 'Battle.net\Agent\product.db'
  if (Test-Path -LiteralPath $db) {
    $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($db))
    foreach ($m in [regex]::Matches($text, '[A-Za-z]:[/\\][^\x00-\x1f"*?<>|]{2,240}')) {
      $p = $m.Value -replace '/', '\'
      # The path is followed by binary data that can look like text; trim until a folder matches.
      for ($i = 0; $i -lt 12 -and $p.Length -gt 3; $i++) {
        if (Test-Path -LiteralPath (Join-Path $p '.build.info')) { $dirs += $p; break }
        $p = $p.Substring(0, $p.Length - 1)
      }
    }
  }
  $found = @()
  foreach ($d in ($dirs | ForEach-Object { $_.TrimEnd('\') } | Sort-Object -Unique)) {
    $info = Join-Path $d '.build.info'
    if (-not (Test-Path -LiteralPath $info)) { continue }
    if (-not (Select-String -LiteralPath $info -Pattern ('\|' + $BattleNetProduct + '\s*$') -Quiet)) { continue }
    $found += Resolve-GameFolder $d
  }
  $found | Where-Object { $_ }
}

function Find-Installs {
  $list = @()
  $overrides = @{ 'Xbox' = $XboxPath; 'Steam' = $SteamPath; 'Battle.net' = $BattleNetPath }
  foreach ($store in $Stores) {
    if ($overrides[$store]) {
      $dir = Resolve-GameFolder $overrides[$store]
      if (-not $dir) { throw "No cod.exe found under the $store path you gave: $($overrides[$store])" }
      $paths = @($dir)
    }
    else {
      $paths = switch ($store) { 'Xbox' { Find-XboxInstalls } 'Steam' { Find-SteamInstalls } 'Battle.net' { Find-BattleNetInstalls } }
    }
    foreach ($p in (@($paths) | Where-Object { $_ } | Sort-Object -Unique)) {
      $list += [pscustomobject]@{ Store = $store; Root = $p; Volume = [CodSharedLink]::VolumeKey($p); Drive = [IO.Path]::GetPathRoot($p).TrimEnd('\'); Name = $StoreLabel[$store] }
    }
  }
  # Name repeated stores by drive, e.g. "Steam (D:)".
  foreach ($g in ($list | Group-Object Store | Where-Object Count -gt 1)) { foreach ($i in $g.Group) { $i.Name = '{0} ({1})' -f $StoreLabel[$i.Store], $i.Drive } }
  $list
}

# ---------------------------------------------------------------------------------------------
# Game data inventory

function Get-GameData($install) {
  $map = @{}
  foreach ($path in [IO.Directory]::EnumerateFiles($install.Root, '*', [IO.SearchOption]::AllDirectories)) {
    if ($path.EndsWith($TmpSuffix, [StringComparison]::OrdinalIgnoreCase) -or $path.EndsWith($CopySuffix, [StringComparison]::OrdinalIgnoreCase)) {
      Repair-Leftover $path
      continue
    }
    $rel = $path.Substring($install.Root.Length + 1)
    if ($rel -notlike $Filter) { continue }
    if ($ShareExtensions -notcontains [IO.Path]::GetExtension($rel)) { continue }
    $parts = $rel.Split('\')
    $skip = $false
    for ($i = 0; $i -lt $parts.Length - 1; $i++) { if ($SkipFolders -contains $parts[$i]) { $skip = $true; break } }
    if ($skip) { continue }
    $len = (New-Object IO.FileInfo $path).Length
    if ($len -eq 0) { continue }   # placeholders for content a store has not installed
    $map[$rel.ToLowerInvariant()] = [pscustomobject]@{ Path = $path; Size = $len }
  }
  $map
}

# Put back anything an interrupted run left half-done.
function Repair-Leftover([string]$path) {
  if ($DryRun) { Write-Log "leftover from an interrupted run: $path"; return }
  if ($path.EndsWith($CopySuffix, [StringComparison]::OrdinalIgnoreCase)) { [CodSharedLink]::Delete($path); Write-Log "removed partial copy $path"; return }
  $orig = $path.Substring(0, $path.Length - $TmpSuffix.Length)
  if (Test-Path -LiteralPath $orig) { [CodSharedLink]::Delete($path); Write-Log "removed leftover $path" }
  else { [IO.File]::Move($path, $orig); Write-Log "restored leftover $path" }
}

# ---------------------------------------------------------------------------------------------
# Settings, logging, prompts

function Read-Settings {
  if (Test-Path -LiteralPath $SettingsPath) { try { return Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json } catch {} }
  [pscustomobject]@{ Main = $null }
}

function Save-Settings($settings) {
  New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
  $settings | ConvertTo-Json | Set-Content -LiteralPath $SettingsPath -Encoding ASCII
}

$script:Log = $null
function Write-Log([string]$msg) { if ($script:Log) { $script:Log.WriteLine(('{0:HH:mm:ss} {1}' -f (Get-Date), $msg)) } }
function Say([string]$msg) { Write-Host $msg; Write-Log $msg }

function Ask([string]$question) {
  try { return Read-Host $question } catch { throw 'This needs an answer, but no one can type one here. Run it in a console, or pass -Main and -Yes.' }
}

function Wait-ForClosedLaunchers {
  while ($true) {
    $busy = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in 'cod', 'steam', 'Battle.net', 'XboxPcApp' -or
        ($_.ProcessName -eq 'Agent' -and $_.Path -like '*Battle.net*')
      } | ForEach-Object { switch ($_.ProcessName) { 'cod' { 'Call of Duty' } 'steam' { 'Steam' } 'XboxPcApp' { 'Xbox app' } default { 'Battle.net' } } } | Select-Object -Unique)
    if (-not $busy.Count -or $Force -or $DryRun) { return }
    Say ('Close these first: ' + ($busy -join ', ') + '. Make sure no store is downloading or updating Call of Duty.')
    if ((Ask 'Press Enter to check again, or type Q to quit') -match '^\s*q') { exit 2 }
  }
}

# ---------------------------------------------------------------------------------------------
# Main

New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null
$logName = 'cod-shared-install-{0:yyyyMMdd-HHmmss}{1}.log' -f (Get-Date), $(if ($DryRun) { '-dryrun' } elseif ($Unshare) { '-unshare' } else { '' })
$LogPath = Join-Path $DataDir "logs\$logName"
$script:Log = New-Object IO.StreamWriter($LogPath, $false, [Text.Encoding]::UTF8)

try {
  Say "CoD Shared Install $ToolVersion$(if ($DryRun) { ' (dry run: nothing will be changed)' })"
  Say ''

  # 1. Find the installs.
  $installs = @(Find-Installs)
  if (-not $installs.Count) {
    Say 'No Call of Duty install found from the Xbox app, Steam or Battle.net.'
    Say 'If yours is somewhere unusual, pass its folder with -XboxPath, -SteamPath or -BattleNetPath.'
    exit 1
  }
  Say 'Call of Duty installs found:'
  for ($i = 0; $i -lt $installs.Count; $i++) { Say ('  [{0}] {1,-16} {2}' -f ($i + 1), $installs[$i].Name, $installs[$i].Root) }
  Say ''
  foreach ($store in $Stores) {
    if ($installs.Store -notcontains $store) { Write-Log "$store install not found" }
  }

  Wait-ForClosedLaunchers

  # 2a. Unshare: give one store its own copies back.
  if ($Unshare) {
    $targets = @($installs | Where-Object Store -eq $Unshare)
    if (-not $targets.Count) { Say "No $($StoreLabel[$Unshare]) install found."; exit 1 }
    foreach ($t in $targets) {
      $files = @((Get-GameData $t).Values | Where-Object { [CodSharedLink]::LinkCount($_.Path) -gt 1 })
      $need = ($files | Measure-Object Size -Sum).Sum
      $free = (New-Object IO.DriveInfo $t.Drive).AvailableFreeSpace
      Say ('{0}: {1:N0} shared files, {2}. Free space: {4} on {3}' -f $t.Name, $files.Count, (GB $need), $t.Drive, (GB $free))
      if (-not $files.Count) { continue }
      if ($need -ge $free - 2GB) { Say 'Not enough free space to give it separate copies.'; exit 1 }
      if ($DryRun) { continue }
      if (-not $Yes -and (Ask "Copy them so $($t.Name) has its own files? (Y/N)") -notmatch '^\s*y') { exit 0 }
      $done = 0; $errors = 0
      foreach ($f in $files) {
        $done++
        if ($done % 50 -eq 0) { Write-Progress -Activity "Giving $($t.Name) its own copies" -Status ('{0:N0} / {1:N0}' -f $done, $files.Count) -PercentComplete (100 * $done / $files.Count) }
        try {
          $copy = $f.Path + $CopySuffix
          [IO.File]::Copy($f.Path, $copy, $true)
          [IO.File]::Move($f.Path, $f.Path + $TmpSuffix)
          [IO.File]::Move($copy, $f.Path)
          [CodSharedLink]::Delete($f.Path + $TmpSuffix)
          Write-Log "unshared $($f.Path)"
        }
        catch { $errors++; Write-Log "ERROR $($f.Path): $($_.Exception.Message)" }
      }
      Write-Progress -Activity "Giving $($t.Name) its own copies" -Completed
      Say ('{0}: {1:N0} files now separate, {2} errors.' -f $t.Name, ($files.Count - $errors), $errors)
    }
    Say "Log: $LogPath"
    exit 0
  }

  if ($installs.Count -lt 2) { Say 'Only one install found: there is nothing to share it with.'; exit 0 }

  # 2b. Which store is the main one?
  $settings = Read-Settings
  if ($Main) { $settings.Main = $Main }
  elseif ($ChooseMain -or -not $settings.Main -or ($installs.Store -notcontains $settings.Main)) {
    $choices = @($Stores | Where-Object { $installs.Store -contains $_ })
    Say 'Which store do you play from most? Its copy is kept as is; the other stores are pointed at it.'
    for ($i = 0; $i -lt $choices.Count; $i++) { Say ('  [{0}] {1}' -f ($i + 1), $StoreLabel[$choices[$i]]) }
    while ($true) {
      $a = Ask ('Choose 1-{0}' -f $choices.Count)
      $n = 0
      if ([int]::TryParse($a, [ref]$n) -and $n -ge 1 -and $n -le $choices.Count) { $settings.Main = $choices[$n - 1]; break }
    }
    Say ''
  }
  else { Say "Main store: $($StoreLabel[$settings.Main]) (saved; run with -ChooseMain to change it)" }
  Save-Settings $settings
  $mainStore = $settings.Main
  Write-Log "main store: $mainStore"

  # Main store first; the others keep the usual order.
  $order = @($mainStore) + @($Stores | Where-Object { $_ -ne $mainStore })
  $installs = @($installs | Sort-Object { [array]::IndexOf($order, $_.Store) })

  # 3. Hard links only work within one drive: group the installs by drive.
  $groups = @($installs | Group-Object Volume | Where-Object Count -gt 1)
  foreach ($lonely in ($installs | Group-Object Volume | Where-Object Count -eq 1)) {
    Say ('{0} is on {1}, with no other Call of Duty install on that drive, so it can''t share files.' -f $lonely.Group[0].Name, $lonely.Group[0].Drive)
  }
  if (-not $groups.Count) { Say 'Installs must be on the same drive to share files. Move one with its store''s move option, then run this again.'; exit 0 }

  # 4. Inventory.
  Say 'Reading the game folders...'
  $inv = @{}
  $sharing = @($groups | ForEach-Object { $_.Group })
  foreach ($i in $sharing) {
    $inv[$i.Root] = Get-GameData $i
    Say ('  {0,-16} {1,7:N0} game data files, {2}' -f $i.Name, $inv[$i.Root].Count, (GB (($inv[$i.Root].Values | Measure-Object Size -Sum).Sum)))
  }

  # 5. Check each store's folder accepts links before relying on it.
  foreach ($g in $groups) {
    $first = $g.Group | Where-Object { $inv[$_.Root].Count } | Select-Object -First 1
    if (-not $first) { continue }
    $sample = $inv[$first.Root].Values | Select-Object -First 1
    foreach ($other in ($g.Group | Where-Object { $_.Root -ne $first.Root -and $inv[$_.Root].Count })) {
      $test = Join-Path $other.Root '.cod-shared-install-test.tmp'
      try {
        if (Test-Path -LiteralPath $test) { [CodSharedLink]::Delete($test) }
        [CodSharedLink]::CreateHardLink($test, $sample.Path)
        [CodSharedLink]::Delete($test)
      }
      catch {
        Say ('Can''t create links in {0}''s folder, so it is left out: {1}' -f $other.Name, $_.Exception.Message)
        $inv[$other.Root] = @{}
      }
    }
  }

  if (-not $DryRun -and -not $Yes) {
    $names = ($sharing | Where-Object { $inv[$_.Root].Count } | ForEach-Object { if ($_.Store -eq $mainStore) { $_.Name + ' (main)' } else { $_.Name } }) -join ', '
    if ((Ask "Share identical game files between $names? (Y/N)") -notmatch '^\s*y') { Say 'Nothing changed.'; exit 0 }
  }

  # 6. Compare and link.
  $freeBefore = @{}
  foreach ($g in $groups) { $freeBefore[$g.Group[0].Drive] = (New-Object IO.DriveInfo $g.Group[0].Drive).AvailableFreeSpace }
  $n = @{ Shared = 0; SharedB = 0; Linked = 0; LinkedB = 0; Differ = 0; DifferB = 0; Errors = 0; Leftover = 0 }
  $equal = @{}   # "keptKey|otherKey" -> bool, so a file already shared by two stores is read once
  $sw = [Diagnostics.Stopwatch]::StartNew()
  foreach ($g in $groups) {
    $members = @($g.Group)
    $keys = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($m in $members) { foreach ($k in $inv[$m.Root].Keys) { [void]$keys.Add($k) } }
    $candidates = New-Object 'System.Collections.Generic.List[string]'
    foreach ($k in $keys) {
      $c = 0
      foreach ($m in $members) { if ($inv[$m.Root].ContainsKey($k)) { $c++ } }
      if ($c -ge 2) { $candidates.Add($k) }
    }
    $candidates.Sort([StringComparer]::OrdinalIgnoreCase)
    Say ('Comparing {0:N0} files found in more than one store on {1}...' -f $candidates.Count, $members[0].Drive)

    $done = 0
    foreach ($rel in $candidates) {
      $done++
      if ($done % 200 -eq 0) {
        Write-Progress -Activity 'Sharing Call of Duty files' -Status ('{0:N0} / {1:N0}   linked {2}' -f $done, $candidates.Count, (GB $n.LinkedB)) -PercentComplete (100 * $done / $candidates.Count)
      }
      $kept = New-Object System.Collections.ArrayList   # distinct copies so far, main store first
      foreach ($m in $members) {
        $f = $inv[$m.Root][$rel]
        if (-not $f) { continue }
        try {
          $key = [CodSharedLink]::FileKey($f.Path)
          $match = $null
          foreach ($k in $kept) {
            if ($k.Size -ne $f.Size) { continue }
            if ($k.Key -eq $key) { $match = $k; $n.Shared++; $n.SharedB += $f.Size; break }
            $ck = $k.Key + '|' + $key
            if (-not $equal.ContainsKey($ck)) { $equal[$ck] = [CodSharedLink]::ContentEqual($k.Path, $f.Path) }
            if ($equal[$ck]) {
              $match = $k
              if ($DryRun) { Write-Log "would link [$($m.Name)] $rel -> [$($k.Name)]" }
              else {
                $tmp = $f.Path + $TmpSuffix
                [IO.File]::Move($f.Path, $tmp)
                try { [CodSharedLink]::CreateHardLink($f.Path, $k.Path) }
                catch { [IO.File]::Move($tmp, $f.Path); throw }
                try { [CodSharedLink]::Delete($tmp) }
                catch { $n.Leftover++; Write-Log "could not delete $tmp (removed on the next run): $($_.Exception.Message)" }
                Write-Log "linked [$($m.Name)] $rel -> [$($k.Name)]"
              }
              $n.Linked++; $n.LinkedB += $f.Size
              break
            }
          }
          if (-not $match) {
            if ($kept.Count) { $n.Differ++; $n.DifferB += $f.Size; Write-Log "differs, kept separate: [$($m.Name)] $rel" }
            [void]$kept.Add([pscustomobject]@{ Name = $m.Name; Path = $f.Path; Size = $f.Size; Key = $key })
          }
        }
        catch { $n.Errors++; Write-Log "ERROR [$($m.Name)] ${rel}: $($_.Exception.Message)" }
      }
    }
    Write-Progress -Activity 'Sharing Call of Duty files' -Completed
  }

  # 7. Report.
  Say ''
  Say ('Already shared:          {0,8:N0} files  {1}' -f $n.Shared, (GB $n.SharedB))
  Say ('{0,-24} {1,8:N0} files  {2}' -f $(if ($DryRun) { 'Would share now:' } else { 'Shared now:' }), $n.Linked, (GB $n.LinkedB))
  Say ('Different, kept apart:   {0,8:N0} files  {1}' -f $n.Differ, (GB $n.DifferB))
  if ($n.Differ) { Say '  (Usually one store is on another patch. Update every store, then run this again.)' }
  Say ('Errors:                  {0,8:N0}' -f $n.Errors)
  if ($n.Leftover) { Say ('Temp files left behind:  {0,8:N0}  (cleaned up on the next run)' -f $n.Leftover) }
  if (-not $DryRun) {
    foreach ($d in $freeBefore.Keys) {
      $after = (New-Object IO.DriveInfo $d).AvailableFreeSpace
      Say ('Free space on {0}  {1} -> {2}  (freed {3})' -f $d, (GB $freeBefore[$d]), (GB $after), (GB ($after - $freeBefore[$d])))
    }
  }
  Say ('Took {0:N0} s. Log: {1}' -f $sw.Elapsed.TotalSeconds, $LogPath)
  if ($n.Errors) { exit 1 }
}
catch {
  Say ('Stopped: ' + $_.Exception.Message)
  Write-Log $_.ScriptStackTrace
  exit 1
}
finally {
  $script:Log.Dispose()
}
