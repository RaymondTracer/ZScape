# Ownership and refresh regression checks

Run `dotnet run --project tests/RefreshChecks/RefreshChecks.csproj` from the repository root.
The executable uses Avalonia's headless platform; it does not open desktop windows or query live servers.

Checks cover keyed row selection, replacement and removal, duplicate keys, collection reset avoidance,
context-menu deferral, explicit reset, and concurrent endpoint notification batching.

The cleanup checks additionally cover domain-edit/adaptive-update merges; single/bulk refresh
transitions and cancellation cleanup; real theme/settings preview acceptance and rollback;
full-file/payload hashing, snapshot invalidation and manual-action eligibility; cancellable
join preparation; shared notification content and activation targets; toolbar/advanced search
and visibility policies; bounded installation discovery; and archive extraction policy.

WAD-path checks exercise platform-specific directory identity, ordered filename
precedence, unavailable-root persistence/reconnection, and root-only case-insensitive
filename lookup. Fixtures use the ignored `artifacts/regression-fixtures` directory;
they never write to the running application's portable settings.

To run while the main Debug executable is open, use
`dotnet run --project tests/RefreshChecks/RefreshChecks.csproj --artifacts-path artifacts/cleanup-build`.

`ResizableListView.UpdateRows` accepts a complete snapshot and a stable key. Use its optional
reconciler to reuse unchanged models. Duplicate keys are matched by their order of occurrence;
use a truly unique key where the source provides one. `ResetRows` deliberately clears selection,
scrolling, and deferred data when switching to a different data scope. `RowsUpdated` is the place
to update counts or actions that depend on the snapshot actually being applied.

The migrated lists are read-only row presentations. This API does not merge editable drafts;
editable lists need an explicit draft ownership policy before migration.

## Ownership audit, October 2026

This is a bounded cleanup wave, not a declaration of repository-wide compliance.
The table records owners inspected, consolidated behavior, and remaining work. A remaining
duplicate is not permission to merge policies that intentionally differ.

| Subsystem | Shared owner / consolidated rule | Remaining work or intentional difference |
| --- | --- | --- |
| Protocol and server refresh | Protocol codecs/clients own wire data; `ServerBrowserService` owns retry outcomes and failure transitions | Master queries and server queries are different protocols. Endpoint parsing is still duplicated; IPv6 is not certified by this suite. |
| Filtering and server rows | `ServerFilter`, `TextMatchUtility`, `ServerInfo` | Toolbar/advanced bot-only scopes deliberately differ for human spectators. Main/history sort plumbing is still duplicated, with different selectors. |
| WAD paths and discovery | `PathResolver` owns directory identity; `WadManager` owns ordered configured/effective roots | Unavailable roots remain configured, empty lists stay empty, and startup does not add WAD roots. Filename lookup is root-only; Browser/archive scans are intentionally recursive. |
| Join preparation | `GameLauncher` owns required/optional requirements, background preparation and launch cancellation | Manual-download projection still drops hash/optional metadata. Startup/settings loads and copy-connect-command preparation can still perform synchronous file work. |
| Hashes and cache actions | `WadHashCacheService` owns hashing, validated snapshots, exclusions and filesystem-free manual-action presentation | Fresh download/archive reads and cache reads retain distinct sharing/trust policies. Excluding caching never excludes verification. Full MD5, not an archive filename prefix, decides a match. |
| Downloads and installation | `ArchiveExtractionService` owns archive containment and materialization | Testing/WAD ranged-transfer and speed-sampling mechanics still overlap. Scheduler/domain budgets remain WAD-specific. DMG mounts and application replacement scripts are platform adapters. |
| Domain configuration | `DomainThreadConfig` owns normalization, locked snapshots and baseline-aware editor merges | `SettingsService` remains the persistence collaborator; adaptive updates made while an editor is open must survive untouched fields. |
| Settings and drafts | `SettingsService` owns saved settings; `ServerRowHeightPresentation` owns the temporary row-height display override | Other mutable settings apply/notification flows remain. Rule trimming and editor deduplication still have separate implementations. The unused alternate settings registry was removed. |
| Themes and lists | `ThemeService` owns palette preview/apply/rollback; `ResizableListView` owns keyed read-only row updates | Third-party theme templates and remaining literal colors are outside this palette slice. Category navigation and editable native lists are not interchangeable with read-only tables. |
| Versions, configuration sync and screenshots | `PathResolver` owns bounded installation/executable discovery; screenshot queue/retry remains owned by `ScreenshotMonitorService` | Managers may retain incomplete installations; launch/config consumers require an executable. Screenshot scope excludes arbitrary saved launches/backups. Config-sync path helpers still duplicate identity logic. |
| Notifications and OS operations | `NotificationService` owns request content/context; `FileManagerService` owns Locate File | Native action capabilities differ. Testing-manager Open Folder still has its own OS switch; macOS notification invocation remains synchronous. |
| Supporting utilities and input | Existing `JsonUtils`, `WadExtensions`, logging, country and controller services remain owners | Byte/ETA formatting still has local copies. IP-country cache access and controller polling need separate concurrency/idle-load work. No hardware/idle-CPU claim is made by these checks. |

## Evidence limits

Fixtures live on the repository drive and isolate settings/cache writes. Headless controls
exercise actual menu/dialog/list behavior, but do not prove native popup capture, controller
input, OS notifications, or Linux/macOS execution. No live network downloads, installs,
server joins, or native screenshot watchers run in this suite.

Archive containment is a lexical path policy. Existing destination symlinks/reparse points
need a separate policy; this wave does not claim to prevent traversal through them. Cancelled
or failed extraction preserves complete entries and removes the current staged file, rather
than rolling back an entire installation. Application update installation retains its
existing synchronous workflow, and testing downloads retain their existing cancellation API.
