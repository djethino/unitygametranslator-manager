# UnityGameTranslator Installer

Finds the Unity games on your machine, tells you what each one needs, and sets up
[UnityGameTranslator](https://github.com/djethino/unitygametranslator) on it — mod loader
included — without touching anything that was already there.

> **Status: beta**, like the rest of the project. It finds games, installs and removes the mod
> and its loader, writes your settings into each game and fetches community translations — all of
> it against real games, not fixtures. What it has not had is a public release: nothing is signed
> yet, and nobody outside the project has walked the whole interface.

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
- Collect anything: no telemetry, no accounts, no identifiers. If something goes wrong,
  `diagnose` prints a report *you* choose to share

## Install

**There is no download yet.** No release has been published, so the only way to run it today is to
build it from source — see [Building](#building) below. This section will say otherwise the day
that changes, rather than promising a file that is not there.

When builds are published, it will be a single executable: nothing to install, and it offers to
install itself properly if you want to keep it around.

> **Windows SmartScreen will warn you** about that first build: it will not be signed with a paid
> certificate, and an unknown executable with no reputation is exactly what SmartScreen exists to
> flag. You will be able to check the download against its published `.sha256`, and the whole
> source is here.

## Command line

The same engine, without the interface. Useful for support and for scripting.

It is the same file: run the executable with a command and it answers on the console instead of
opening the window. There is nothing extra to download, and nothing to keep in step.

```
UnityGameTranslatorInstaller scan [--all]   List the Unity games found on this machine
UnityGameTranslatorInstaller report <game>  Everything known about one game
UnityGameTranslatorInstaller catalog        Show the loader catalog and where it came from
UnityGameTranslatorInstaller diagnose       Printable report, safe to paste into an issue
```

On Linux the file is named `unitygametranslator-installer`.

Add `--offline` to any command to skip every network call. Run it with no command, or drop a game
folder onto it, and you get the window.

## Supported systems

| System | Status |
|---|---|
| Windows | Supported |
| Linux / SteamOS (Steam Deck) | Supported, including games running through Proton |
| macOS | Not yet, and for reasons of ours rather than of the loaders — see below |

### Why not macOS

Not because of what can be modded there. That line used to say only Mono games could ever work,
which conflated two different things and was never checked.

A native macOS game being Mono or IL2CPP only decides which loader could suit it — and if none
does, the catalog offers none and the game takes the ordinary "no loader fits here" path, with the
reason, exactly as on the other systems. A Windows game running through a translation layer is a
Windows game with Windows loaders, which is the same case as Proton on Linux, already supported.

What actually stops us is our own code. There is no macOS adapter: nothing knows where Steam keeps
its library there, or where this tool should keep its settings. More basic still, a Unity game is
laid out differently — its data sits in `Game.app/Contents/Resources/Data`, while every probe here
looks for a `*_Data` folder beside the executable. Our detection would find nothing at all,
whatever the engine.

Those are jobs, not obstacles. They are simply not done.

## The loader catalog

Nothing about a mod loader's on-disk layout is hardcoded. It all lives in
[`catalog/loaders.json`](catalog/loaders.json), which the tool fetches at runtime.

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
dotnet run --project src/UnityGameTranslator.Installer.Gui -- scan
```

Both faces live in the same project because they live in the same file: `Gui` is the executable,
`Cli` is a library it links in.

## Project layout

```
src/
├── UnityGameTranslator.Installer.Core/   Everything: detection, catalog, install, receipts
│   ├── Platform/                         The only OS-specific code (IPlatform + adapters)
│   ├── Detection/                        Games, runtimes, Unity versions, loaders, anti-cheat
│   ├── Catalog/                          Loader catalog, fetched and cached
│   ├── Api/                              Read-only, anonymous calls to the community site
│   └── Model/                            Shared types
├── UnityGameTranslator.Installer.Cli/    Command line face (a library, not a program)
└── UnityGameTranslator.Installer.Gui/    The executable: window, and the entry point of both
```

Same shape as the mod: one shared trunk holding all the logic, thin adapters for what genuinely
differs. The command line is not a lesser version of the interface — it is how the logic gets
tested against real game folders.

## License

AGPL-3.0. See [LICENSE](LICENSE) and [LICENSING.md](LICENSING.md).

Third-party components are listed in [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md). This
tool downloads BepInEx and MelonLoader from their official releases; it does not redistribute
them.
