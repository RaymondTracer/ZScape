# Refresh regression checks

Run `dotnet run --project tests/RefreshChecks/RefreshChecks.csproj` from the repository root.
The executable uses Avalonia's headless platform; it does not open desktop windows or query live servers.

Checks cover keyed row selection, replacement and removal, duplicate keys, collection reset avoidance,
context-menu deferral, explicit reset, and concurrent endpoint notification batching.

`ResizableListView.UpdateRows` accepts a complete snapshot and a stable key. Use its optional
reconciler to reuse unchanged models. Duplicate keys are matched by their order of occurrence;
use a truly unique key where the source provides one. `ResetRows` deliberately clears selection,
scrolling, and deferred data when switching to a different data scope. `RowsUpdated` is the place
to update counts or actions that depend on the snapshot actually being applied.

The migrated lists are read-only row presentations. This API does not merge editable drafts;
editable lists need an explicit draft ownership policy before migration.
