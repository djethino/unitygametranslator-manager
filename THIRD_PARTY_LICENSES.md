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

## Notes

Games are never modified beyond adding the loader and plugin files, all of which are recorded in
an install receipt so they can be removed exactly. No game asset is ever copied, redistributed or
altered.
