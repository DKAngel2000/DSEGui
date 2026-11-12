# DSEGui

DSE Setup Tool — a WPF front end for [dse](https://github.com/SouNobbao/dse)
(Driver Signature Enforcement).

## Download

Grab the latest `DseGui.exe` from the
[Releases page](https://github.com/DKAngel2000/DSEGui/releases/latest).
The app is a single, framework-dependent file — it needs the
[.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) installed.

The app updates itself: on every start it checks this repo's latest release and,
if a newer version was published, offers to download it, swap it in and restart.

## Build

```powershell
dotnet publish DseGui.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Every push to `main` is built by GitHub Actions (`.github/workflows/release.yml`)
and published as a release tagged `v<Version>` from `DseGui.csproj`.
Bump `<Version>` to ship a new build.
