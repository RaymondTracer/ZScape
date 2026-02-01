# ZScape

A modern, dark-themed WinForms application for browsing Zandronum game servers.

This repository contains a desktop client that queries the Zandronum master server, retrieves server lists, and queries individual servers for rich details (players, WADs, game mode, limits, etc.). The project is implemented in C# targeting .NET 10.

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

### Server Browsing
- Browse and query Zandronum game servers via the Zandronum master server
- Pipelined server queries with configurable concurrency and retry logic
- Favorite servers with priority querying
- Auto-refresh favorites only option (skip master server query)
- Manual server addition for servers not in master list
- Connection history tracking
- Server alerts when favorites come online with players

### Filtering
- Comprehensive filtering by game mode, player count, map, IWAD, WADs
- Country-based filtering with CheckedListBox UI and search functionality
- Mutual exclusion between include/exclude country lists
- Special country codes at top: Unknown, Anonymous Proxy, Satellite Provider, Regional
- IP-to-Country geolocation for servers with unknown countries (via ip-api.com)
- Ping range filtering
- Regex and wildcard pattern matching
- Saveable filter presets

### WAD Management
- Multi-threaded WAD downloads with parallel URL discovery
- idgames Archive integration for WAD searching
- Web search fallback (DuckDuckGo) for hard-to-find WADs
- Hash verification before joining servers
- Automatic WAD version archiving with hash suffixes
- Domain-specific thread optimization with adaptive learning

### Testing Builds
- Automatic download and installation of server-provided testing builds via `GameLauncher`
- Testing version management dialog
- Configuration file copying to new testing versions
- Screenshot consolidation from testing versions

### UI & UX
- Dark theme UI with custom control styling (`DarkTheme`)
- Sortable columns with persistent sort settings
- Verbose logging and optional hexdump output for protocol debugging
- Persisted application settings (`settings.json`) using `SettingsService`

---

## Prerequisites

- .NET 10 SDK (required to build and run)
- Windows is the primary development and runtime target (WinForms)

### Dependencies (NuGet)
- **SharpCompress** (0.44.5) - Archive extraction for testing builds and WAD downloads (zip, 7z, rar, tar)

---

## Quick Start

1. Open a terminal and change directory to the project folder:

```bash
cd ZScape
```

2. Build and run the application:

```bash
dotnet build
dotnet run
```

The application will create and use `settings.json` in the application's base directory to persist UI and behavior settings. By default the app will auto-refresh on launch (`AppSettings.RefreshOnLaunch = true`) unless changed in settings.

---

## Usage

- Use the **Refresh** toolbar action (or press `F5`) to query the master server for available servers.
- The server list supports sortable columns. Double-click a server to launch/connect to it.
- Right-click context menu provides options to copy connect command, refresh single server, add to favorites, or view connection history.
- Use the quick search box or open the **Filter** dialog for advanced filtering (game mode, IWAD, WADs, map, country, ping, player count).
- Click the star icon to mark servers as favorites. Use **Show Favorites Only** to filter to just favorites.
- Use **Servers > Add Server** to manually add servers not in the master list.
- Enable verbose logging and hexdumps in Settings for detailed protocol diagnostics.
- Missing WADs are detected automatically; use **Fetch WADs** to download them with the multi-threaded downloader.

---

## Configuration

- Settings are managed by `SettingsService` and stored in `settings.json` in the application directory. Defaults include window geometry, column widths, sort preferences, verbose mode off, and default concurrency settings.
- Protocol constants (master server host/port, challenges, flags) are defined in `Protocol/ProtocolConstants.cs`.
- Huffman encoding/decoding is implemented in `Protocol/HuffmanCodec.cs` and is compatible with the Zandronum/Skulltag Huffman tree.

Configuration options exposed in `AppSettings` include:
- Window position, size, and splitter positions
- Column widths and sorting preferences
- Filter presets and current filter state
- Verbose logging and hexdump toggles
- Auto-refresh settings and interval (including favorites-only mode)
- Query concurrency, retry attempts, and timing settings
- WAD search paths and download concurrency settings
- Domain-specific thread settings (learned automatically)
- Paths to Zandronum stable and testing binaries
- Favorite servers and manual server entries
- Server alert configuration
- Connection history
- Screenshot consolidation settings

---

## Development Notes

Key directories and files:

- `Protocol/` — `HuffmanCodec.cs`, `ProtocolConstants.cs`, `MasterServerClient.cs`, `ServerQueryClient.cs`
- `Models/` — `ServerInfo.cs`, `PlayerInfo.cs`, `TeamInfo.cs`, `PWadInfo.cs`, `WadInfo.cs`, `GameMode.cs`, `ServerFilter.cs`
- `Services/` — `ServerBrowserService.cs`, `SettingsService.cs`, `LoggingService.cs`, `WadDownloader.cs`, `WadManager.cs`, `GameLauncher.cs`, `DomainThreadConfig.cs`, `NotificationService.cs`, `ScreenshotMonitorService.cs`, `Ip2CountryService.cs`
- `UI/` — `MainForm.cs`, `DarkTheme.cs`, `UnifiedSettingsDialog.cs`, `ServerFilterDialog.cs`, `AddServerDialog.cs`, `ConnectionHistoryDialog.cs`, `FetchWadsDialog.cs`, `WadBrowserDialog.cs`, `WadDownloadDialog.cs`, `TestingVersionManagerDialog.cs`
- `Utilities/` — `AppConstants.cs`, `FormatUtils.cs`, `JsonUtils.cs`, `DarkModeHelper.cs`, `DoomColorCodes.cs`, `WadExtensions.cs`

Testing & debugging tips:
- Toggle `LoggingService.Instance.VerboseMode` and `ShowHexDumps` for protocol-level logs (visible in the application log panel when enabled).
- `ProtocolConstants` contains default timeout values aligned with `AppConstants`.

---

## Troubleshooting

- If the master server cannot be resolved, check DNS/network connectivity. The master host is `master.zandronum.com:15300` (configured in `Protocol/ProtocolConstants.cs`).
- Master server queries retry automatically (default 3 attempts with configurable delay).
- Server queries time out after the configured `ServerQueryTimeout` (default 3000 ms). Increase timeout in settings if on a high-latency network.
- Servers are marked offline after consecutive failures (configurable via `ConsecutiveFailuresBeforeOffline` setting, default 3).
- If decoding issues appear, enable verbose logging and hexdumps in Settings and review raw payloads in the log panel.
- WAD downloads support multiple sources with automatic fallback. If a download fails, alternate sources are tried automatically.
- The application extracts various archive formats (zip, 7z, rar, tar) using SharpCompress - no external tools required.
- Commercial IWADs (DOOM, DOOM2, Heretic, etc.) are in the forbidden list and won't be downloaded - you must obtain these yourself.
- Country codes are automatically normalized (USA -> US, XIP/XUN -> Unknown). Servers with unknown countries are resolved via IP geolocation (ip-api.com).
- IP geolocation is rate-limited (45 requests/minute) and cached. Failed lookups are marked as Unknown to prevent retries.

> Note: Some implementation details (such as segmented response handling and unconditional extended flag field writing in `CreateServerChallenge`) are documented in `SPECIFICATION.md` for reference.

---

## Contributing

- Create an issue describing the change or bug.
- Fork the repository and open a pull request with a clear description and tests where applicable.
- Keep code style consistent with the existing codebase.

---

## License

ZScape - Copyright (C) 2026 Charlie Gadd
Licensed under the GNU General Public License v3.0. See LICENSE for details.

---

## Where to look next

For a complete, implementation-accurate reference, see `SPECIFICATION.md` in this repository.
