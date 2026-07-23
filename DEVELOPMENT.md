# Development setup

LilithMod builds on Windows against assemblies generated from an installed copy
of the game. These assemblies are local dependencies and must not be committed.

## Requirements

- Windows with PowerShell 5.1 or newer
- .NET 9 SDK
- Python 3.10 or newer
- The game with BepInEx 6 IL2CPP installed

Launch the BepInEx-enabled game once. Confirm that
`BepInEx\interop\Assembly-CSharp.dll` exists before building.

## Local configuration

Set the game folder for the current PowerShell session:

```powershell
$env:LILITH_GAME_DIR = "D:\path\to\The NOexistenceN of Lilith"
```

Keep API keys in the game's `BepInEx\config\LilithMod.cfg`. Do not add keys,
game files, dialogue extracts, voice data, models, caches, logs, or local
runtimes to this repository.

## Build and test

```powershell
dotnet restore LilithMod\LilithMod.csproj
dotnet build LilithMod\LilithMod.csproj -c Release -p:IncludeDialogueCatalog=false
python verify-bilingual.py
dotnet run --project tests\StreamingReplyHarness -c Release
```

Normal builds write to `LilithMod\bin`. To deploy to the installed game, close
the game, build without `-p:IncludeDialogueCatalog=false` (the catalogue is
wanted locally), and write directly to the plugin folder:

```powershell
$pluginOut = "$env:LILITH_GAME_DIR\BepInEx\plugins\LilithMod"
dotnet build LilithMod\LilithMod.csproj -c Release -p:OutputPath="$pluginOut\"
```

`reapply-mod.ps1` is only the backup/restore helper around a game reinstall.

## Package

Download the BepInEx 6 x64 IL2CPP archive and save it locally as
`tools\bepinex785.zip`. The `tools` folder is ignored by Git.

```powershell
python verify-package.py
```

The packaging script excludes the game dialogue catalogue, symbols, configs,
logs, voice models, and extracted assets. Run `python verify-package.py`
to build and validate in one command. Run `runtime\package-mod.ps1` directly
when validation is not needed.

Before publishing, review `git status`, run `git diff --check`, and inspect the
archive contents. Never publish files from `backup`, `dialogue`, `training`,
`voice-data`, `voice-runtime`, or a game installation.
