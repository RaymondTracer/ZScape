# ZScape

ZScape is an Avalonia desktop server browser for Zandronum. It queries the Zandronum master server, fetches detailed data from individual servers, helps locate missing WADs, and can launch either stable or testing Zandronum builds directly.

The project targets .NET 10. It builds on Windows, Linux, and macOS, with the most polished integration currently on Windows.

---

## Table of Contents

- [Features](#features)
- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
- [Usage](#usage)
- [Configuration](#configuration)
- [Development Notes](#development-notes)
- [Troubleshooting](#troubleshooting)
- [Contributing](#contributing)
- [License](#license)

---

## Features

### Server Browser and Launching
- Query the Zandronum master server and then fetch per-server details concurrently
- Browse rich server metadata including players, map, game mode, IWAD, PWADs, and optional WADs
- Launch directly into Zandronum from the server list
- Track favorites by server address or exact server name, plus add manual servers not present in the master list
- Keep connection history and copy connect commands from the UI
- Receive server alerts through native Windows notifications or custom in-app popup notifications

### Filtering and Discovery
- Filter by game mode, player counts, ping, map, IWAD, PWADs, and country
- Save and reuse filter presets
- Use text-match rules for favorite and hidden server name behavior
- Refresh only favorites when you want a watchlist-style workflow
- Resolve unknown countries through cached IP geolocation using `ip-api.com`

### WAD Management
- Detect missing WADs before launch and search configured WAD folders with cached results
- Download WADs from configured mirrors, the `/idgames` archive, and DuckDuckGo fallback search
- Verify PWAD hashes when the server provides them
- Extract supported archives with SharpCompress
- Adapt download concurrency per domain and persist the learned settings

### Testing Builds, Updates, and Housekeeping
- Auto-download and install server-provided testing builds when required
- Manage installed testing versions from the dedicated dialog
- Copy base Zandronum `.ini` files into newly installed testing builds
- Consolidate screenshots from stable and testing installs into one folder
- Run GitHub release checks in disabled, notify-only, or auto-download modes
- Use a first-time setup flow that can auto-detect Zandronum and common WAD locations

---

## Prerequisites

- .NET 10 SDK
- A Zandronum installation if you want to launch or join servers from the app
- Network access to the master server and WAD download sources

Platform notes:

- Windows is the primary runtime target and includes native toast notifications plus dark title-bar integration.
- Linux and macOS builds are supported by the project file, but some desktop integrations are necessarily platform-specific.

---

## Quick Start

1. Open a terminal in the repository root.
2. Build the solution:

```bash
dotnet build ZScape.sln -c Debug
```

3. Run the application:

```bash
dotnet run --project ZScape.csproj
```

4. On first launch, complete the setup dialog:

- Select your stable Zandronum executable.
- Optionally select a testing versions folder.
- Configure WAD search and download folders.
- Choose your update behavior.

When running from source, ZScape stores its data beside the built executable in `AppContext.BaseDirectory`, not in the repository root.

---

## Usage

- Press `F5` or use the refresh action to query the master server.
- Double-click a server to launch Zandronum and connect.
- Use the context menu to refresh a single server, copy a connect command, add address favorites, or add exact-name favorites.
- Use the quick search box and filter dialog to narrow the server list.
- If a server requires missing WADs, use the WAD download flow before joining.
- If a server requires a testing build, ZScape can download and install it before launch.
- Enable screenshot monitoring if you want screenshots from stable and testing installs collected into one destination.
- Enable verbose logging when you need protocol-level diagnostics in the log panel and `runtime.log`.

---

## Configuration

ZScape uses portable-style files stored beside the executable:

- `settings.json` - main application settings
- `history.json` - connection history
- `domain-settings.json` - learned per-domain downloader settings
- `runtime.log` - runtime logging and exception output

Notable configuration areas include:

- Window layout, splitter positions, column widths, and sorting
- Current filter state and saved filter presets
- Favorite servers, favorite server-name rules, hidden server-name rules, and manual servers
- Auto-refresh behavior, retry counts, timeouts, and concurrency
- WAD search paths, download sites, and download concurrency settings
- Stable and testing Zandronum paths
- Alert display mode and alert behavior
- Screenshot consolidation settings
- Update behavior, interval, and GitHub release source

Implementation details:

- Master server host, port, and query flags live in `Protocol/ProtocolConstants.cs`.
- Huffman encode/decode support lives in `Protocol/HuffmanCodec.cs`.
- Default `TestingVersions` and `Screenshots` locations are resolved relative to the configured stable Zandronum directory when explicit paths are not set.

---

## Development Notes

Main areas of the codebase:

- `Controls/` - reusable Avalonia controls
- `Models/` - server, player, WAD, and filter models
- `Protocol/` - master-server and game-server query logic
- `Services/` - settings, logging, updates, downloads, launching, notifications, and screenshot monitoring
- `Themes/` - shared theme resources
- `Utilities/` - helpers for paths, formatting, color codes, matching, and constants
- `Views/` - Avalonia windows and dialogs, including the main window and settings flows

Useful entry points:

- `Program.cs` sets up Avalonia and top-level exception logging.
- `App.axaml` and `App.axaml.cs` initialize the application.
- `Views/MainWindow.axaml` and `Views/MainWindow.axaml.cs` contain the main browser UI and interaction logic.

Debugging notes:

- `LoggingService` writes `runtime.log` to the application base directory on every run.
- Unhandled startup, UI-thread, and task exceptions are logged automatically.
- The in-app log panel reflects the same logging pipeline used for file output.

---

## Troubleshooting

- If server refresh fails entirely, confirm DNS and network access to `master.zandronum.com:15300`.
- If the app crashes, fails during startup, or silently aborts an operation, inspect `runtime.log` beside the built executable first.
- If launching fails, verify that the stable Zandronum path is configured. Testing servers also need a valid testing root or the default `TestingVersions` path next to the stable install.
- If WADs are not found, verify your configured WAD search paths and download folder in settings.
- If screenshot consolidation is not working, make sure screenshot monitoring is enabled and that the stable/testing paths are configured correctly.
- If update checks do not run, verify the configured GitHub owner and repository in settings.
- Commercial IWADs such as DOOM, DOOM II, Heretic, Hexen, and similar titles are intentionally excluded from automated download.
- Country lookups for unknown servers are rate-limited and cached. Failures are recorded as Unknown to avoid repeated lookups.

---

## Contributing

- Open an issue for significant bugs or feature changes.
- Keep changes focused and consistent with the existing codebase structure.
- Open a pull request with a clear summary of user-visible behavior changes.

---

## License

ZScape - Copyright (C) 2026 Charlie Gadd

Licensed under the GNU General Public License v3.0. See `LICENSE` for details.
