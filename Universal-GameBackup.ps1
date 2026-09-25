<#
.SYNOPSIS
    Universal Windows game backup/restore helper.

.DESCRIPTION
    Backs up:
      - Steam installed games, workshop content, appmanifest files, userdata
      - Epic installed games discovered from Epic .item manifests
      - GOG/EA/Ubisoft/Blizzard/Rockstar/etc. installs discoverable through the Windows uninstall registry
      - Launcher metadata for Steam, Epic, GOG, EA, Ubisoft, Battle.net, Rockstar
      - Broad Windows game-save locations:
          Documents
          Saved Games
          AppData\Local
          AppData\LocalLow
          AppData\Roaming

    Restore uses the generated manifest.json and can optionally remap drive letters.

.NOTES
    - Run PowerShell as Administrator, especially for Restore.
    - CLOSE all game launchers before backup/restore.
    - Microsoft Store/Xbox "WindowsApps" game binaries are intentionally not copied.
      Their per-user save/config data under AppData is included.
    - Backups can be very large when -UserDataCoverage Comprehensive is used.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("Backup", "Restore")]
    [string]$Mode = "",

    # BACKUP: parent folder where a timestamped backup will be created.
    # Automatically defaults to D:\GameBackups if D: exists, another secondary drive, or C:\GameBackups.
    [string]$Destination = "",

    # RESTORE: exact backup-set folder containing manifest.json.
    [string]$BackupPath = "",

    # Backup scope options:
    #   Full         = Installed game files + Saves/config + Launcher metadata (everything)
    #   GamesOnly    = Installed game files & manifests only (no saves, no Documents/AppData)
    #   SavesOnly    = Game saves, profiles & configs only (lightweight & fast)
    #   MetadataOnly = Launcher manifests & configs only
    [ValidateSet("Full", "GamesOnly", "SavesOnly", "MetadataOnly")]
    [string]$Scope = "",

    # User-data coverage for Full and SavesOnly:
    #   Targeted      = Common game saves (Documents game folders, Saved Games, game AppData) [Recommended]
    #   Comprehensive = Broad coverage (Entire Documents + all AppData Local/LocalLow/Roaming - can be very large!)
    [ValidateSet("Targeted", "Comprehensive")]
    [string]$UserDataCoverage = "Targeted",

    # Optional restore mapping, e.g. -DriveMap "D=E","F=D"
    [string[]]$DriveMap = @(),

    # Show what would happen without copying.
    [switch]$DryRun,

    # Skip interactive confirmation before copying.
    [switch]$Force
)

Set-StrictMode -Off
$ErrorActionPreference = "Continue"

# ----------------------------- Helpers -----------------------------

function Write-Section {
    param([string]$Text)
    Write-Host ""
    Write-Host ("=" * 78) -ForegroundColor DarkGray
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ("=" * 78) -ForegroundColor DarkGray
}

function Test-Administrator {
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch {
        return $false
    }
}

