# Contributing to UnityGameTranslator Manager

Thank you for your interest in contributing!

## Contributor License Agreement (CLA)

By submitting a pull request or contribution, you agree that:

1. **You own the rights** to the code you are contributing, or have permission to contribute it.

2. **You grant us a perpetual, worldwide, non-exclusive, royalty-free license** to use, modify, and
   distribute your contribution under the AGPL-3.0 license, and under any other licence this
   project may later be released under.

3. **You understand** that your contribution becomes part of a project released under AGPL-3.0.

The second point is what lets the project change licence later without having to track down every
past contributor — a practical necessity, not a plan.

## How to Contribute

### Reporting Bugs

The most useful bug report for this tool is the output of:

```
ugt-manager diagnose
```

(the same program you double-click; given a command it answers on the console. `ugt-manager.cmd`
sits beside the executable on Windows; on Linux run `unitygametranslator-manager` directly.)

It lists what was detected and what was expected, with your user name, home directory and game
library stripped out. Nothing is sent anywhere — you paste it yourself, or not.

Please also include:
- What you expected to happen and what happened instead
- The game and its store (Steam, Epic, GOG, standalone)
- Your operating system, and whether the game runs through Proton

### A loader changed its layout

If a mod loader is no longer detected, or is installed in the wrong place, the fix is usually
[`catalog/loaders.json`](catalog/loaders.json) rather than code. Say which loader and which
version, and include the `diagnose` output — the catalog can be corrected for everyone without a
new release.

### Submitting Code

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Make your changes
4. Test against real games — `scan` and `report` are there for that
5. Commit with clear messages
6. Push and open a Pull Request

### Code Style

- **C#:** follow existing patterns, use meaningful names
- **No dead code:** remove unused imports and functions
- **Nothing OS-specific outside `Platform/`:** if it can be written once for every system, it
  belongs in `Core`
- **Nothing about loader layouts in code:** it belongs in the catalog
- **Never guess:** a field that could not be established stays `Unknown`. A wrong runtime means
  the wrong loader, and the wrong loader means a game that will not start

## Questions?

Open an issue.
