# Contributing to SFHud

Thanks for helping! SFHud is free and open source, with every feature included.

- Build: `dotnet build src/SFHud.App/SFHud.App.csproj` (needs the .NET 10 SDK on Windows). `.\build.ps1` makes a release.
- Test without touching your real data: set `SFHUD_DATA_DIR` to an empty folder before starting the build.
- Keep it offline and injection-free: SFHud never reads game memory or injects into games, and nothing leaves the PC unless the user asks.
- There is no paid edition and no license check. If you'd like to support development: https://ko-fi.com/theslorb
- Licensed under GPL-3.0 (see LICENSE).
