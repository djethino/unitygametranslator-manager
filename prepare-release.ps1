#!/usr/bin/env pwsh
# UnityGameTranslator Manager — release preparation
# Usage: ./prepare-release.ps1 [-Rid win-x64,linux-x64]
#
# Produces, in ./releases/, one archive per system and its .sha256 sidecar — plus, for Linux, an
# AppImage and its own sidecar (built in a Docker container: see New-AppImage).
#
# The sidecar is not decoration: the tool updates itself from these very files, and until we can
# afford a signing certificate the checksum is the only thing standing between a person and a
# swapped binary. GitHub also publishes a sha256 digest per asset through its API, which the tool
# reads — two independent sources for the same fact, neither of which we have to maintain by hand.

param(
    [string[]] $Rid = @('win-x64', 'linux-x64'),

    # 🔴 **Point this build at a development site instead of production.**
    #
    # Never the default. The three addresses are compiled in (BuildInfo.g.cs), so a package made
    # while they point somewhere local reaches nothing on anybody else's machine — and says so only
    # in a log. Passed as MSBuild properties, so Directory.Build.props is never edited and the next
    # ordinary run is production again with nothing to remember.
    #
    # ⚠ The archive is then named "-local", because the one thing this must not allow is publishing
    # such a build by mistake. Without the switch, the addresses are CHECKED — see below.
    [string] $LocalSite
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

    # ⚠ .NET resolves a relative path against the PROCESS directory, not PowerShell's location:
    # run from a shell that had been elsewhere, the archive was written into that other folder
    # and the checksum below found nothing. Resolved the way PowerShell resolves it.
    $Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
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

# An AppImage of the Linux build: one file a person downloads, marks executable (most file managers
# offer it on double-click) and runs, on any distribution — SteamOS and Bazzite included, where
# /usr is read-only and a package manager is not the way people install things.
#
# ⚠ **The tar.gz stays.** It is what the installed tool updates itself from (SelfUpdater's
# AssetNameFor), and an AppImage cannot replace itself: its binary lives in an image mounted
# read-only for the session. Opened from an AppImage, the tool offers to install itself — the same
# binary as in the tar.gz, taken from inside the image — and that copy updates (SelfUpdater's
# RunningAppImage).
#
# The tools are pinned by version AND checksum, downloaded once into .build-tools/, and run in a
# container: appimagetool is a Linux program, and the execute bits an AppDir needs cannot be set
# on a Windows file system. The static type2 runtime needs no libfuse2 on the machine that runs the
# AppImage — the reason the old runtime failed to start on recent distributions.
$appImageTools = @(
    @{ Name = 'appimagetool-x86_64.AppImage'
       Url = 'https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage'
       Sha256 = 'ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0' }
    @{ Name = 'runtime-x86_64'
       Url = 'https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64'
       Sha256 = '2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d' }
)
$appImageContainer = 'debian:bookworm-slim'

function Get-AppImageTools {
    $folder = Join-Path $PSScriptRoot '.build-tools'
    New-Item -ItemType Directory -Force -Path $folder | Out-Null

    foreach ($tool in $appImageTools) {
        $path = Join-Path $folder $tool.Name
        if (-not (Test-Path $path)) {
            Write-Host "  downloading $($tool.Name)" -ForegroundColor DarkGray
            Invoke-WebRequest -Uri $tool.Url -OutFile $path
        }

        # Checked every time, not only after a download: the cache is a folder anybody can write.
        $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $tool.Sha256) {
            Remove-Item -Force $path
            throw "$($tool.Name) does not match its pinned checksum (got $actual). Deleted; run again to download it anew."
        }
    }

    return $folder
}

