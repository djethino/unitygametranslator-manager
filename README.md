# UnityGameTranslator Manager

Finds the Unity games on your machine, tells you what each one needs, and sets up
[UnityGameTranslator](https://github.com/djethino/unitygametranslator) on it — mod loader
included — without touching anything that was already there.

> **Status: beta**, like the rest of the project. It finds games, installs and removes the mod
> and its loader, writes your settings into each game and fetches community translations — all of
> it against real games, not fixtures. What it has not had is wear: this is its first public
> build, nothing is signed yet, and nobody outside the project has walked the whole interface.

## Why

Installing the mod by hand means knowing whether your game is Mono or IL2CPP, picking the right
mod loader, choosing one of five builds, and extracting it into the right folder. That is a fair
amount to ask of someone who just wants to play in their own language.

This tool answers those questions for you, and it can also tell you something no other installer
can: **whether the community has already translated this game**.

## What it does

- Finds Unity games from Steam, Epic, GOG, and plain folders
- Identifies the scripting backend (Mono / IL2CPP), the Unity version and the architecture
- Detects a mod loader that is already installed — and never replaces it
- Warns you, and refuses, when a game ships an anti-cheat
- Shows the community translations available for that game, and installs the one you pick
- Says where you stand on a translation you took part in: the one you publish, or a branch of it
- Writes your settings into each game's own configuration, so the mod asks nothing on first run
- Installs the loader and the matching plugin build, keeping your settings and translations
- Uninstalls exactly what it installed, and nothing else

## What it does not do

- Manage other people's mods — this is not a mod manager
- Host anything: every download comes from the official source, checksum verified
- Collect anything: no telemetry, no analytics, no identifier of any kind sent anywhere. If
  something goes wrong, `diagnose` prints a report *you* choose to share
- Require an account: finding, downloading and installing translations is anonymous. Signing in
  exists, and is only ever needed to publish your own — the tool is complete without it

## Install

Download the archive for your system from the
[releases page](https://github.com/djethino/unitygametranslator-manager/releases), unzip it
anywhere, and run `UnityGameTranslatorManager.exe`. There is nothing to install: it is a single
executable, and it offers to install itself properly if you want to keep it around.

**Windows only for now.** Linux is written for but has never been run, so no build is published —
see [Supported systems](#supported-systems).

> **Windows SmartScreen will warn you.** This build is not signed with a paid certificate, and an
> unknown executable with no reputation is exactly what SmartScreen exists to flag. Check the
> download against its published `.sha256` — every release carries one beside the archive — and
> the whole source is here.

Every release is a **pre-release** for now: you get it by choosing it, not by being notified.

## Command line

The same engine, without the interface. Useful for support and for scripting.

It is the same program: given a command it answers on the console instead of opening the window.
There is nothing extra to download, and nothing to keep in step.

```
ugt-manager scan [--all]        List the Unity games found on this machine
ugt-manager report <game>       Everything known about one game
ugt-manager catalog             Show the loader catalog and where it came from
ugt-manager diagnose            Printable report, safe to paste into an issue
ugt-manager self-update         Update this tool itself
```

On Windows `ugt-manager.cmd` sits beside the executable and is the way to run commands: the
executable is a window program, so that opening the tool never puts a console on your screen, and
going through the small batch file is what keeps `> log.txt` and exit codes working from a
PowerShell prompt. On Linux there is no such thing — run `unitygametranslator-manager` directly.

Add `--offline` to any command to skip every network call. Run it with no command, or drop a game
folder onto it, and you get the window.

## Supported systems

| System | Status |
|---|---|
| Windows | Supported — this is what the published build is |
| Linux / SteamOS (Steam Deck) | **Written for, never run.** No build is published: the code handles Proton and the Deck, but some of its paths are known to be wrong and nobody has started it once. A later release |
| macOS | Not yet — see below |

⚠ The Linux line used to read "Supported". It was not: it described what the code was written to
do, which is not the same claim, and somebody on a Deck would have found that out the hard way.

### macOS

Nothing about macOS stands in the way: a game there is Mono or IL2CPP like anywhere else, and a
Windows game running through a translation layer is the same case as Proton on Linux, which the
code already handles.

What is missing is on our side — this tool does not yet know where a Mac keeps its games, nor how
a Unity game is laid out there. It is work, not a wall. If you want it, open an issue: knowing
somebody is waiting is what moves it up.

## The loader catalog

Nothing about a mod loader's on-disk layout is hardcoded. It all lives in
[`loaders.json`](https://github.com/djethino/unitygametranslator-catalogs/blob/main/loaders.json),
a public data repository the tool fetches at runtime.

That is deliberate: BepInEx and MelonLoader have both changed their layout more than once, and a
tool that cannot update itself would break for everyone on the same day. This way, a layout
change is fixed by editing one file — every installed copy picks it up.

The catalog is read from GitHub first, our website as a mirror, then a local cache, then the copy
built into the binary. GitHub comes first on purpose: serving it ourselves would put an IP in our
logs on every launch, and we have no reason to hold that.

## Building

Requires the .NET 8 SDK.

```bash
dotnet build -c Release
dotnet run --project src/UnityGameTranslator.Manager.Gui -- scan
```

`Gui` is the executable and `Cli` is a library it links in — one binary, two faces.

## Project layout

```
src/
├── UnityGameTranslator.Manager.Core/   Everything: detection, catalog, install, receipts
│   ├── Platform/                         The only OS-specific code (IPlatform + adapters)
│   ├── Detection/                        Games, runtimes, Unity versions, loaders, anti-cheat
│   ├── Catalog/                          Loader catalog, fetched and cached
│   ├── Api/                              Read-only, anonymous calls to the community site
│   └── Model/                            Shared types
├── UnityGameTranslator.Manager.Cli/    Command line face (a library, not a program)
└── UnityGameTranslator.Manager.Gui/    The executable: window, and the entry point of both
```

Same shape as the mod: one shared trunk holding all the logic, thin adapters for what genuinely
differs. The command line is not a lesser version of the interface — it reaches the same engine.

## Acknowledgments

- **[Avalonia](https://github.com/AvaloniaUI/Avalonia)** — cross-platform .NET UI framework
- **[Inter](https://github.com/rsms/inter)** by Rasmus Andersson — the font the window renders with
- **[BepInEx](https://github.com/BepInEx/BepInEx)** and **[MelonLoader](https://github.com/LavaGang/MelonLoader)** by LavaGang — the mod loaders this tool installs, downloaded from their own release pages and never redistributed here

See [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) for full license details.

## License

AGPL-3.0. See [LICENSE](LICENSE) and [LICENSING.md](LICENSING.md).

Third-party components are listed in [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md). This
tool downloads BepInEx and MelonLoader from their official releases; it does not redistribute
them.