function Normalize-Path {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim('"'))
    try {
        return [System.IO.Path]::GetFullPath($expanded).TrimEnd('\')
    }
    catch {
        return $expanded.TrimEnd('\')
    }
}

function Get-DefaultBackupDestination {
    if (Test-Path -LiteralPath "D:\") {
        return "D:\GameBackups"
    }

    $otherDrives = Get-PSDrive -PSProvider FileSystem |
    Where-Object { $_.Name -match '^[A-Za-z]$' -and $_.Name -notmatch '^[Cc]$' -and (Test-Path -LiteralPath "$($_.Name):\") }

    if ($otherDrives) {
        $chosen = $otherDrives | Select-Object -First 1
        return ([System.IO.Path]::Combine("$($chosen.Name):\", "GameBackups"))
    }

    return "C:\GameBackups"
}

function Get-BackupRelativePath {
    param([string]$Source)

    $p = Normalize-Path $Source

    if ($p -match '^([A-Za-z]):\\(.*)$') {
        $drive = $matches[1].ToUpper()
        $rest = $matches[2]
        return (Join-Path (Join-Path "Drives" $drive) $rest)
    }

    if ($p -match '^\\\\([^\\]+)\\([^\\]+)\\?(.*)$') {
        $server = $matches[1]
        $share = $matches[2]
        $rest = $matches[3]
        return (Join-Path (Join-Path (Join-Path "UNC" $server) $share) $rest)
    }

    $safe = ($p -replace '[:*?"<>|]', '_').TrimStart('\')
    return (Join-Path "Other" $safe)
}

function Invoke-RoboCopy {
    param(
        [string]$Source,
        [string]$DestinationPath
    )

    if ($DryRun) {
        Write-Host "[DRY RUN] $Source  ->  $DestinationPath" -ForegroundColor Yellow
        return $true
    }

    if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
        Write-Warning "Destination path is empty for source: $Source"
        return $false
    }

    if (-not (Test-Path -LiteralPath $Source)) {
        Write-Warning "Source no longer exists: $Source"
        return $false
    }

    $item = Get-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $false }

    if (-not $item.PSIsContainer) {
        try {
            $parent = Split-Path -Parent $DestinationPath
            if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent)) {
                New-Item -ItemType Directory -Path $parent -Force -ErrorAction Stop | Out-Null
            }
            Copy-Item -LiteralPath $Source -Destination $DestinationPath -Force -ErrorAction Stop
            return $true
        }
        catch {
            Write-Warning "Failed to copy file '$Source' to '$DestinationPath': $($_.Exception.Message)"
            return $false
        }
    }

    try {
        if (-not (Test-Path -LiteralPath $DestinationPath)) {
            New-Item -ItemType Directory -Path $DestinationPath -Force -ErrorAction Stop | Out-Null
        }
    }
    catch {
        Write-Warning "Failed to create destination directory '$DestinationPath': $($_.Exception.Message)"
        return $false
    }

    # /XJ avoids recursing through directory junctions.
    # Exit codes 0-7 are success/non-fatal for robocopy.
    $args = @(
        $Source,
        $DestinationPath,
        "/E",
        "/COPY:DAT",
        "/DCOPY:DAT",
        "/R:2",
        "/W:2",
        "/XJ",
        "/MT:16",
        "/NP",
        "/NFL",
        "/NDL"
    )

    & robocopy @args | Out-Host
    $code = $LASTEXITCODE

    if ($code -gt 7) {
        Write-Warning "Robocopy failed with exit code $code : $Source"
        return $false
    }

    return $true
}

function Parse-DriveMap {
    param([string[]]$Mappings)

    $map = @{}
    foreach ($entry in $Mappings) {
        if ($entry -match '^\s*([A-Za-z])\s*=\s*([A-Za-z])\s*$') {
            $map[$matches[1].ToUpper()] = $matches[2].ToUpper()
        }
        else {
            Write-Warning "Ignoring invalid drive mapping '$entry'. Use D=E format."
        }
    }
    return $map
}

function Apply-DriveMap {
    param(
        [string]$OriginalPath,
        [hashtable]$Map
    )

    if ($OriginalPath -match '^([A-Za-z]):\\') {
        $old = $matches[1].ToUpper()
        if ($Map.ContainsKey($old)) {
            $new = $Map[$old]
            return ($new + $OriginalPath.Substring(1))
        }
    }
    return $OriginalPath
}

function Get-UserShellFolder {
    param(
        [string]$ValueName,
        [string]$Fallback
    )

    try {
        $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"
        $prop = Get-ItemProperty -Path $key -Name $ValueName -ErrorAction Stop
        if ($prop -and $prop.PSObject.Properties[$ValueName]) {
            $value = $prop.$ValueName
            if ($value) {
                return Normalize-Path ([Environment]::ExpandEnvironmentVariables($value))
            }
        }
    }
    catch {}

    return Normalize-Path $Fallback
}

function Get-UninstallEntries {
    $keys = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    foreach ($key in $keys) {
        Get-ItemProperty $key -ErrorAction SilentlyContinue |
        Where-Object {
            $_.PSObject.Properties['DisplayName'] -and -not [string]::IsNullOrWhiteSpace($_.DisplayName)
        }
    }
}

function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]

    $candidates = @(
        "${env:ProgramFiles(x86)}\Steam",
        "$env:ProgramFiles\Steam"
    )

    try {
        $steamKey = Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction Stop
        if ($steamKey -and $steamKey.PSObject.Properties['SteamPath'] -and $steamKey.SteamPath) {
            $candidates += $steamKey.SteamPath
        }
    }
    catch {}

    foreach ($candidate in $candidates) {
        $candidate = Normalize-Path $candidate
        if ($candidate -and (Test-Path -LiteralPath $candidate) -and -not $roots.Contains($candidate)) {
            $roots.Add($candidate)
        }
    }

    # Parse libraryfolders.vdf from every Steam root we find.
    $allRoots = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) {
        if (-not $allRoots.Contains($root)) { $allRoots.Add($root) }

        $vdf = [System.IO.Path]::Combine($root, "steamapps\libraryfolders.vdf")
        if (Test-Path -LiteralPath $vdf) {
            $raw = Get-Content -LiteralPath $vdf -Raw -ErrorAction SilentlyContinue
            if ($raw) {
                $matchesFound = [regex]::Matches($raw, '"path"\s+"([^"]+)"')
                foreach ($m in $matchesFound) {
                    $path = $m.Groups[1].Value -replace '\\\\', '\'
                    $path = Normalize-Path $path
                    if ($path -and (Test-Path -LiteralPath $path) -and -not $allRoots.Contains($path)) {
                        $allRoots.Add($path)
                    }
                }
            }
        }
    }

    return $allRoots
}

