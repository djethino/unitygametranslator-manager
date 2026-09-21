# Third-Party Licenses

This document lists the third-party components used by UnityGameTranslator Manager.

## Bundled in the executable

### .NET Runtime

- **Source:** https://github.com/dotnet/runtime
- **License:** MIT
- **Copyright:** .NET Foundation and Contributors

Shipped inside the executable so the tool runs without asking the user to install anything.

### Microsoft.Win32.Registry

- **Source:** https://github.com/dotnet/runtime
- **License:** MIT
- **Copyright:** .NET Foundation and Contributors

Used on Windows to locate Steam and GOG installations.

### System.Security.Cryptography.Pkcs

- **Source:** https://github.com/dotnet/runtime
- **License:** MIT
- **Copyright:** .NET Foundation and Contributors

Reads the Authenticode signature of Unity's engine modules, so that none is copied into a game
unless Unity signed it.

### Avalonia

- **Source:** https://github.com/AvaloniaUI/Avalonia
- **License:** MIT
- **Copyright:** The AvaloniaUI Project

The interface framework, with its Fluent theme. Shipped inside the executable.

### Inter

- **Source:** https://github.com/rsms/inter
- **License:** SIL Open Font License 1.1
- **Copyright:** Rasmus Andersson — "Inter" is a Reserved Font Name

The font the window renders with, shipped inside the executable through the
`Avalonia.Fonts.Inter` package (MIT). It travels with the tool so the interface reads the same on
every machine, and so it reads at all on a system carrying no suitable font of its own. It covers
Latin, Greek and Cyrillic; anything outside that is drawn by the system's own fonts.

## Downloaded, never redistributed

The tool downloads these from their official release pages, at the user's request, and verifies
the archive against a published checksum. **No copy of them is hosted or bundled here.**

### BepInEx

- **Source:** https://github.com/BepInEx/BepInEx
- **License:** LGPL-2.1
- **Copyright:** BepInEx contributors

### MelonLoader

- **Source:** https://github.com/LavaGang/MelonLoader
- **License:** Apache-2.0
- **Copyright:** Lava Gang and contributors

### Ollama

- **Source:** https://github.com/ollama/ollama
- **License:** MIT
- **Copyright:** Ollama contributors

Offered as an optional local translation backend. **The models it downloads are not covered by
Ollama's licence** — each carries its own terms (Llama Community Licence, Gemma Terms of Use,
Apache-2.0 for others). The user chooses and downloads them; no model weights are hosted or
mirrored here.

### UnityGameTranslator (the mod)

- **Source:** https://github.com/djethino/unitygametranslator
- **License:** AGPL-3.0

### Unity's .NET class libraries (Mono)

- **Source:** https://unity.bepinex.dev/corlibs/ — BepInEx's archive of the class libraries each
  Unity version ships, extracted from Unity's own editor builds
- **License:** MIT (Unity's fork of Mono: https://github.com/Unity-Technologies/mono)
- **Copyright:** Mono contributors, Unity Technologies

Downloaded only for a game that shipped without some of them, and only the files that game lacks.

## Copied or downloaded from Unity, at the user's request

### Unity engine modules (`UnityEngine.dll`, `UnityEngine.*Module.dll`)

- **Owner:** Unity Technologies — proprietary, covered by Unity's terms of service
- **Never hosted, mirrored or redistributed by this project.**

Needed only by a game whose build stripped its own engine modules. The tool either copies them
from another Unity game or Unity editor already installed on the same computer, or downloads
them from Unity's own servers (`download.unity3d.com`) after saying so and showing Unity's terms.
A module copied from this computer is used only when its Authenticode signature by Unity
Technologies verifies. A module downloaded from Unity's server may carry none, since Unity signs
neither its Linux builds nor versions before 2020; one whose signature is present and fails is
refused whatever its origin.

## Notes

Games are never modified beyond adding the loader and plugin files, and — for a game that lacks
them — the libraries and engine modules above, all of which are recorded in an install receipt so
they can be removed exactly. They go in a folder of their own; none of the game's own files is
replaced or altered.