function New-AppImage {
    param(
        [Parameter(Mandatory)] [string] $StagingDir,
        [Parameter(Mandatory)] [string] $ExecutableName,
        [Parameter(Mandatory)] [string] $Destination
    )

    docker info --format '{{.ServerVersion}}' *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker is not running: the AppImage is built in a Linux container. Start Docker Desktop, or pass -Rid win-x64 to skip Linux.'
    }

    $tools = Get-AppImageTools

    # The AppDir. The binary and what ships beside it go in usr/bin TOGETHER, exactly as in the
    # tar.gz: installing from an AppImage copies the executable and its named companions
    # (SelfInstaller.Companions), so the licences must sit next to it.
    $appDir = Join-Path ([System.IO.Path]::GetTempPath()) "ugt-appdir-$([guid]::NewGuid().ToString('N'))"
    $bin = Join-Path $appDir 'usr/bin'
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    Get-ChildItem -LiteralPath $StagingDir -File | Copy-Item -Destination $bin

    $iconSource = Join-Path $PSScriptRoot 'src/UnityGameTranslator.Manager.Core/Platform/PackIcon/unitygametranslator-manager-128.png'
    Copy-Item $iconSource (Join-Path $appDir 'unitygametranslator-manager.png')

    # LF only: these are read by a shell and by the desktop, not by Notepad.
    $appRun = "#!/bin/sh`nHERE=`"`$(dirname `"`$(readlink -f `"`$0`")`")`"`nexec `"`$HERE/usr/bin/$ExecutableName`" `"`$@`"`n"
    [System.IO.File]::WriteAllText((Join-Path $appDir 'AppRun'), $appRun)

    # %f and the MimeType: an AppImage integrated by the desktop (Gear Lever, AppImageLauncher)
    # then opens a .ugtpack too. The installed copy declares the type itself (PackFileType).
    $desktop = @(
        '[Desktop Entry]'
        'Type=Application'
        'Name=UnityGameTranslator Manager'
        'Comment=Set up UnityGameTranslator in your Unity games'
        "Exec=$ExecutableName %f"
        'Icon=unitygametranslator-manager'
        'Terminal=false'
        'Categories=Game;'
        'MimeType=application/x-ugtpack;'
        ''
    ) -join "`n"
    [System.IO.File]::WriteAllText((Join-Path $appDir 'unitygametranslator-manager.desktop'), $desktop)

    $outDir = Split-Path -Parent $Destination
    $outName = Split-Path -Leaf $Destination

    # Copied into the container's own file system first: the modes set there are real, where the
    # same chmod on the mounted Windows folder would be ignored.
    $script = @(
        'set -e'
        # appimagetool refuses to run without the file command, which the slim image lacks.
        'apt-get update -qq'
        'apt-get install -y -qq --no-install-recommends file > /dev/null'
        'cp -r /appdir /tmp/AppDir'
        # The mounted Windows folder reports every file as executable: modes set from scratch,
        # so the licences do not arrive executable in an installed copy.
        'find /tmp/AppDir -type d -exec chmod 755 {} + && find /tmp/AppDir -type f -exec chmod 644 {} +'
        "chmod 755 /tmp/AppDir/AppRun /tmp/AppDir/usr/bin/$ExecutableName"
        'cp /tools/appimagetool-x86_64.AppImage /tmp/appimagetool && chmod 755 /tmp/appimagetool'
        'cd /tmp'
        "ARCH=x86_64 ./appimagetool --appimage-extract-and-run --no-appstream --runtime-file /tools/runtime-x86_64 /tmp/AppDir /out/$outName"
    ) -join ' && '

    try {
        docker run --rm `
            -v "${appDir}:/appdir:ro" `
            -v "${tools}:/tools:ro" `
            -v "$((Resolve-Path $outDir).Path):/out" `
            $appImageContainer sh -c $script 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "appimagetool failed (exit $LASTEXITCODE)" }
    }
    finally {
        Remove-Item -Recurse -Force $appDir -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path $Destination)) { throw "appimagetool reported success but $outName is not there" }
}

[xml]$props = Get-Content 'Directory.Build.props'
$Version = ($props.Project.PropertyGroup | Where-Object { $_.Version }).Version
if (-not $Version) { throw 'No <Version> found in Directory.Build.props' }

Write-Host "=== UnityGameTranslator Manager $Version ===" -ForegroundColor Cyan