function Get-EpicInstallLocations {
    $locations = New-Object System.Collections.Generic.List[string]
    $manifestDir = [System.IO.Path]::Combine($env:ProgramData, "Epic\EpicGamesLauncher\Data\Manifests")

    if (Test-Path -LiteralPath $manifestDir) {
        Get-ChildItem -LiteralPath $manifestDir -Filter "*.item" -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                $j = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                if ($j -and $j.PSObject.Properties['InstallLocation'] -and $j.InstallLocation) {
                    $p = Normalize-Path $j.InstallLocation
                    if ($p -and (Test-Path -LiteralPath $p) -and -not $locations.Contains($p)) {
                        $locations.Add($p)
                    }
                }
            }
            catch {
                Write-Warning "Could not parse Epic manifest: $($_.FullName)"
            }
        }
    }

    return $locations
}

function Get-RegistryGameLocations {
    $locations = New-Object System.Collections.Generic.List[object]

    $publisherPattern = '(?i)(GOG|CD PROJEKT|Ubisoft|Electronic Arts|EA Games|Blizzard|Activision|Rockstar|Bethesda|2K Games|Epic Games|Valve|Paradox|SEGA|Square Enix|Bandai Namco|Capcom|Konami|THQ|Focus Entertainment|Deep Silver)'
    $excludeNamePattern = '(?i)^(Steam|GOG GALAXY|Epic Games Launcher|EA app|EA Desktop|Ubisoft Connect|Uplay|Battle\.net|Rockstar Games Launcher)$'

    foreach ($entry in Get-UninstallEntries) {
        $name = if ($entry.PSObject.Properties['DisplayName']) { [string]$entry.DisplayName } else { "" }
        $publisher = if ($entry.PSObject.Properties['Publisher']) { [string]$entry.Publisher } else { "" }
        $installLocation = if ($entry.PSObject.Properties['InstallLocation']) { [string]$entry.InstallLocation } else { "" }
        $keyName = if ($entry.PSObject.Properties['PSChildName']) { [string]$entry.PSChildName } else { "" }

        $looksLikeGame = (
            ($publisher -match $publisherPattern) -or
            ($keyName -match '(?i)GOG') -or
            ($name -match '(?i)\b(Game|Edition|Remastered|Remake)\b')
        )

        if (-not $looksLikeGame) { continue }
        if ($name -match $excludeNamePattern) { continue }
        if ([string]::IsNullOrWhiteSpace($installLocation)) { continue }

        $p = Normalize-Path $installLocation
        if ($p -and (Test-Path -LiteralPath $p)) {
            $already = $false
            foreach ($x in $locations) {
                if ($x.Path -ieq $p) { $already = $true; break }
            }

            if (-not $already) {
                $locations.Add([pscustomobject]@{
                        Name      = $name
                        Publisher = $publisher
                        Path      = $p
                    })
            }
        }
    }

    return $locations
}

