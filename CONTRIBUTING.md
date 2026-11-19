# Contributing to SFHud

Thanks for helping. This repository is **SFHud Free**, the open-source edition of SFHud.

## Getting started

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.
2. Build with `dotnet build src/SFHud.App/SFHud.App.csproj`, or make a full release with `.\build.ps1`. See [docs/BUILDING.md](docs/BUILDING.md).
3. Test without touching your real data by setting `SFHUD_DATA_DIR` to an empty folder before you start the build.

## Ground rules

- **No injection and no memory reading.** SFHud must stay a separate overlay window that is safe to run next to anti-cheat.
- **Local first.** Nothing leaves the PC unless the user asks for it.
- **Keep the edition gates.** Pro features are switched off through `Edition.Has(...)` in `src/SFHud.App/Services/Edition.cs`. Pro sales pay for development, so please don't send pull requests that remove those checks.
- Match the existing code style: C# with nullable reference types, and small focused services.

## Pull requests

- Keep each PR to one change and explain what it fixes or adds.
- Add screenshots for UI changes.
- Bug reports help most when they include your Windows version, game, resolution and display mode, plus `%LOCALAPPDATA%\SFHud\logs\`.

By contributing, you agree that your work is licensed under the [GPL-3.0](LICENSE).