# What this build will talk to, decided once and reported.
#
# 🔴 **Without -LocalSite, the addresses are CHECKED rather than trusted.** They are compiled into
# the binary, so a package made while they point at a development site reaches nothing on anybody
# else's machine — and pointing them there is a normal thing to do while testing. Putting the file
# back afterwards is a thing to remember, which is why it is verified instead.
#
# ⚠ It refuses DEVELOPMENT addresses, not "addresses that are not ours": self-hosting is supported,
# and somebody's own domain is none of this script's business.
$urlArgs = @()
$localSuffix = ''

if ($LocalSite) {
    $site = $LocalSite.TrimEnd('/')
    $urlArgs = @(
        "-p:ApiBaseUrl=$site/api/v1",
        "-p:WebsiteBaseUrl=$site",
        "-p:SseBaseUrl=http://127.0.0.1:3000"
    )
    # ⚠ In the archive name, so such a build cannot be published by mistake. It is the one failure
    # a check inside the script cannot catch: by then the file is just a file on disk.
    $localSuffix = '-local'
    Write-Host "  [LOCAL] this build will talk to $site" -ForegroundColor Magenta
    Write-Host "  [LOCAL] archives are named -local and must never be released" -ForegroundColor Magenta
}
else {
    foreach ($urlName in @('ApiBaseUrl', 'WebsiteBaseUrl', 'SseBaseUrl')) {
        $value = ($props.Project.PropertyGroup | Where-Object { $_.$urlName }).$urlName

        $uri = $null
        if (-not [Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri)) {
            throw "$urlName is not an absolute URL: $value"
        }

        # ⚠ NOT $host: PowerShell reserves that name for the shell, and assigning to it throws a
        # read-only error that reads as a script bug rather than the check doing its job.
        $urlHost = $uri.Host
        $isLocal = $uri.IsLoopback `
            -or $urlHost -eq 'localhost' `
            -or $urlHost -like '*.test' `
            -or $urlHost -like '*.local' `
            -or $urlHost -like '*.localhost' `
            -or $uri.Scheme -ne 'https'

        if ($isLocal) {
            throw ("$urlName points at a development address: $value`n" +
                   "Put Directory.Build.props back, or pass -LocalSite to build against it on purpose.")
        }
    }
}

$project = 'src/UnityGameTranslator.Manager.Gui/UnityGameTranslator.Manager.Gui.csproj'
$releasesDir = 'releases'