# ----------------------------- Backup -----------------------------

function Invoke-Backup {
    if ([string]::IsNullOrWhiteSpace($Destination)) {
        $Destination = Get-DefaultBackupDestination
        Write-Host "No -Destination specified. Automatically defaulting to: $Destination" -ForegroundColor DarkGray
    }

    $dest = Normalize-Path $Destination
    if ([string]::IsNullOrWhiteSpace($dest)) {
        throw "A valid backup destination could not be determined."
    }

    $driveRoot = [System.IO.Path]::GetPathRoot($dest)
    if (-not [string]::IsNullOrWhiteSpace($driveRoot) -and -not (Test-Path -LiteralPath $driveRoot)) {
        $availableDrives = (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Name -match '^[A-Za-z]$' } | ForEach-Object { "$($_.Name):\" }) -join ", "
        throw "Destination drive '$driveRoot' does not exist. Available drives on this system: $availableDrives. Please specify a valid destination using -Destination."
    }

    # Avoid accidentally backing the backup into itself.
    if ($dest -like "$env:USERPROFILE\AppData\*" -or
        $dest -like "$env:USERPROFILE\Documents\*" -or
        $dest -ieq (Normalize-Path "$env:USERPROFILE\AppData") -or
        $dest -ieq (Normalize-Path "$env:USERPROFILE\Documents")) {
        throw "Choose a backup destination outside Documents/AppData, preferably another drive."
    }

    $setName = "GameBackup_{0}" -f (Get-Date -Format "yyyyMMdd_HHmmss")
    $setPath = [System.IO.Path]::Combine($dest, $setName)

    if (-not $DryRun) {
        try {
            New-Item -ItemType Directory -Path $setPath -Force -ErrorAction Stop | Out-Null
        }
        catch {
            throw "Failed to create backup directory at '$setPath': $($_.Exception.Message)"
        }
    }

    $entries = New-Object System.Collections.Generic.List[object]
    $seen = @{}

    function Add-Source {
        param(
            [string]$Path,
            [string]$Type,
            [string]$Provider,
            [string]$Description
        )

        $p = Normalize-Path $Path
        if (-not $p -or -not (Test-Path -LiteralPath $p)) { return }

        $key = $p.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { return }
        $seen[$key] = $true

        $rel = Get-BackupRelativePath $p

        $entries.Add([pscustomobject]@{
                Source         = $p
                BackupRelative = $rel
                Type           = $Type
                Provider       = $Provider
                Description    = $Description
            })
    }

    if ($Scope -in @("Full", "SavesOnly")) {
        Write-Section "Discovering user game data (Saves & Profiles)"

        $documents = Get-UserShellFolder -ValueName "Personal" -Fallback "$env:USERPROFILE\Documents"
        $savedGames = Get-UserShellFolder -ValueName "{4C5C32FF-BB9D-43B0-BF45-D1B8C7F0383A}" -Fallback "$env:USERPROFILE\Saved Games"

        if ($UserDataCoverage -eq "Comprehensive") {
            # Broad coverage: full Documents and all AppData Local/LocalLow/Roaming.
            Add-Source $documents            "UserData" "Windows" "Documents (broad game-save coverage)"
            Add-Source $savedGames           "UserData" "Windows" "Saved Games"
            Add-Source $env:LOCALAPPDATA     "UserData" "Windows" "AppData Local (broad game-save coverage)"
            Add-Source "$env:USERPROFILE\AppData\LocalLow" "UserData" "Windows" "AppData LocalLow"
            Add-Source $env:APPDATA          "UserData" "Windows" "AppData Roaming (broad game-save coverage)"
        }
        else {
            # Targeted set: game save folders in Documents + Saved Games + targeted launcher/game AppData
            Add-Source $savedGames "UserData" "Windows" "Saved Games"

            # Auto-detect game folders in Documents
            $excludeDocFolders = @(
                'Custom Office Templates', 'OneNote Notebooks', 'PowerShell', 'WindowsPowerShell',
                'Visual Studio *', 'SQL Server Management Studio', 'FeedbackHub', 'Zoom', 'Audacity',
                'Blackmagic Design', 'PDFgear', 'Moto'
            )
            if (Test-Path -LiteralPath $documents) {
                Add-Source ([System.IO.Path]::Combine($documents, "My Games")) "UserData" "Windows" "Documents\My Games"
                Get-ChildItem -LiteralPath $documents -Directory -ErrorAction SilentlyContinue |
                Where-Object {
                    $n = $_.Name
                    $exclude = $false
                    foreach ($pat in $excludeDocFolders) {
                        if ($n -like $pat) { $exclude = $true; break }
                    }
                    -not $exclude
                } |
                ForEach-Object {
                    Add-Source $_.FullName "UserData" "Windows" "Documents game save: $($_.Name)"
                }
            }

            # Known launcher & game appdata paths
            $targeted = @(
                "$env:APPDATA\.minecraft",
                "$env:APPDATA\itch",
                "$env:LOCALAPPDATA\GOG.com\Galaxy\Applications",
                "$env:LOCALAPPDATA\Rockstar Games",
                "$env:LOCALAPPDATA\Ubisoft Game Launcher",
                "$env:LOCALAPPDATA\Electronic Arts",
                "$env:LOCALAPPDATA\Sony Interactive Entertainment Inc",
                "$env:LOCALAPPDATA\REDEngine",
                "$env:LOCALAPPDATA\Black_Tree_Gaming"
            )
            foreach ($p in $targeted) {
                Add-Source $p "UserData" "Windows" "Targeted game/launcher user data"
            }

            # Scan Microsoft Store Packages strictly for actual games (e.g. Minecraft Bedrock, Forza, Halo),
            # avoiding Windows system apps, HP OMEN tools, Clipchamp, Ubuntu WSL, etc.
            $packagesDir = "$env:LOCALAPPDATA\Packages"
            if (Test-Path -LiteralPath $packagesDir) {
                Get-ChildItem -LiteralPath $packagesDir -Directory -ErrorAction SilentlyContinue |
                    Where-Object {
                        $_.Name -match '(?i)^(Microsoft\.(Minecraft|SeaofThieves|FlightSimulator|Halo|AgeofEmpires)|.*\.ForzaHorizon|.*\.StateofDecay|BethesdaSoftworks\.)'
                    } |
                    ForEach-Object {
                        Add-Source $_.FullName "UserData" "Windows" "Microsoft Store game save: $($_.Name)"
                    }
            }
        }
    }

    Write-Section "Discovering Steam"

    $steamRoots = @(Get-SteamRoots)
    foreach ($steamRoot in $steamRoots) {
        if ($Scope -in @("Full", "SavesOnly")) {
            Add-Source ([System.IO.Path]::Combine($steamRoot, "userdata")) "UserData" "Steam" "Steam per-user saves/config/cloud data"
        }

        $steamApps = [System.IO.Path]::Combine($steamRoot, "steamapps")
        if (Test-Path -LiteralPath $steamApps) {
            # Steam manifests needed to identify installed games
            if ($Scope -in @("Full", "GamesOnly", "MetadataOnly")) {
                Get-ChildItem -LiteralPath $steamApps -Filter "appmanifest_*.acf" -File -ErrorAction SilentlyContinue |
                ForEach-Object {
                    Add-Source $_.FullName "LauncherMetadata" "Steam" "Steam app manifest"
                }
            }

            if ($Scope -in @("Full", "GamesOnly")) {
                Add-Source ([System.IO.Path]::Combine($steamApps, "common")) "GameFiles" "Steam" "Steam installed games"
                Add-Source ([System.IO.Path]::Combine($steamApps, "workshop\content")) "GameFiles" "Steam" "Steam Workshop content"
            }
        }

        if ($Scope -in @("Full", "SavesOnly", "MetadataOnly")) {
            Add-Source ([System.IO.Path]::Combine($steamRoot, "config")) "LauncherMetadata" "Steam" "Steam launcher configuration"
        }
    }

    Write-Section "Discovering Epic Games"

    $epicManifestDir = [System.IO.Path]::Combine($env:ProgramData, "Epic\EpicGamesLauncher\Data\Manifests")
    if ($Scope -in @("Full", "GamesOnly", "MetadataOnly")) {
        Add-Source $epicManifestDir "LauncherMetadata" "Epic" "Epic .item manifests"
    }

    if ($Scope -in @("Full", "GamesOnly")) {
        foreach ($p in Get-EpicInstallLocations) {
            Add-Source $p "GameFiles" "Epic" "Epic installed game"
        }
    }

    if ($Scope -in @("Full", "SavesOnly", "MetadataOnly")) {
        Write-Section "Adding GOG / EA / Ubisoft / Battle.net / Rockstar metadata"

        $launcherMetadata = @(
            @{ Path = "$env:ProgramData\GOG.com\Galaxy\storage"; Provider = "GOG"; Description = "GOG Galaxy database/storage metadata" },
            @{ Path = "$env:LOCALAPPDATA\GOG.com\Galaxy"; Provider = "GOG"; Description = "GOG Galaxy local data/cloud storage" },
            @{ Path = "$env:ProgramData\EA Desktop"; Provider = "EA"; Description = "EA app metadata" },
            @{ Path = "$env:ProgramData\Electronic Arts\EA Services\License"; Provider = "EA"; Description = "EA license data" },
            @{ Path = "$env:ProgramData\Battle.net"; Provider = "Battle.net"; Description = "Battle.net metadata" },
            @{ Path = "$env:ProgramData\Blizzard Entertainment"; Provider = "Battle.net"; Description = "Blizzard metadata" },
            @{ Path = "$env:ProgramData\Rockstar Games"; Provider = "Rockstar"; Description = "Rockstar launcher metadata" },
            @{ Path = "$env:ProgramFiles\Rockstar Games"; Provider = "Rockstar"; Description = "Rockstar launcher/game data" },
            @{ Path = "${env:ProgramFiles(x86)}\Ubisoft\Ubisoft Game Launcher\savegames"; Provider = "Ubisoft"; Description = "Ubisoft Connect saves" },
            @{ Path = "$env:LOCALAPPDATA\Ubisoft Game Launcher\savegames"; Provider = "Ubisoft"; Description = "Ubisoft saves (alternate location)" }
        )

        foreach ($m in $launcherMetadata) {
            Add-Source $m.Path "LauncherMetadata" $m.Provider $m.Description
        }
    }

    if ($Scope -in @("Full", "GamesOnly")) {
        Write-Section "Discovering additional game installs through Windows registry"

        foreach ($game in Get-RegistryGameLocations) {
            Add-Source $game.Path "GameFiles" $game.Publisher $game.Name
        }
    }

    Write-Section "Backup plan"

    $entries |
    Sort-Object Type, Provider, Source |
    Format-Table Type, Provider, Description, Source -AutoSize |
    Out-Host

    if ($entries.Count -eq 0) {
        throw "Nothing was discovered to back up."
    }

    $manifest = [ordered]@{
        FormatVersion    = 1
        CreatedUtc       = (Get-Date).ToUniversalTime().ToString("o")
        ComputerName     = $env:COMPUTERNAME
        UserName         = $env:USERNAME
        WindowsVersion   = [Environment]::OSVersion.VersionString
        Scope            = $Scope
        UserDataCoverage = $UserDataCoverage
        BackupSetPath    = $setPath
        Entries          = $entries
    }

    if (-not $DryRun) {
        try {
            $manifest | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath ([System.IO.Path]::Combine($setPath, "manifest.json")) -Encoding UTF8 -ErrorAction Stop

            # Inventory is convenient to inspect without opening JSON.
            $entries |
            Select-Object Type, Provider, Description, Source, BackupRelative |
            Export-Csv -LiteralPath ([System.IO.Path]::Combine($setPath, "inventory.csv")) -NoTypeInformation -Encoding UTF8 -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not write backup manifest or inventory: $($_.Exception.Message)"
        }
    }

    if (-not $DryRun -and -not $Force) {
        Write-Host ""
        $confirm = Read-Host "Ready to backup $($entries.Count) items to '$setPath'. Proceed? [Y/n] (default: Y)"
        if ($confirm -match '^(n|no)$') {
            Write-Host "Backup cancelled by user." -ForegroundColor Yellow
            return
        }
    }

    Write-Section "Copying backup"

    $i = 0
    $ok = 0
    foreach ($entry in $entries) {
        $i++
        Write-Progress -Activity "Game backup" -Status "$i / $($entries.Count): $($entry.Description)" -PercentComplete (($i / $entries.Count) * 100)

        $dst = [System.IO.Path]::Combine($setPath, $entry.BackupRelative)
        Write-Host "[$i/$($entries.Count)] $($entry.Provider): $($entry.Source)" -ForegroundColor White

        if (Invoke-RoboCopy -Source $entry.Source -DestinationPath $dst) {
            $ok++
        }
    }

    Write-Progress -Activity "Game backup" -Completed

    Write-Section "Backup complete"
    Write-Host "Backup set : $setPath" -ForegroundColor Green
    Write-Host "Entries    : $ok / $($entries.Count) copied without fatal robocopy errors"
    Write-Host ""
    Write-Host "Keep manifest.json with the backup. Restore depends on it." -ForegroundColor Yellow
}

# ----------------------------- Restore -----------------------------

function Invoke-Restore {
    if ([string]::IsNullOrWhiteSpace($BackupPath)) {
        throw "Restore requires -BackupPath pointing to a GameBackup_YYYYMMDD_HHMMSS folder."
    }

    $setPath = Normalize-Path $BackupPath
    if ([string]::IsNullOrWhiteSpace($setPath)) {
        throw "Backup path is invalid or empty."
    }

    $driveRoot = [System.IO.Path]::GetPathRoot($setPath)
    if (-not [string]::IsNullOrWhiteSpace($driveRoot) -and -not (Test-Path -LiteralPath $driveRoot)) {
        throw "Backup path drive '$driveRoot' does not exist."
    }

    $manifestPath = [System.IO.Path]::Combine($setPath, "manifest.json")

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "manifest.json not found in: $setPath"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $map = Parse-DriveMap $DriveMap

    Write-Section "Restore plan"

    Write-Host "Backup created : $($manifest.CreatedUtc)"
    Write-Host "Original PC    : $($manifest.ComputerName)"
    Write-Host "Original user  : $($manifest.UserName)"
    Write-Host "Scope          : $($manifest.Scope)"
    Write-Host "Coverage       : $($manifest.UserDataCoverage)"

    if ($map.Count -gt 0) {
        Write-Host "Drive mappings :" -ForegroundColor Yellow
        foreach ($k in $map.Keys) {
            Write-Host "  $k`: -> $($map[$k])`:"
        }
    }

    # Restore files first, then launcher metadata, then user saves/config.
    $priority = @{
        "GameFiles"        = 1
        "LauncherMetadata" = 2
        "UserData"         = 3
    }

    $entries = @($manifest.Entries | Sort-Object {
            $t = if ($_.PSObject.Properties['Type']) { $_.Type } else { $null }
            if ($t -and $priority.ContainsKey($t)) { $priority[$t] } else { 99 }
        })

    $i = 0
    $ok = 0

    foreach ($entry in $entries) {
        $i++
        $src = [System.IO.Path]::Combine($setPath, $entry.BackupRelative)
        $dst = Apply-DriveMap -OriginalPath ([string]$entry.Source) -Map $map

        Write-Progress -Activity "Game restore" -Status "$i / $($entries.Count): $($entry.Description)" -PercentComplete (($i / $entries.Count) * 100)

        if (-not (Test-Path -LiteralPath $src)) {
            Write-Warning "Backup payload missing; skipping: $src"
            continue
        }

        # If the target drive does not exist, skip instead of writing somewhere unexpected.
        if ($dst -match '^([A-Za-z]):\\') {
            $driveRoot = "$($matches[1]):\"
            if (-not (Test-Path -LiteralPath $driveRoot)) {
                Write-Warning "Target drive does not exist for '$dst'. Use -DriveMap OLD=NEW."
                continue
            }
        }

        Write-Host "[$i/$($entries.Count)] $($entry.Provider): $src  ->  $dst" -ForegroundColor White

        if (Invoke-RoboCopy -Source $src -DestinationPath $dst) {
            $ok++
        }
    }

    Write-Progress -Activity "Game restore" -Completed

    Write-Section "Restore complete"
    Write-Host "Entries restored: $ok / $($entries.Count)" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next:" -ForegroundColor Cyan
    Write-Host "  1. Reinstall/open Steam, Epic, GOG Galaxy, EA app, Ubisoft Connect, Battle.net, etc."
    Write-Host "  2. Point each launcher at the restored game library if necessary."
    Write-Host "  3. Use Verify / Repair / Locate Installed Game where available."
    Write-Host "  4. Before accepting a cloud-save conflict, compare timestamps and choose the copy you actually want."
}

# ----------------------------- Main -----------------------------

Write-Host "Universal Windows Game Backup / Restore" -ForegroundColor Cyan

if ([string]::IsNullOrWhiteSpace($Mode)) {
    Write-Host ""
    Write-Host "Select Operation Mode:" -ForegroundColor Yellow
    Write-Host "  [1] Backup  - Back up game files, saves, and/or launcher metadata"
    Write-Host "  [2] Restore - Restore games and saves from an existing backup"
    $modeChoice = Read-Host "Enter choice [1-2] (default: 1 - Backup)"
    if ($modeChoice -eq "2" -or $modeChoice -ieq "Restore") {
        $Mode = "Restore"
    }
    else {
        $Mode = "Backup"
    }
}

if ($Mode -eq "Backup") {
    if ([string]::IsNullOrWhiteSpace($Scope)) {
        Write-Host ""
        Write-Host "Select Backup Scope:" -ForegroundColor Yellow
        Write-Host "  [1] Full          - Everything (Installed game files + Saves + Launcher metadata)"
        Write-Host "  [2] GamesOnly     - Installed game files & manifests only (no saves/Documents/AppData)"
        Write-Host "  [3] SavesOnly     - Game saves, profiles & configs only (lightweight & fast) [Recommended]"
        Write-Host "  [4] MetadataOnly  - Launcher manifests & configs only"
        $scopeChoice = Read-Host "Enter choice [1-4] (default: 1 - Full)"
        switch ($scopeChoice.Trim()) {
            "1" { $Scope = "Full" }
            "2" { $Scope = "GamesOnly" }
            "3" { $Scope = "SavesOnly" }
            "4" { $Scope = "MetadataOnly" }
            default {
                if ($scopeChoice -in @("Full", "GamesOnly", "SavesOnly", "MetadataOnly")) {
                    $Scope = $scopeChoice
                }
                else {
                    $Scope = "Full"
                }
            }
        }
    }

    if ($Scope -in @("Full", "SavesOnly") -and -not $PSBoundParameters.ContainsKey('UserDataCoverage')) {
        Write-Host ""
        Write-Host "Select Save-Data Coverage:" -ForegroundColor Yellow
        Write-Host "  [1] Targeted      - Game saves & configs only (Documents game folders, Saved Games, game AppData) [Recommended]"
        Write-Host "  [2] Comprehensive - Broad coverage (Entire Documents + all AppData Local/LocalLow/Roaming - can be very large!)"
        $covChoice = Read-Host "Enter choice [1-2] (default: 1 - Targeted)"
        if ($covChoice -eq "2" -or $covChoice -ieq "Comprehensive") {
            $UserDataCoverage = "Comprehensive"
        }
        else {
            $UserDataCoverage = "Targeted"
        }
    }
}
elseif ($Mode -eq "Restore") {
    if ([string]::IsNullOrWhiteSpace($BackupPath)) {
        Write-Host ""
        Write-Host "Enter the path to the backup set folder containing manifest.json" -ForegroundColor Yellow
        $BackupPath = Read-Host "Backup Set Path"
    }
}

Write-Host "Mode: $Mode | Scope: $Scope | User-data coverage: $UserDataCoverage"

if (-not (Test-Administrator)) {
    Write-Warning "PowerShell is not running as Administrator. Backup may work, but Restore into protected folders can fail."
}

Write-Warning "Close Steam, Epic, GOG Galaxy, EA app, Ubisoft Connect, Battle.net, Rockstar Launcher, and running games before continuing."

if ($Mode -eq "Backup") {
    Invoke-Backup
}
elseif ($Mode -eq "Restore") {
    Invoke-Restore
}
