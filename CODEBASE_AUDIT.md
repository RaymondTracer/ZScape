# ZScape Codebase Audit

Date: 2026-04-17

## Scope

This audit covered the full workspace for:

- correctness bugs
- overlooked or dead code
- incomplete or partially wired features
- parity gaps between the codebase and SPECIFICATION.md
- user-facing risks that are easy to miss because the project still builds cleanly

## Verification

- Verified build command: `dotnet build ZScape.sln -c Debug`
- Result: build succeeded
- Verified from workspace root: `C:\Users\user\Documents\Coding-Projects\ZScape`

### Build Warnings

1. `Controls/ResizableListView.cs(662,36)` `CS8619` nullability mismatch
2. `Services/NotificationService.cs(19,54)` `CS0067` event `AlertClicked` is never used
3. `Views/WadDownloadDialog.axaml.cs(451,51)` `CS0067` event `LogEntry.PropertyChanged` is never used

## High-Confidence Findings

### 1. Optional WADs are treated as required when joining servers

Severity: High

The protocol parser marks optional PWADs via `PWadInfo.IsOptional`, but the join-time WAD validation path checks every PWAD as mandatory. This can cause unnecessary download prompts or failed joins for servers that advertise optional content.

Relevant files:

- `Protocol/ServerQueryClient.cs`
- `Services/GameLauncher.cs`

### 2. Testing-build WAD discovery does not line up with testing executable folders

Severity: High

`MainWindow` only registers the stable executable folder with `WadManager`, while testing-server launch checks are version-specific. `GameLauncher` computes a testing executable folder but ultimately calls back into `WadManager.FindWad()` without using that folder directly. This can make WADs that are present next to a testing executable appear missing.

Relevant files:

- `Views/MainWindow.axaml.cs`
- `Services/GameLauncher.cs`
- `Services/WadManager.cs`

### 3. Auto-refresh favorites-only is exposed but not honored by the timer

Severity: Medium

The app persists and displays an `AutoRefreshFavoritesOnly` setting, and `ServerBrowserService` has a dedicated `RefreshFavoritesAsync()` path. The auto-refresh timer in `MainWindow`, however, always calls the full refresh path.

Relevant files:

- `Views/MainWindow.axaml.cs`
- `Services/ServerBrowserService.cs`

### 4. Favorite/manual server alerts are still a stub

Severity: Medium

Alert detection exists and the main window schedules background alert checks, but `NotificationService` currently only logs messages. Native desktop notifications are not implemented, and the dead `AlertClicked` event confirms the missing backend.

Relevant files:

- `Views/MainWindow.axaml.cs`
- `Services/NotificationService.cs`

### 5. Hexdump diagnostics are only partially wired

Severity: Medium

`LoggingService` supports `VerboseMode` and `ShowHexDumps`, and protocol clients call `LogHexDump()`. The current main-window wiring only applies `VerboseLogging` to `LoggingService.VerboseMode`; no runtime path was found that applies `AppSettings.ShowHexDumps` to the logger.

Relevant files:

- `Services/LoggingService.cs`
- `Services/SettingsService.cs`
- `Views/MainWindow.axaml.cs`
- `Protocol/MasterServerClient.cs`
- `Protocol/ServerQueryClient.cs`

### 6. Updater asset selection is Windows-specific despite multi-runtime targeting

Severity: Medium

The project declares `win-x64`, `linux-x64`, and `osx-x64` runtime identifiers, but `UpdateService.DownloadUpdateAsync()` only looks for a Windows x64 zip asset. Non-Windows builds cannot currently use the automatic download path successfully.

Relevant files:

- `ZScape.csproj`
- `Services/UpdateService.cs`

### 7. IP geolocation currently uses HTTP endpoints

Severity: Medium

`Ip2CountryService` uses `http://ip-api.com` for both single and batch lookups. That exposes request traffic in cleartext and permits response tampering on the network path.

Relevant files:

- `Services/Ip2CountryService.cs`

## Overlooked or Dead Code

### 8. `AppSettings.ShowFavoritesOnly` is currently unused

Severity: Low

The field exists in `AppSettings`, but no usage was found outside the settings model. The current favorites-only filter is driven directly by the toolbar toggle, and the state is not persisted through `ShowFavoritesOnly`.

Relevant files:

- `Services/SettingsService.cs`
- `Views/MainWindow.axaml.cs`

### 9. `AppSettings.VerboseMode` appears to be legacy state

Severity: Low

The model still persists `VerboseMode`, but the current UI path uses `VerboseLogging` and applies that to `LoggingService.VerboseMode`. `AppSettings.VerboseMode` looks like leftover compatibility state rather than an actively used setting.

Relevant files:

- `Services/SettingsService.cs`
- `Views/MainWindow.axaml.cs`

## Suspicious Areas Requiring Runtime Validation

### 10. Master server packet parsing looks suspicious

Confidence: Medium

The master parser defines a `MasterResponseServerBlock` constant and the spec documents grouped endpoint blocks, but the current parsing logic does not appear to consume the block marker in a straightforward way. This may be correct for live packets, but it warrants targeted packet-trace validation before assuming the implementation is sound.

Relevant files:

- `Protocol/MasterServerClient.cs`
- `Protocol/ProtocolConstants.cs`
- `SPECIFICATION.md`

## Recommended Fix Order

1. Fix optional-WAD handling during join.
2. Fix testing-build WAD discovery/search priority.
3. Wire auto-refresh favorites-only to `RefreshFavoritesAsync()`.
4. Finish notification delivery or explicitly degrade the feature in UI/docs.
5. Apply or remove dead settings fields such as `ShowFavoritesOnly` and legacy `VerboseMode`.
