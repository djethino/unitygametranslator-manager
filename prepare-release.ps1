#!/usr/bin/env pwsh
# UnityGameTranslator Installer — release preparation
# Usage: ./prepare-release.ps1 [-Rid win-x64,linux-x64]
#
# Produces, in ./releases/, one archive per system and its .sha256 sidecar.
#
# The sidecar is not decoration: the tool updates itself from these very files, and until we can
# afford a signing certificate the checksum is the only thing standing between a person and a
# swapped binary. GitHub also publishes a sha256 digest per asset through its API, which the tool
# reads — two independent sources for the same fact, neither of which we have to maintain by hand.

param(
    [string[]] $Rid = @('win-x64', 'linux-x64')
)

$ErrorActionPreference = 'Stop'

# Moved for the duration and given back afterwards, whether this ends well or badly. A script that
# leaves the caller somewhere else is a script whose next line fails on a relative path — which is
# exactly how this was noticed.
$callerLocation = Get-Location
Set-Location $PSScriptRoot
trap { Set-Location $callerLocation; break }

# A tar.gz written from Windows, with the execute bit set on the binary.
#
# Windows' own tar cannot set a mode, and a zip has nowhere to put one — so the archive is written
# through the framework's tar writer, which does. Without this the Linux download unpacks into a
# file the desktop will not launch, and the person has no way of knowing that a chmod is all that
# stands between them and a working tool.
function New-TarGz {
    param(
        [Parameter(Mandatory)] [string] $SourceDir,
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [string] $ExecutableName
    )

    Add-Type -AssemblyName System.Formats.Tar -ErrorAction SilentlyContinue

    $executable = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite `
        -bor [System.IO.UnixFileMode]::UserExecute -bor [System.IO.UnixFileMode]::GroupRead `
        -bor [System.IO.UnixFileMode]::GroupExecute -bor [System.IO.UnixFileMode]::OtherRead `
        -bor [System.IO.UnixFileMode]::OtherExecute
    $readable = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite `
        -bor [System.IO.UnixFileMode]::GroupRead -bor [System.IO.UnixFileMode]::OtherRead

    $file = [System.IO.File]::Create($Destination)
    try {
        $gzip = [System.IO.Compression.GZipStream]::new(
            $file, [System.IO.Compression.CompressionLevel]::Optimal, $true)
        try {
            $writer = [System.Formats.Tar.TarWriter]::new(
                $gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $true)
            try {
                foreach ($item in Get-ChildItem -LiteralPath $SourceDir -File | Sort-Object Name) {
                    $entry = [System.Formats.Tar.PaxTarEntry]::new(
                        [System.Formats.Tar.TarEntryType]::RegularFile, $item.Name)
                    $entry.Mode = if ($item.Name -eq $ExecutableName) { $executable } else { $readable }

                    $content = [System.IO.File]::OpenRead($item.FullName)
                    try {
                        $entry.DataStream = $content
                        $writer.WriteEntry($entry)
                    }
                    finally { $content.Dispose() }
                }
            }
            finally { $writer.Dispose() }
        }
        finally { $gzip.Dispose() }
    }
    finally { $file.Dispose() }
}

[xml]$props = Get-Content 'Directory.Build.props'
$Version = ($props.Project.PropertyGroup | Where-Object { $_.Version }).Version
if (-not $Version) { throw 'No <Version> found in Directory.Build.props' }

Write-Host "=== UnityGameTranslator Installer $Version ===" -ForegroundColor Cyan

$project = 'src/UnityGameTranslator.Installer.Gui/UnityGameTranslator.Installer.Gui.csproj'
$releasesDir = 'releases'

# What each system gets: the runtime identifier, the name the executable takes there (the same
# name IPlatform.ExecutableFileName expects, or self-install would look for a file that is not
# there), and how it is packed.
#
# Linux gets a tar.gz rather than a zip for one concrete reason: a zip cannot carry the execute
# bit. Someone unpacking it on a Steam Deck would get a file the desktop refuses to run, with
# nothing on screen saying why. A tar preserves the mode, and Ark and tar both honour it.
$targets = @(
    @{ Rid = 'win-x64';   Executable = 'UnityGameTranslatorInstaller.exe'; Archive = 'zip'    ; Shim = $true }
    @{ Rid = 'linux-x64'; Executable = 'unitygametranslator-installer';    Archive = 'tar.gz' ; Shim = $false }
) | Where-Object { $Rid -contains $_.Rid }