# What each system gets: the runtime identifier, the name the executable takes there (the same
# name IPlatform.ExecutableFileName expects, or self-install would look for a file that is not
# there), and how it is packed.
#
# Linux gets a tar.gz rather than a zip for one concrete reason: a zip cannot carry the execute
# bit. Someone unpacking it on a Steam Deck would get a file the desktop refuses to run, with
# nothing on screen saying why. A tar preserves the mode, and Ark and tar both honour it.
$targets = @(
    @{ Rid = 'win-x64';   Executable = 'UnityGameTranslatorManager.exe'; Archive = 'zip'    ; Shim = $true ; AppImage = $false }
    @{ Rid = 'linux-x64'; Executable = 'unitygametranslator-manager';    Archive = 'tar.gz' ; Shim = $false; AppImage = $true }
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
$shimName = 'ugt-manager.cmd'
$shimBody = @'
@echo off
rem The command line face of UnityGameTranslator Manager.
rem The executable itself opens the window when it is run with no command.
"%~dp0UnityGameTranslatorManager.exe" %*
'@

if (-not $targets) { throw "No known target among: $($Rid -join ', ')" }

# Shipped beside the binary because the licence requires it, not as a courtesy: this is AGPL-3.0
# software and whoever holds a copy is entitled to those terms and to the notices of what it is
# built on.
$documents = @('LICENSE', 'THIRD_PARTY_LICENSES.md')
foreach ($doc in $documents) {
    if (-not (Test-Path $doc)) { throw "Missing $doc — it must ship with the binary." }
}

# 🔴 **The archives, not the folder.** Deleting releases/ wholesale took anything else living
# there with it — including the unpacked copy a developer had open and was testing against, which
# a build for a completely different runtime identifier would silently destroy. The symptom was a
# Linux-only pass failing on "Access to the path …\win-x64\UnityGameTranslatorManager.exe is
# denied", naming a file that had nothing to do with the command that was run.
#
# ⚠ Every version, not only this one: a stale archive left behind would appear in the "ready to
# attach" list below and get published beside the real one.
#
# Staging folders are removed by the loop that creates them; anything else here belongs to
# whoever put it there.
if (Test-Path $releasesDir) {
    Get-ChildItem $releasesDir -File |
        Where-Object { $_.Name -like 'UnityGameTranslatorManager-*' } |
        Remove-Item -Force
}
else {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}

# A remote string must not become a path outside the game. The compiler cannot see it; this can.
& "$PSScriptRoot/check-path-guard.ps1"
if ($LASTEXITCODE -ne 0) { throw "check-path-guard.ps1 refused the build" }

foreach ($target in $targets) {
    $rid = $target.Rid
    Write-Host "`nPublishing $rid..." -ForegroundColor Yellow

    $stagingDir = Join-Path $releasesDir "staging-$rid"

    # Left behind by a run that stopped halfway, it would fail the single-file check below with
    # files this publish never made.
    if (Test-Path $stagingDir) { Remove-Item -Recurse -Force $stagingDir }

    dotnet publish $project -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -o $stagingDir --nologo -v q @urlArgs
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    # A single-file publish that quietly leaves a second file behind is the failure this checks
    # for: the point of the format is that a person downloads one thing and runs it, and a stray
    # .pdb or satellite assembly beside it means the archive is no longer that. It has happened
    # here before (DebugType had to move to Directory.Build.props for exactly this reason), so it
    # is verified rather than assumed.
    $produced = Get-ChildItem $stagingDir -File
    $expected = $produced | Where-Object { $_.Name -eq 'UnityGameTranslatorManager.exe' -or $_.Name -eq 'UnityGameTranslatorManager' }
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

    # ⚠ $localSuffix is empty for an ordinary build and "-local" for one pointed at a development
    # site. In the FILENAME, because that is the only place a mistake can still be caught: once the
    # archive exists, whoever uploads it sees the name and nothing else.
    $base = "UnityGameTranslatorManager-v$Version-$rid$localSuffix"
    $archiveName = "$base.$($target.Archive)"
    $archivePath = Join-Path $releasesDir $archiveName

    if ($target.Archive -eq 'zip') {
        Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $archivePath
    }
    else {
        New-TarGz -SourceDir $stagingDir -Destination $archivePath -ExecutableName $target.Executable
    }

    $made = @($archivePath)

    if ($target.AppImage) {
        $appImagePath = Join-Path $releasesDir "$base.AppImage"
        New-AppImage -StagingDir $stagingDir -ExecutableName $target.Executable -Destination $appImagePath
        $made += $appImagePath
    }

    Remove-Item -Recurse -Force $stagingDir

    # sha256sum-compatible: lowercase hash, two spaces, file name, LF. So `sha256sum -c` works as
    # it does for the mod, rather than inventing a format of our own.
    foreach ($file in $made) {
        $name = Split-Path -Leaf $file
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
        Set-Content -Path "$file.sha256" -Value "$hash  $name`n" -NoNewline -Encoding utf8

        $size = [math]::Round((Get-Item $file).Length / 1MB, 1)
        Write-Host "  Created $name ($size MB, + .sha256)" -ForegroundColor Gray
    }
}

Write-Host "`n=== Release archives ready in ./releases/ ===" -ForegroundColor Green
Get-ChildItem $releasesDir -File | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name)" -ForegroundColor Cyan
}
Write-Host '  (attach the .sha256 files alongside their archive on the GitHub release)' -ForegroundColor DarkGray
Write-Host '  (the tool checks its own updates against these — a release without them cannot be applied)' -ForegroundColor DarkGray

Set-Location $callerLocation
