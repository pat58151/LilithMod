# LilithMod source

Private development repository for
[LilithMod](https://github.com/pat58151/LilithMod), a companion mod for
*The NOexistenceN of Lilith*.

The public repository contains releases and user documentation. This repository
contains the C# source, installer, local service setup, tests, and packaging
tools.

## Current target

| Component | Version |
|---|---|
| LilithMod | 1.0.0 |
| Game | 1.0.1 |
| Runtime | BepInEx 6 IL2CPP |
| Target framework | .NET Standard 2.1 |

## Repository layout

| Path | Contents |
|---|---|
| `LilithMod/` | Mod source and project file |
| `installer/` | Windows installer source |
| `runtime/` | Runtime and release packaging scripts |
| `tests/` | Streaming reply test harness |
| `image/` | Public documentation images |
| `SETUP.md` | End-user setup instructions |
| `TECHNIQUES.md` | Design and implementation notes |
| `verify-bilingual.py` | Localization and source validation |
| `verify-package.py` | Release package build and validation |

## Requirements

- Windows with PowerShell 5.1 or newer
- .NET 8 SDK
- Python 3.10 or newer
- An installed copy of the game
- BepInEx 6 IL2CPP installed in the game directory

Launch the modded game once before building. Confirm that
`BepInEx\interop\Assembly-CSharp.dll` exists.

## Local configuration

Set the game directory for the current PowerShell session:

```powershell
$env:LILITH_GAME_DIR = "D:\path\to\The NOexistenceN of Lilith"
```

Keep API keys in the game's `BepInEx\config\LilithMod.cfg` file.

Do not commit game files, dialogue extracts, voice data, models, caches, logs,
local runtimes, or API keys.

## Build and test

Run the standard validation sequence from the repository root:

```powershell
dotnet restore LilithMod\LilithMod.csproj
dotnet build LilithMod\LilithMod.csproj -c Release -p:IncludeDialogueCatalog=false
python verify-bilingual.py
dotnet run --project tests\StreamingReplyHarness -c Release
```

Normal builds write to `LilithMod\bin`.

To deploy a local build, close the game and run:

```powershell
dotnet build LilithMod\LilithMod.csproj -c Release
Copy-Item LilithMod\bin\Release\*.dll `
  "$env:LILITH_GAME_DIR\BepInEx\plugins\LilithMod" -Force
```

The local deploy build includes the dialogue catalogue. Public builds must not
include it.

## Package a release

Download the BepInEx 6 x64 IL2CPP archive to `tools\bepinex785.zip`. The
`tools` directory is ignored by Git.

Then run:

```powershell
python verify-package.py
```

This builds the package and checks that it excludes dialogue catalogues,
symbols, configuration files, logs, models, and extracted assets.

Before publishing:

1. Confirm the mod and supported game versions.
2. Run the build and test sequence.
3. Run `python verify-package.py`.
4. Review `git status` and `git diff --check`.
5. Inspect the generated archive.
6. Update the public repository documentation and release notes.

Never publish content from `backup`, `dialogue`, `training`, `voice-data`,
`voice-runtime`, or a game installation.

## Documentation

- [Detailed development notes](DEVELOPMENT.md)
- [User setup](SETUP.md)
- [Design techniques](TECHNIQUES.md)
- [Disclaimer](DISCLAIMER.md)
