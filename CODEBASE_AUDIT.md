# ZScape Codebase Audit

Date: 2026-04-18

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

1. `ZScape.csproj` `NU1903` known vulnerability in transitive package `Tmds.DBus.Protocol` 0.20.0
2. `Controls/ResizableListView.cs(662,36)` `CS8619` nullability mismatch
3. `Views/WadDownloadDialog.axaml.cs(451,51)` `CS0067` event `LogEntry.PropertyChanged` is never used

## High-Confidence Findings

### 1. Updater asset selection is Windows-specific despite multi-runtime targeting

Severity: Medium

The project declares `win-x64`, `linux-x64`, and `osx-x64` runtime identifiers, but `UpdateService.DownloadUpdateAsync()` only looks for a Windows x64 zip asset. Non-Windows builds cannot currently use the automatic download path successfully.

Relevant files:

- `ZScape.csproj`
- `Services/UpdateService.cs`

### 2. IP geolocation currently uses HTTP endpoints

Severity: Medium

`Ip2CountryService` uses `http://ip-api.com` for both single and batch lookups. That exposes request traffic in cleartext and permits response tampering on the network path.

Relevant files:

- `Services/Ip2CountryService.cs`

## Overlooked or Dead Code

### 3. `AppSettings.ShowFavoritesOnly` is currently unused

Severity: Low

The field exists in `AppSettings`, but no usage was found outside the settings model. The current favorites-only filter is driven directly by the toolbar toggle, and the state is not persisted through `ShowFavoritesOnly`.

Relevant files:

- `Services/SettingsService.cs`
- `Views/MainWindow.axaml.cs`

### 4. `AppSettings.VerboseMode` appears to be legacy state

Severity: Low

The model still persists `VerboseMode`, but the current UI path uses `VerboseLogging` and applies that to `LoggingService.VerboseMode`. `AppSettings.VerboseMode` looks like leftover compatibility state rather than an actively used setting.

Relevant files:

- `Services/SettingsService.cs`
- `Views/MainWindow.axaml.cs`

## Suspicious Areas Requiring Runtime Validation

### 5. Master server packet parsing looks suspicious

Confidence: Medium

The master parser defines a `MasterResponseServerBlock` constant and the spec documents grouped endpoint blocks, but the current parsing logic does not appear to consume the block marker in a straightforward way. This may be correct for live packets, but it warrants targeted packet-trace validation before assuming the implementation is sound.

Relevant files:

- `Protocol/MasterServerClient.cs`
- `Protocol/ProtocolConstants.cs`
- `SPECIFICATION.md`

## Recommended Fix Order

1. Apply or remove dead settings fields such as `ShowFavoritesOnly` and legacy `VerboseMode`.
2. Validate master server packet parsing against a fresh packet trace.
