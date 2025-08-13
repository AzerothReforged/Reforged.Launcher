# Azeroth Reforged Launcher (WPF, .NET 8)

Reminiscent-of-WotLK launcher for a 3.3.5a private server. Updates files from a CDN manifest, writes `realmlist.wtf`, shows news from RSS, and launches the client.

## Build (Visual Studio)

1. Install **Visual Studio 2022** with “.NET desktop development”.
2. Open the solution (or create one and add the project).
3. Restore packages and **Build**.

## Build (CLI)

```powershell
cd src\AzerothReforged.Launcher
dotnet build -c Debug
dotnet run