# The Windows command line entry point: one line of batch, not a second program.
#
# The executable is a window program, so no console is ever created when someone opens the tool —
# and the price is that PowerShell does not wait for it, so `tool diagnose > log.txt` typed at a
# PowerShell prompt ends the redirection before a single byte is written. Measured, on this
# machine: an empty file, no error, nothing to tell anyone why.
#
# Going through cmd fixes it, because cmd IS a console program: PowerShell waits for cmd, cmd waits
# for us, and the redirection and the exit code both survive. Verified for `>`, for a pipeline and
# for the exit code. It costs one text file, and it means the command has a name worth typing.
$shimName = 'ugt-installer.cmd'
$shimBody = @'
@echo off
rem The command line face of UnityGameTranslator Installer.
rem The executable itself opens the window when it is run with no command.
"%~dp0UnityGameTranslatorInstaller.exe" %*
'@

if (-not $targets) { throw "No known target among: $($Rid -join ', ')" }

# Shipped beside the binary because the licence requires it, not as a courtesy: this is AGPL-3.0
# software and whoever holds a copy is entitled to those terms and to the notices of what it is
# built on.
$documents = @('LICENSE', 'THIRD_PARTY_LICENSES.md')
foreach ($doc in $documents) {
    if (-not (Test-Path $doc)) { throw "Missing $doc — it must ship with the binary." }
}

if (Test-Path $releasesDir) { Remove-Item -Recurse -Force $releasesDir }
New-Item -ItemType Directory -Path $releasesDir | Out-Null

foreach ($target in $targets) {
    $rid = $target.Rid
    Write-Host "`nPublishing $rid..." -ForegroundColor Yellow

    $stagingDir = Join-Path $releasesDir "staging-$rid"

    dotnet publish $project -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -o $stagingDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    # A single-file publish that quietly leaves a second file behind is the failure this checks
    # for: the point of the format is that a person downloads one thing and runs it, and a stray
    # .pdb or satellite assembly beside it means the archive is no longer that. It has happened
    # here before (DebugType had to move to Directory.Build.props for exactly this reason), so it
    # is verified rather than assumed.
    $produced = Get-ChildItem $stagingDir -File
    $expected = $produced | Where-Object { $_.Name -eq 'UnityGameTranslatorInstaller.exe' -or $_.Name -eq 'UnityGameTranslatorInstaller' }
    if (-not $expected) {
        throw "Published $rid but found no executable in $stagingDir"
    }
    $extra = $produced | Where-Object { $_.FullName -ne $expected.FullName }
    if ($extra) {
        throw ("Publish for $rid is not a single file. Also produced: " + ($extra.Name -join ', '))
    }

    # The Linux binary takes the name a Linux command has. Renaming a published single-file host
    # is safe: everything it needs is inside it.
    if ($expected.Name -ne $target.Executable) {
        Move-Item -LiteralPath $expected.FullName -Destination (Join-Path $stagingDir $target.Executable)
    }

    foreach ($doc in $documents) { Copy-Item $doc $stagingDir }

    if ($target.Shim) {
        # CRLF and no trailing newline surprises: this is a batch file, read by cmd.
        Set-Content -Path (Join-Path $stagingDir $shimName) -Value $shimBody -Encoding ascii
    }

    $base = "UnityGameTranslatorInstaller-v$Version-$rid"
    $archiveName = "$base.$($target.Archive)"
    $archivePath = Join-Path $releasesDir $archiveName

    if ($target.Archive -eq 'zip') {
        Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $archivePath
    }
    else {
        New-TarGz -SourceDir $stagingDir -Destination $archivePath -ExecutableName $target.Executable
    }

    Remove-Item -Recurse -Force $stagingDir

    # sha256sum-compatible: lowercase hash, two spaces, file name, LF. So `sha256sum -c` works as
    # it does for the mod, rather than inventing a format of our own.
    $hash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLower()
    Set-Content -Path "$archivePath.sha256" -Value "$hash  $archiveName`n" -NoNewline -Encoding utf8

    $size = [math]::Round((Get-Item $archivePath).Length / 1MB, 1)
    Write-Host "  Created $archiveName ($size MB, + .sha256)" -ForegroundColor Gray
}

Write-Host "`n=== Release archives ready in ./releases/ ===" -ForegroundColor Green
Get-ChildItem $releasesDir -File | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name)" -ForegroundColor Cyan
}
Write-Host '  (attach the .sha256 files alongside their archive on the GitHub release)' -ForegroundColor DarkGray
Write-Host '  (the tool checks its own updates against these — a release without them cannot be applied)' -ForegroundColor DarkGray

Set-Location $callerLocation
