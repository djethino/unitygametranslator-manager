#!/usr/bin/env pwsh
<#
    Refuse to build code that turns the catalogue's `userdata_dir` into a path on its own.

    `userdata_dir` is fetched — GitHub, then the site mirror, then a cache — so it is a remote
    string, and `Path.Combine(gamePath, "../somewhere")` resolves happily outside the game the
    user chose. The guard (FileOperations.TryResolveInsideGame) existed and was applied at the
    border — install, uninstall, backups — while six classes composed the same path themselves
    and wrote, read and deleted wherever it pointed. Nothing but a rule can keep the next class
    from doing it again: the compiler sees a string and a string.

    So every composition goes through UserDataInventory.DataFolder, and this refuses any other.
    Same shape as the mod's check-il2cpp-safety.ps1, for the same reason: a trap that compiles
    needs a build-time check, not a note somebody reads afterwards.

    Exits 1 on the first violation. Silent and exit 0 when clean.
#>

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"

if (-not (Test-Path $src)) {
    Write-Host "  [path guard] src not found at $src" -ForegroundColor Red
    exit 1
}

$rules = @(
    @{
        Name    = "a path composed from the catalogue's userdata_dir"
        # Both spellings seen in the code: the separator swap, and a Path.Combine naming the field.
        # The word boundary keeps `platform.UserDataDirectory` — the tool's own settings folder,
        # nothing to do with the catalogue — out of it.
        Pattern = 'UserDataDir\s*\.Replace\s*\(|Path\.Combine\s*\([^;]*\.UserDataDir\b'
        # The one legitimate home, where the guard is applied.
        Allowed = @("UserDataInventory.cs")
        Advice  = @(
            "Need the folder      -> UserDataInventory.DataFolder(gamePath, descriptor)  (null = refused, say so)",
            "Need it to exist too -> UserDataInventory.FolderFor(gamePath, descriptor)"
        )
    }
)

foreach ($rule in $rules) {
    $violations = Get-ChildItem -Path $src -Filter *.cs -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } |
        Where-Object { $rule.Allowed -notcontains $_.Name } |
        Select-String -Pattern $rule.Pattern -CaseSensitive |
        Where-Object { $_.Line -notmatch '^\s*(//|///|\*)' }   # a comment naming the trap is not the trap

    if ($violations) {
        Write-Host ""
        Write-Host ("  [path guard] FAILED - {0}, outside {1}" -f $rule.Name, ($rule.Allowed -join ", ")) -ForegroundColor Red
        foreach ($v in $violations) {
            $rel = $v.Path.Substring($root.Length).TrimStart('\', '/')
            Write-Host ("    {0}:{1}" -f $rel, $v.LineNumber) -ForegroundColor Red
            Write-Host ("      {0}" -f $v.Line.Trim()) -ForegroundColor DarkGray
        }
        Write-Host ""
        Write-Host "  A fetched catalogue could point this outside the game folder." -ForegroundColor Yellow
        foreach ($line in $rule.Advice) {
            Write-Host ("  {0}" -f $line) -ForegroundColor Yellow
        }
        Write-Host ""
        exit 1
    }
}

exit 0
