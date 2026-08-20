# ZScape

ZScape is a cross-platform server browser and launcher for
[Zandronum](https://zandronum.com/). It queries the master server, shows live
server details, finds missing WADs, and launches the correct stable or testing
build. ZScape runs on Windows, Linux, and macOS and currently targets .NET 10.

## Features

### Browse and find servers

- Search server names, maps, addresses, versions, countries, game modes, IWADs,
  and PWADs. Search ignores punctuation and highlights matching text.
- Filter by player count, ping, map, IWAD, PWAD, country, and game mode, with
  reusable filter presets.
- Choose which columns are shown and save their widths. Header menus provide
  multi-level sorting, automatic sizing, and layout reset.
- Keep favorites by address or name rule, hide servers by name rule, and add
  servers that are not listed by the master.
- Auto-refresh the full list or favorites only, and receive native or in-app
  alerts when watched servers become active.
- Search and sort connection history, reconnect to a previous server, or copy
  its address.

### Launch and manage Zandronum

- Join a server from the list, with password prompts, version checks, WAD
  checks, and hash verification before launch.
- Launch offline games or host a server from saved launch profiles.
- Install required stable and testing builds and manage installed testing
  versions.
- Use the standard desktop layout or Big UI, a controller-friendly layout with
  arrow-key and SDL game-controller navigation.
- Choose a dark or light theme during first-run setup.

### Download and maintain files

- Search configured WAD mirrors, `/idgames`, and a DuckDuckGo fallback when a
  required file is missing.
- Download `.wad`, `.pk3`, `.pk7`, `.ipk3`, `.ipk7`, and `.pke` files, plus
  `.zip`, `.7z`, and `.rar` archives. Per-domain concurrency settings and
  resumable segmented transfers are used where the server supports byte ranges.
- Consolidate screenshots from stable and testing installs into one directory.
- Check GitHub releases in disabled, notify-only, or automatic-download mode.

Commercial IWADs are never downloaded automatically.

## Requirements

- The .NET 10 SDK to build from source, or the .NET 10 runtime for the
  framework-dependent release packages.
- A Zandronum installation to play. On supported platforms, the first-run
  wizard can install a stable build or use an existing one.
- Network access to the Zandronum master server and any download sources you
  enable.

Native notifications use Windows toasts, the freedesktop notification service
on Linux, and Notification Center on macOS.

## Build and run

From the repository root:

```sh
dotnet build ZScape.sln -c Debug
dotnet run --project ZScape.csproj
```

To build both Windows configurations and keep timestamped, runnable copies
outside the working tree, run this from PowerShell:

```powershell
.\scripts\Build-ZScape.ps1
```

The script places complete Debug and Release outputs under
`Documents\ZScape\Builds\<timestamp>`. It copies the files after a successful
build; it does not move or alter an existing Zandronum installation or its
backups.

The first-run wizard configures Zandronum, WAD paths, theme, and update
behavior.

CI builds `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and
`osx-arm64`. Tags beginning with `v` create packages on
[GitHub Releases](https://github.com/RaymondTracer/ZScape/releases).

## Using ZScape

- Press `F5` to refresh the server list.
- Double-click a server to join it.
- Right-click a server to refresh it, copy its connect command, or change its
  favorite status.
- Right-click the server-list header to choose columns, resize them, or set an
  ordered sort.
- Use **File > Launch Game** to play offline or host without joining a listed
  server.
- Use **View > Big UI Mode** for the controller-friendly interface.
- Turn on verbose logging from **View > Log Panel** when diagnosing protocol or
  download problems.

## Files and settings

ZScape is portable: it writes data to `AppContext.BaseDirectory`, beside the
running executable. The directory must therefore be writable.

- `settings.json` contains application settings, filters, favorites, layout,
  and saved launch profiles.
- `history.json` contains connection history.
- `domain-settings.json` contains learned and user-defined per-domain download
  settings.
- `wad-hash-cache.json` stores full local WAD MD5 values with file identity and
  timestamp metadata. It is enabled by default, can be disabled in
  **Preferences**, and never replaces a server's full hash comparison.
- `runtime.log` contains runtime messages and unhandled exception details.

When paths are left blank, the WAD download folder defaults to `WADs` beside the
application. `TestingVersions` and `Screenshots` default to directories beside
the configured stable Zandronum install.

Country lookup for otherwise unknown servers uses the rate-limited
`ip-api.com` batch API. Update checks use the `githubOwner` and `githubRepo`
values in `settings.json`. WAD fallback searches send the requested WAD name to
DuckDuckGo.

## Development

`Protocol/` contains the master-server and game-server clients, including the
Huffman codec. `Services/` owns querying, persistence, downloads, launching,
updates, notifications, and screenshot monitoring. Avalonia controls and
windows live in `Controls/` and `Views/`; models and shared helpers live in
`Models/` and `Utilities/`.

`Program.cs` configures Avalonia and process-level exception logging.
`Views/MainWindow.axaml.cs` is the main UI coordinator. There is currently no
separate test project, so the baseline repository check is:

```sh
dotnet build ZScape.csproj -c Release
```

## Troubleshooting

- If refresh fails, check DNS and UDP access to
  `master.zandronum.com:15300`.
- If startup or an operation fails, inspect `runtime.log` beside the executable.
- If settings do not persist, make sure the application directory is writable.
- If launch fails, check the configured stable and testing paths in
  Preferences.
- If a WAD is not found, check the search paths, download directory, and mirror
  list. Required commercial IWADs must be supplied manually.
- If screenshot consolidation fails, confirm that monitoring is enabled and
  the stable/testing paths are valid.
- If update checks fail, verify `githubOwner` and `githubRepo` in
  `settings.json`.

Bug reports and focused pull requests are welcome.

## License

Copyright (C) 2026 Charlie Gadd.

ZScape is licensed under the [GNU General Public License v3.0](LICENSE).
