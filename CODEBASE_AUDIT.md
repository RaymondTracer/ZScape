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

### 1. Specification still describes updates as Windows-oriented partial behavior

Severity: Low

`SPECIFICATION.md` still says the update installation/restart flow is partial because asset packaging is Windows-oriented, but the code and release workflow now publish runtime-specific release archives and select the matching asset for Windows, Linux, and macOS. This is a specification parity gap rather than a runtime bug.

Relevant files:

- `SPECIFICATION.md`
- `Services/UpdateService.cs`
- `.github/workflows/build.yml`

### 2. Specification still says alert delivery falls back to logging

Severity: Low

`SPECIFICATION.md` still says native desktop notification delivery is partial and currently falls back to logging, but `NotificationService` now attempts native Windows toasts and falls back to the custom `ServerAlertNotificationWindow` popup path instead. This is stale spec text rather than missing functionality.

Relevant files:

- `SPECIFICATION.md`
- `Services/NotificationService.cs`
- `Views/ServerAlertNotificationWindow.cs`

## Recommended Fix Order

1. Update the specification's update-status note to match the current multi-runtime asset packaging and selection behavior.
2. Update the specification's alert-delivery note to describe native toast plus custom-popup fallback instead of logging-only fallback.
