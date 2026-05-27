# SyncMaster / CalExport — Developer Wiki

This document is the technical reference for developers maintaining or extending CalExport (the calendar-export module of the SyncMaster suite). It covers architecture, every class in the codebase, application flows, configuration, export formats, testing strategy, extension points, and known limitations.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Layer-by-layer Reference](#2-layer-by-layer-reference)
3. [Application Flow](#3-application-flow)
4. [Configuration Reference](#4-configuration-reference-settingsjson)
5. [Export Formats](#5-export-formats)
6. [Testing](#6-testing)
7. [Extension Points](#7-extension-points)
8. [Known Limitations and Notes](#8-known-limitations-and-notes)

---

## 1. Architecture Overview

### Layered Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        Program.cs                           │
│                  (Composition Root)                         │
└─────────────────────────────┬───────────────────────────────┘
                              │ constructs + wires
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                    Presentation Layer                       │
│  ApplicationRunner  │  ArgumentParser  │  ConsoleIO         │
│  ConsoleApplicationTerminator                               │
└──────────────┬──────────────────────────────────────────────┘
               │ depends on interfaces from Core
               ▼
┌─────────────────────────────────────────────────────────────┐
│                       Core Layer                            │
│  Contracts (interfaces)   │   Models (pure data records)   │
│  Services (business logic — no I/O, no COM, no Console)    │
└──────────┬───────────────────────────────┬──────────────────┘
           │                               │
           ▼                               ▼
┌──────────────────────┐   ┌───────────────────────────────────┐
│   Infrastructure     │   │         Configuration             │
│  OutlookCalendarSvc  │   │  AppSettings  SettingsResolver    │
│  PhysicalFileSystem  │   │  SettingsRepository               │
└──────────────────────┘   └───────────────────────────────────┘
```

Dependency direction: outer layers depend on inner layers only through interfaces defined in Core. Infrastructure and Configuration implement Core contracts; they are never referenced by Core directly.

### Layer Descriptions

| Layer | Responsibility |
|---|---|
| **Core/Contracts** | Defines the interfaces (`ICalendarService`, `IFileSystem`, etc.) that decouple business logic from concrete I/O |
| **Core/Models** | Pure immutable data records (`AppointmentRecord`, `ExportParameters`, etc.) with no dependencies |
| **Core/Services** | Business logic that operates on models; depends only on Core contracts |
| **Configuration** | `AppSettings` model and the logic to load, resolve, and save `settings.json` |
| **Infrastructure** | COM interop (`OutlookCalendarService`) and filesystem (`PhysicalFileSystem`) implementations |
| **Presentation** | CLI argument parsing, console interaction, and top-level orchestration (`ApplicationRunner`) |
| **Program.cs** | Composition root: constructs every concrete type, wires dependencies, calls `ApplicationRunner.Run` |

### Key Design Principles

- **Single Responsibility** — each class has one clearly named job; `ApplicationRunner` orchestrates flows but delegates every distinct operation to a collaborator.
- **Open/Closed** — new export formats are added by implementing `IAppointmentExporter` without touching existing exporters or `AppointmentExportService` (factory delegate pattern).
- **Liskov Substitution** — tests freely substitute `ICalendarService`, `IFileSystem`, and `IConsoleIO` with mocks; production implementations are swappable.
- **Interface Segregation** — interfaces are narrow (`IConsoleIO` has four methods; `IApplicationTerminator` has two).
- **Dependency Inversion** — `ApplicationRunner`, `AppointmentExportService`, and `OutputDirectoryService` depend on abstractions injected through constructors.
- **DRY** — `MonthNames` is the single source of month name strings; `SettingsResolver` centralises all token-resolution logic; `BuildSettings` in `ApplicationRunner` is the single place that assembles a settings object.
- **Constructor validation** — every class that accepts injected dependencies throws `ArgumentNullException` for null arguments in its constructor, enforcing fail-fast composition.

---

## 2. Layer-by-layer Reference

### Core/Models

#### `ExportMode` (enum)

| Value | Meaning |
|---|---|
| `Simple` | Produce a pipe-delimited `.txt` file |
| `Complete` | Produce a structured `.json` file |

#### `AppointmentRecord`

Immutable record holding a single calendar event. All properties use `init` setters.

| Property | Type | Description |
|---|---|---|
| `Start` | `DateTime` | Local start time (used by Simple exporter and for sorting) |
| `Duration` | `int` | Duration in minutes |
| `IsAllDay` | `bool` | True for all-day events |
| `Subject` | `string` | Event title; defaults to `"(no title)"` if blank |
| `OrganizerName` | `string` | Display name of the organizer |
| `OrganizerEmail` | `string` | SMTP address of the organizer (may be empty) |
| `IsCancelled` | `bool` | True when MeetingStatus is 5 or 7, or subject starts with "Canceled:"/"Cancelled:" |
| `StartOffset` | `DateTimeOffset` | Complete mode only — local start with UTC offset |
| `EndOffset` | `DateTimeOffset` | Complete mode only — local end with UTC offset |
| `StartTimeZoneId` | `string` | Complete mode only — Windows timezone ID |
| `StartTimeZoneDisplayName` | `string` | Complete mode only — human-readable timezone name |
| `Description` | `string` | Complete mode only — body text |
| `Participants` | `IReadOnlyList<ParticipantRecord>` | Complete mode only — all recipients |

#### `ParticipantRecord`

Immutable. Describes one meeting recipient.

| Property | Type | Values |
|---|---|---|
| `Name` | `string` | Display name |
| `Email` | `string` | SMTP address |
| `Type` | `string` | `"required"`, `"optional"`, `"resource"` |
| `Response` | `string` | `"accepted"`, `"tentative"`, `"declined"`, `"notResponded"`, `"organizer"`, `"none"` |

#### `CalendarFolderInfo`

Identifies a specific Outlook calendar folder.

| Property | Type | Description |
|---|---|---|
| `DisplayName` | `string` | e.g. `"Calendar [user@company.com]"` — shown in picker and stored in `settings.json` |
| `EntryId` | `string` | Outlook COM EntryID — used with `GetFolderFromID` to re-open a specific folder |
| `StoreId` | `string` | Outlook COM StoreID — paired with EntryId for cross-store folder lookup |

#### `ExportParameters`

Value object that carries all parameters for a single export run. Validated at construction time.

| Property | Type | Notes |
|---|---|---|
| `Year` | `int` | Must be > 0 (enforced by constructor) |
| `Month` | `int` | Must be 1–12 (enforced by constructor) |
| `Mode` | `ExportMode` | Simple or Complete |
| `IncludeCancelled` | `bool` | Whether to include cancelled events |
| `SelectedFolders` | `IReadOnlyList<CalendarFolderInfo>?` | `null` means "all calendars" |

Constructor throws `ArgumentOutOfRangeException` for invalid `year` or `month`.

#### `ParsedArguments`

Immutable output of `ArgumentParser.Parse`. All properties nullable except `AutoMode`.

| Property | Type | Description |
|---|---|---|
| `AutoMode` | `bool` | `-a` / `--auto` was present |
| `ConfigPath` | `string?` | Path from `-c` / `--config`, or null |
| `OutputPath` | `string?` | Path from `-o` / `--output`, or null |

---

### Core/Contracts

#### `ICalendarService`

```csharp
IReadOnlyList<CalendarFolderInfo> GetCalendarFolders();
IReadOnlyList<AppointmentRecord>  GetAppointments(ExportParameters parameters);
```

`GetCalendarFolders` enumerates all calendar-type `MAPIFolder` objects across all Outlook stores.  
`GetAppointments` returns all appointments in the requested month, filtered and (optionally) deduped.  
Implemented by `OutlookCalendarService`. Used by `ApplicationRunner` (folder listing) and `AppointmentExportService` (export).

#### `IAppointmentExporter`

```csharp
string FileSuffix    { get; }
string FileExtension { get; }
string Serialize(IReadOnlyList<AppointmentRecord> records, ExportContext context);
```

Converts a sorted list of records to a string for writing to disk.  
`FileSuffix` becomes part of the output filename (e.g. `simple`, `complete`).  
`FileExtension` is the file extension without a dot (e.g. `txt`, `json`).  
Implemented by `SimpleAppointmentExporter` and `CompleteAppointmentExporter`.

#### `ISettingsRepository`

```csharp
AppSettings? TryLoad(string path);
AppSettings  LoadOrCreateDefault(string path);
void         Save(AppSettings settings, string path);
bool         Exists(string path);
```

`TryLoad` returns null if the file does not exist or cannot be parsed.  
`LoadOrCreateDefault` creates and writes a default `AppSettings` if the file is missing.  
Implemented by `SettingsRepository`.

#### `IFileSystem`

```csharp
bool   FileExists(string path);
bool   DirectoryExists(string path);
void   CreateDirectory(string path);
string ReadAllText(string path);
void   WriteAllText(string path, string content, Encoding encoding);
void   WriteAllLines(string path, IEnumerable<string> lines, Encoding encoding);
```

Thin abstraction over `System.IO` for testability. Implemented by `PhysicalFileSystem`.

#### `IConsoleIO`

```csharp
void    Write(string text);
void    WriteLine(string? text = null);
void    WriteError(string text);
string? ReadLine();
```

Wraps `Console.Write`, `Console.WriteLine`, `Console.Error.WriteLine`, and `Console.ReadLine`. Implemented by `ConsoleIO`. Tests inject a mock to capture and control console interaction.

#### `IApplicationTerminator`

```csharp
[DoesNotReturn] void Exit(int code);
[DoesNotReturn] void ExitWithError(string message, int code = 1);
```

Both methods are annotated `[DoesNotReturn]`, which informs the compiler that code after a call to either method is unreachable. This allows callers to write `_terminator.ExitWithError(…); throw new InvalidOperationException("Unreachable");` without triggering a CS0162 warning.

In tests, the mock throws instead of calling `Environment.Exit`, allowing exit-path logic to be tested without terminating the test process. Implemented by `ConsoleApplicationTerminator`.

---

### Core/Services

#### `AppointmentExportService`

**Purpose:** Orchestrates a full export: fetches records, sorts them, selects the right exporter, builds the file content, and writes it to disk.

**Dependencies:** `ICalendarService`, `Func<ExportMode, IAppointmentExporter>` (factory delegate), `IFileSystem`, `IConsoleIO`

**Key method:**

```csharp
public string Export(ExportParameters parameters, string outputDirectory)
```

Returns the full path of the written file.

**Factory pattern:** The exporter is selected at call time via the factory delegate rather than being injected directly. This keeps `AppointmentExportService` open for new formats — the delegate in `Program.cs` is the only place that maps `ExportMode` to a concrete type:

```csharp
IAppointmentExporter SelectExporter(ExportMode mode) => mode switch
{
    ExportMode.Simple   => new SimpleAppointmentExporter(),
    ExportMode.Complete => new CompleteAppointmentExporter(),
    _ => throw new ArgumentOutOfRangeException(nameof(mode))
};
```

**File naming convention:** `Calendar_{year}_{MonthName}_{FileSuffix}_{yyyyMMdd}.{FileExtension}`  
Example: `Calendar_2026_May_complete_20260523.json`

#### `SimpleAppointmentExporter`

**Purpose:** Formats records as a pipe-delimited `.txt` file (one line per event).

**FileSuffix / FileExtension:** `"simple"` / `"txt"`

**Line format:**

- Regular event: `yyyy-MM-dd | HH:mm | Xh YYm | Subject | Name <email>`
- All-day event: `yyyy-MM-dd | All day | All day | Subject | Name <email>`
- Cancelled event (either type): line as above, appended with ` | CANCELADO`
- Organizer with no email: `Name` without angle brackets

**Context parameter** (`ExportContext`) is accepted by the interface but not used by Simple — the format carries no metadata header.

#### `CompleteAppointmentExporter`

**Purpose:** Formats records as a structured Newtonsoft JSON file.

**FileSuffix / FileExtension:** `"complete"` / `"json"`

**Timezone handling:** For each event, both local-with-offset and UTC are recorded. The local offset is derived from the Windows timezone ID reported by Outlook's `AppointmentItem.StartTimeZone.ID`. If the ID cannot be read or is not found in the system timezone database, `TimeZoneInfo.Local` is used as fallback. See section 5 for the full JSON schema.

**Serialization:** Uses `Newtonsoft.Json` anonymous object projection with `Formatting.Indented`.

#### `CalendarFolderMatcher`

**Purpose:** Resolves a list of calendar display-name strings against the live list of `CalendarFolderInfo` objects returned by Outlook.

**Key method:**

```csharp
public IReadOnlyList<CalendarFolderInfo>? Match(
    IEnumerable<string>              requestedNames,
    IReadOnlyList<CalendarFolderInfo> available,
    Action<string>?                  onNotFound = null)
```

**Null = all calendars semantics:** Returns `null` when the input list is empty or when no names matched at all. A null return throughout the codebase consistently means "use all calendars". Duplicate entries are eliminated by `EntryId` (case-insensitive).

**onNotFound callback:** Called for each name that was not found, allowing the caller to log a warning without this class taking a dependency on `IConsoleIO`.

#### `OutputDirectoryService`

**Purpose:** Resolves the effective output directory, creating it if necessary.

**Key method:**

```csharp
public string Resolve(string? requestedPath, string fallbackPath, bool createSilently)
```

- If `requestedPath` is null/empty, returns `fallbackPath` (the exe directory).
- If the directory exists, returns its full path.
- If it does not exist, prompts (unless `createSilently` is true) and calls `IFileSystem.CreateDirectory`.
- On `UnauthorizedAccessException`, `ArgumentException`, or `IOException`, calls `_terminator.ExitWithError`.

#### `MonthNames`

**Purpose:** Central lookup table for English month names (1 = January, 12 = December).

**Access:**

```csharp
MonthNames.Get(int month)          // returns "January"…"December"; throws on out-of-range
MonthNames.All                     // IReadOnlyList<string> of all 12 names
```

Internal static class — not exposed outside the assembly.

---

### Configuration

#### `AppSettings`

The JSON-mapped settings model. Properties use `JToken` for fields that accept either a string token or a number, which allows `settings.json` to be flexible without a custom converter.

| Property | JSON Key | Type | Default |
|---|---|---|---|
| `Year` | `"year"` | `JToken` (JValue string or integer) | `new JValue("current")` |
| `Month` | `"month"` | `JToken` (JValue string or integer) | `new JValue("current")` |
| `Mode` | `"mode"` | `string` | `"complete"` |
| `IncludeCancelled` | `"includeCancelled"` | `bool` | `true` |
| `Calendars` | `"calendars"` | `JToken` (JValue "all" or JArray of strings) | `new JValue("all")` |
| `OutputPath` | `"outputPath"` | `string?` | `null` |

Newtonsoft.Json is used directly (via `JsonProperty` attributes and `JToken`) rather than `System.Text.Json` because the `JToken` flexibility for mixed-type fields is simpler to implement with Newtonsoft.

#### `SettingsResolver`

**Purpose:** Converts the raw `JToken` values in `AppSettings` into concrete types the rest of the application can use.

| Method | Returns | Behaviour |
|---|---|---|
| `ResolveYear(settings)` | `int` | `"current"` → `DateTime.Today.Year`; `"previous"` → year - 1; integer JValue → that integer; unrecognised → current year |
| `ResolveMonth(settings)` | `int` | `"current"` → `DateTime.Today.Month`; `"previous"` → `DateTime.Today.AddMonths(-1).Month`; integer 1–12 → that integer; unrecognised → current month |
| `ResolveMode(settings)` | `ExportMode` | `"simple"` (case-insensitive) → `Simple`; anything else → `Complete` |
| `ResolveCalendarNames(settings)` | `string[]?` | `"all"` → `null`; JArray → `string[]`; deserialization failure → `null` |

Null return from `ResolveCalendarNames` means "all calendars".

#### `SettingsRepository`

**Purpose:** Load, create, and save `settings.json` via `IFileSystem`.

| Method | Behaviour |
|---|---|
| `TryLoad(path)` | Returns null if file missing or JSON is invalid (swallows `JsonException`) |
| `LoadOrCreateDefault(path)` | Calls `TryLoad`; if null, constructs `new AppSettings()`, saves it, and returns it |
| `Save(settings, path)` | Serializes to indented JSON and writes via `IFileSystem.WriteAllText` |
| `Exists(path)` | Delegates to `IFileSystem.FileExists` |

---

### Infrastructure

#### `OutlookCalendarService`

**Purpose:** Implements `ICalendarService` using Outlook COM interop.

**COM lifecycle:** Each public method creates a fresh `Application` COM object if needed. If Outlook was not already running when the method was called (`owned = true`), the `Application` is quit and released in the `finally` block. This avoids leaving ghost Outlook processes behind.

Every COM object obtained is released with `Marshal.ReleaseComObject` in a `finally` block. Failures during release are silently caught to avoid masking the real exception.

**`GetCalendarFolders` flow:**
1. Connect to MAPI namespace.
2. Iterate `ns.Stores` — each store represents an Outlook account/PST.
3. For each store, recursively walk `MAPIFolder.Folders`, adding any folder whose `DefaultItemType == olAppointmentItem`.
4. `DisplayName` is formatted as `"{folder.Name} [{store.DisplayName}]"`.

**`GetAppointments` flow:**
1. Build a DASL filter string for `[Start] >= rangeStart AND [Start] < rangeEnd`.
2. For each target folder (all stores if `SelectedFolders == null`, or specific folders by EntryId/StoreId):
   - Set `items.IncludeRecurrences = true` and call `items.Sort("[Start]")` before `items.Restrict(filter)`. This order is required to correctly expand recurring series.
   - For each `AppointmentItem` in the filtered set, capture `Start` immediately (before accessing `GlobalAppointmentID`), check cancellation status, and call `BuildRecord`.
   - Add to results via a `(start, subject)` dedup key.

**Cancellation detection:** An appointment is considered cancelled if:
- `MeetingStatus == 5` (`olMeetingCanceled`) — cancelled by organizer
- `MeetingStatus == 7` — received and cancelled
- Subject starts with `"Canceled:"` or `"Cancelled:"` (handles some Outlook versions that prefix the subject)

**Deduplication key:** `$"{start:yyyy-MM-dd HH:mm}|{subject}"`. `GlobalAppointmentID` is not used because accessing it on recurring-series occurrences can corrupt the COM context and cause `Start` to return the series master start date rather than the occurrence date. The `(start, subject)` composite key reliably deduplicates across multi-calendar selections without that risk.

**Organizer email resolution:** The organizer name comes from `AppointmentItem.Organizer`. The email is resolved by iterating `Recipients` to find a recipient whose `Name` matches `Organizer`, then calling `GetRecipientEmail`. Exchange users use `GetExchangeUser().PrimarySmtpAddress`; others use `AddressEntry.Address`.

**`BuildRecord` mode optimization:** When `ExportMode == Simple`, the more expensive COM calls (`Body`, `StartTimeZone`, `Recipients`) are skipped entirely, which speeds up simple exports of large calendars.

#### `PhysicalFileSystem`

**Purpose:** Implements `IFileSystem` by delegating directly to `System.IO.File` and `System.IO.Directory`.

Reads use explicit `Encoding.UTF8` to match the write path and avoid BOM issues with the default system encoding.

---

### Presentation

#### `ConsoleIO`

**Purpose:** Implements `IConsoleIO` by wrapping `System.Console`.

`WriteError` writes to `Console.Error` (stderr), which keeps error messages separate from normal output and allows piping or redirection of stdout-only content.

#### `ConsoleApplicationTerminator`

**Purpose:** Implements `IApplicationTerminator` by calling `Environment.Exit`.

Both `Exit` and `ExitWithError` are annotated `[DoesNotReturn]` and follow the call with `throw new InvalidOperationException("Unreachable")`. The throw is never reached at runtime but satisfies the C# compiler's definite-assignment analysis for callers that use these methods inside non-void branches.

In tests, `IApplicationTerminator` is mocked. The mock typically throws a custom test exception (e.g., `ApplicationExitException`) so that unit tests can assert on exit-path code without the test process terminating.

#### `ArgumentParser`

**Purpose:** Parses `string[] args` into a `ParsedArguments` record.

Recognised flags: `-a`/`--auto`, `-c`/`--config <path>`, `-o`/`--output <path>`.

Throws `ArgumentParsingException` (a custom `Exception` subclass) for unknown flags or missing path arguments. `Program.cs` catches this before the composition root is wired and calls `Environment.Exit(1)` directly — the only place in the codebase where `IApplicationTerminator` is bypassed.

#### `ApplicationRunner`

**Purpose:** Top-level application orchestrator. Receives all dependencies via constructor injection and drives one of three flow modes based on `ParsedArguments`.

**Constructor parameters** (all validated for null):

| Parameter | Interface / Type | Role |
|---|---|---|
| `console` | `IConsoleIO` | All user-facing I/O |
| `calendarService` | `ICalendarService` | Folder listing |
| `settingsRepository` | `ISettingsRepository` | Load/save settings |
| `fileSystem` | `IFileSystem` | File existence checks |
| `settingsResolver` | `SettingsResolver` | Token resolution |
| `folderMatcher` | `CalendarFolderMatcher` | Name-to-folder matching |
| `outputDirService` | `OutputDirectoryService` | Output directory resolution |
| `exportService` | `AppointmentExportService` | The actual export |
| `terminator` | `IApplicationTerminator` | Controlled exit |
| `exeDir` | `string` | Default settings and output path |

**`ResolveDefaults` helper:** Calls `SettingsResolver` for year, month, mode, and calendar names in one place, returning a named tuple. Used at the top of all three flow modes.

**`PromptMonthSaveMode` helper:** Asks the user how to store the month in `settings.json` and returns a `JToken` — either a `JValue(int)` for a fixed month, `JValue("current")`, or `JValue("previous")`.

**Three flow modes** — see section 3 for step-by-step detail:

| Condition | Method called |
|---|---|
| `args.AutoMode == true` | `RunAutoMode` |
| `args.AutoMode == false && pendingCreatePath != null` | `RunNewConfigFlow` |
| `args.AutoMode == false && pendingCreatePath == null` | `RunNormalFlow` |

`pendingCreatePath` is non-null when `-c` was given but the file does not exist.

---

## 3. Application Flow

### Settings file resolution (all modes)

Before any flow mode runs, `ResolveSettingsFile` determines which `AppSettings` to use:

1. If `-c` was not given: load (or create) `settings.json` next to the exe.
2. If `-c` was given and the file exists: load it and use it as the active settings path.
3. If `-c` was given but the file does not exist: load the default `settings.json` as the baseline, set `pendingCreatePath` to the `-c` path (to be created later if the user confirms).
4. If `-c` was given but the file exists and fails to parse: show an error and the example JSON, ask whether to continue with defaults. If yes, use default `settings.json`; if no, call `_terminator.Exit(0)`.

### Normal Interactive Mode (no flags)

```
1. ResolveSettingsFile        → load/create settings.json next to exe
2. OutputDirectoryService.Resolve   → determine output directory
3. ResolveDefaults            → compute year, month, mode, cancellation, calNames from settings
4. Display current defaults   → show a summary table to the user
5. Prompt "Proceed? [Y/n]"
   ├── Y (or Enter):
   │   └── ResolveNamedCalendars if calNames != null
   │       └── DoExport
   └── N:
       ├── Connect to Outlook, list calendar folders
       ├── PromptCalendarSelection
       ├── PromptYear
       ├── PromptMonth
       ├── PromptMode
       ├── PromptIncludeCancelled
       ├── AskSaveDefaults
       │   ├── Y → PromptMonthSaveMode → BuildSettings → WriteSettingsWithDirCreation
       │   └── N → skip
       └── DoExport
```

### Auto Mode (`-a`)

```
1. ResolveSettingsFile        → load/create settings.json next to exe (or custom -c path)
2. OutputDirectoryService.Resolve (createSilently=true)
3. ResolveDefaults
4. Display current settings   → printed but not confirmed
5. ResolveNamedCalendars if calNames != null
6. If pendingCreatePath != null → WriteSettingsWithDirCreation (silent, no prompts)
7. DoExport
```

No user input is requested at any step. If the output directory does not exist it is created silently.

### Custom Config Mode — existing file (`-c path` where file exists)

Follows the exact same flow as Normal Interactive Mode or Auto Mode depending on whether `-a` is present, except the active settings path is the custom file and the prompt header shows the custom filename.

### Custom Config Mode — new file (`-c path` where file does not exist)

```
1. ResolveSettingsFile        → pendingCreatePath = <path>, settings = default settings.json
2. OutputDirectoryService.Resolve
3. ResolveDefaults from default settings
4. Prompt "Start with default settings? [Y/n]"
   ├── Y:
   │   ├── Display defaults
   │   ├── Prompt "These settings look good? [Y/n]"
   │   │   ├── Y → use defaults (resolve named calendars if needed)
   │   │   └── N → go interactive (same prompts as Normal N-branch above)
   └── N:
       └── go interactive
5. Prompt "Save these settings to '<path>'? [y/N]"
   └── Y → PromptMonthSaveMode → BuildSettings → WriteSettingsWithDirCreation
6. DoExport
```

In **auto mode** with a non-existent `-c` path, the interactive steps are skipped and the file is created silently at step 6 of Auto Mode.

---

## 4. Configuration Reference (`settings.json`)

### Full Schema

```json
{
  "year":             "current",
  "month":            "current",
  "mode":             "complete",
  "includeCancelled": true,
  "calendars":        "all",
  "outputPath":       null
}
```

### Field Reference

| Field | JSON type | Accepted values | Default | Notes |
|---|---|---|---|---|
| `year` | string or number | `"current"`, `"previous"`, integer ≥ 1 | `"current"` | `"current"` resolves to `DateTime.Today.Year` at run time |
| `month` | string or number | `"current"`, `"previous"`, `1`–`12` | `"current"` | `"previous"` resolves to `DateTime.Today.AddMonths(-1).Month` |
| `mode` | string | `"simple"`, `"complete"` | `"complete"` | Case-insensitive; anything other than `"simple"` → Complete |
| `includeCancelled` | boolean | `true`, `false` | `true` | Controls whether cancelled events appear in the output |
| `calendars` | string or array | `"all"` or `["Calendar [user@co.com]"]` | `"all"` | `"all"` exports every calendar across all accounts |
| `outputPath` | string or null | Absolute directory path or `null` | `null` | `null` or missing → same directory as the executable |

### Token Resolution Details

**`"current"` / `"previous"` for year and month** are evaluated at run time each time the tool starts, so a config saved with `"month": "previous"` will always default to last month regardless of when it was created.

**`"previous"` month across year boundary:** `DateTime.Today.AddMonths(-1).Month` correctly handles January (returns 12) without any special-case logic.

**Mixed types:** The JSON parser accepts both `"month": 5` and `"month": "5"` as the integer 5, since `SettingsResolver` handles both `JTokenType.Integer` and numeric strings.

### Examples

Fixed export for a specific past month:

```json
{
  "year": 2025,
  "month": 11,
  "mode": "simple",
  "includeCancelled": false,
  "calendars": ["Calendar [work@company.com]"],
  "outputPath": "D:\\timesheets"
}
```

Always export the previous month for all calendars:

```json
{
  "year": "current",
  "month": "previous",
  "mode": "complete",
  "includeCancelled": true,
  "calendars": "all",
  "outputPath": null
}
```

---

## 5. Export Formats

### Simple (TXT)

#### Normal event line

```
yyyy-MM-dd | HH:mm | Xh YYm | Subject | OrganizerName <email>
```

Example:

```
2026-05-12 | 14:00 | 1h 30m | Sprint Planning | Jane Doe <jane@company.com>
```

#### All-day event line

```
yyyy-MM-dd | All day | All day | Subject | OrganizerName <email>
```

#### Cancelled event line

Cancelled events append ` | CANCELADO` regardless of whether the event is all-day or not:

```
2026-05-15 | 09:00 | 1h 00m | Weekly Sync | Jane Doe <jane@company.com> | CANCELADO
```

#### Organizer with no email

When the organizer's email address cannot be resolved from COM, the email part and angle brackets are omitted:

```
2026-05-20 | 11:00 | 0h 30m | Team Standup | Jane Doe
```

#### File naming

```
Calendar_{year}_{MonthName}_simple_{yyyyMMdd}.txt
```

Example: `Calendar_2026_May_simple_20260523.txt`

---

### Complete (JSON)

#### Full schema

```json
{
  "exportedAt": "<ISO 8601 with offset>",
  "period": {
    "year":      2026,
    "month":     5,
    "monthName": "May"
  },
  "calendars": ["all"] ,
  "events": [
    {
      "subject":                  "string",
      "isAllDay":                 false,
      "isCancelled":              false,
      "start":                    "2026-05-12T14:00:00-06:00",
      "startUtc":                 "2026-05-12T20:00:00Z",
      "startTimeZoneId":          "Central Standard Time",
      "startTimeZoneDisplayName": "(UTC-06:00) Central Time (US & Canada)",
      "end":                      "2026-05-12T15:30:00-06:00",
      "endUtc":                   "2026-05-12T21:30:00Z",
      "durationMinutes":          90,
      "organizer": {
        "name":  "Jane Doe",
        "email": "jane@company.com"
      },
      "description": "string (body text, may be empty string)",
      "participants": [
        {
          "name":     "Alice Smith",
          "email":    "alice@company.com",
          "type":     "required",
          "response": "accepted"
        }
      ]
    }
  ]
}
```

#### Top-level fields

| Field | Type | Description |
|---|---|---|
| `exportedAt` | ISO 8601 string with offset | Timestamp when the export ran (`DateTimeOffset.Now.ToString("o")`) |
| `period.year` | integer | The exported year |
| `period.month` | integer | The exported month number (1–12) |
| `period.monthName` | string | English month name |
| `calendars` | array | `["all"]` when all calendars were exported; otherwise an array of `DisplayName` strings |

#### Event fields

| Field | Type | Description |
|---|---|---|
| `subject` | string | Event title; `"(no title)"` if blank in Outlook |
| `isAllDay` | boolean | True for all-day events |
| `isCancelled` | boolean | True if the event was detected as cancelled |
| `start` | ISO 8601 with offset | Local start time + UTC offset |
| `startUtc` | ISO 8601 UTC (`Z` suffix) | Same moment expressed in UTC |
| `startTimeZoneId` | string | Windows timezone ID (e.g. `"Central Standard Time"`) |
| `startTimeZoneDisplayName` | string | Human-readable name (e.g. `"(UTC-06:00) Central Time (US & Canada)"`) |
| `end` | ISO 8601 with offset | Local end time + UTC offset |
| `endUtc` | ISO 8601 UTC | Same as `startUtc` pattern |
| `durationMinutes` | integer | Duration in minutes (from `AppointmentItem.Duration`) |
| `organizer.name` | string | Display name of the meeting organizer |
| `organizer.email` | string | SMTP address; empty string if not resolvable |
| `description` | string | Body text; empty string if blank or unreadable |
| `participants` | array | All `Recipients` from the COM item |

#### Timezone field explanation

Both local+offset and UTC are stored for every event. This allows an importing system to:

- Display the event in the original local time (use `start` / `end`).
- Sort or compare events across timezones (use `startUtc` / `endUtc`).
- Identify the originating timezone for calendar round-tripping (use `startTimeZoneId`).

Only `startTimeZoneId` and `startTimeZoneDisplayName` are stored (not end timezone) because Outlook's COM model exposes the start timezone directly; the end timezone is always the same timezone as the start for standard appointments.

#### Participant fields

| Field | Possible values |
|---|---|
| `type` | `"required"`, `"optional"`, `"resource"` |
| `response` | `"accepted"`, `"tentative"`, `"declined"`, `"notResponded"`, `"organizer"`, `"none"` |

`"organizer"` response appears when the recipient's `MeetingResponseStatus` maps to code 1. `"none"` is the fallback for any unrecognised code.

#### File naming

```
Calendar_{year}_{MonthName}_complete_{yyyyMMdd}.json
```

Example: `Calendar_2026_May_complete_20260523.json`

---

## 6. Testing

### Test Project Structure

```
SyncMaster.CalExport.Tests/
└── Unit/
    ├── ArgumentParserTests.cs          # CLI flag parsing, error cases
    ├── AppointmentRecordTests.cs       # Model default values
    ├── AppointmentExportServiceTests.cs # Export orchestration, file naming, sorting
    ├── CalendarFolderMatcherTests.cs   # Name matching, dedup, null semantics
    ├── CompleteAppointmentExporterTests.cs # JSON output structure and field values
    ├── ExportParametersTests.cs        # Constructor validation (year/month range)
    ├── MonthNamesTests.cs              # Get() range, all 12 names
    ├── OutputDirectoryServiceTests.cs  # Directory resolution, creation, error exits
    ├── SettingsRepositoryTests.cs      # Load/save/TryLoad/Exists via mock IFileSystem
    ├── SettingsResolverTests.cs        # All token resolution paths for year/month/mode/calendars
    └── SimpleAppointmentExporterTests.cs # TXT line formatting, all-day, cancelled, no-email
```

**Total: 126 unit tests**

### Running the Tests

Run from the solution root:

```
dotnet test
```

Run only the test project:

```
dotnet test SyncMaster.CalExport.Tests/SyncMaster.CalExport.Tests.csproj
```

Run with verbose output:

```
dotnet test -v normal
```

### Coverage

Generate an LCOV coverage report (processed by most CI tools):

```
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=lcov /p:CoverletOutput=./coverage/
```

Generate a Cobertura report (for Azure DevOps or GitHub Actions coverage reports):

```
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura /p:CoverletOutput=./coverage/
```

The `coverlet.msbuild` package is already included in `SyncMaster.CalExport.Tests.csproj`. The target coverage for business logic classes is 80%+ line coverage.

### Key Test Classes and What They Cover

| Test class | What it verifies |
|---|---|
| `ArgumentParserTests` | All valid flag combinations; missing path argument throws; unknown flag throws |
| `AppointmentExportServiceTests` | Correct exporter is selected; records are sorted by `Start`; file is named correctly; event count is printed |
| `SettingsResolverTests` | Every token (`"current"`, `"previous"`, integer, string-integer, unrecognised) for year and month; mode case-insensitivity; calendar name array and `"all"` |
| `SettingsRepositoryTests` | `TryLoad` returns null for missing file and invalid JSON; `LoadOrCreateDefault` creates and saves default on first call; `Save` writes correct JSON |
| `OutputDirectoryServiceTests` | Existing directory returns immediately; missing directory with confirm=Y creates it; confirm=N exits; silent mode creates without prompt; errors call terminator |
| `CalendarFolderMatcherTests` | Match by display name (case-insensitive); not-found callback; duplicate EntryId deduplication; empty input returns null |
| `SimpleAppointmentExporterTests` | Date/time/duration format; all-day format; cancelled suffix; no-email organizer format; multiple events joined by newline |
| `CompleteAppointmentExporterTests` | JSON structure; all event fields present; `"all"` calendars array vs named array; ISO 8601 date strings |
| `ExportParametersTests` | `ArgumentOutOfRangeException` for year < 1 and month out of 1–12 range; valid construction succeeds |
| `MonthNamesTests` | `Get` returns correct name for 1–12; throws for 0 and 13; `All` has 12 entries |

### Testing Strategy

**Why COM infrastructure is excluded from unit tests:**  
`OutlookCalendarService` requires Outlook Classic to be installed and running. It makes live COM calls that cannot be meaningfully mocked at the interop boundary without a real Outlook process. The COM types (`AppointmentItem`, `MAPIFolder`, etc.) are sealed and carry no test-friendly interfaces. Instead, `ICalendarService` is mocked in `AppointmentExportServiceTests` and wherever else it is consumed, achieving full business-logic coverage without Outlook.

**Why `PhysicalFileSystem` is excluded from unit tests:**  
`PhysicalFileSystem` is a three-line wrapper per method. All consumers depend on `IFileSystem`, so the behavior that matters (what the application does with the filesystem) is tested via the mock. The wrapper itself has no logic to test.

**How `IApplicationTerminator` enables testing exit flows:**  
`OutputDirectoryServiceTests` injects a mock `IApplicationTerminator` that throws a custom exception instead of calling `Environment.Exit`. This allows tests to assert that the terminator was called with the right message without terminating the test process. The `[DoesNotReturn]` attribute on the interface methods ensures the compiler is happy with code that follows an `ExitWithError` call inside a non-void method.

---

## 7. Extension Points

### Adding a New Export Format

1. Create a new class implementing `IAppointmentExporter` in `Core/Services/`:

```csharp
public sealed class CsvAppointmentExporter : IAppointmentExporter
{
    public string FileSuffix    => "csv";
    public string FileExtension => "csv";

    public string Serialize(IReadOnlyList<AppointmentRecord> records, ExportContext context)
    {
        // build CSV string
    }
}
```

2. Add a new `ExportMode` value if needed, or reuse an existing one with a different setting.

3. Update the factory delegate in `Program.cs`:

```csharp
IAppointmentExporter SelectExporter(ExportMode mode) => mode switch
{
    ExportMode.Simple    => new SimpleAppointmentExporter(),
    ExportMode.Complete  => new CompleteAppointmentExporter(),
    ExportMode.Csv       => new CsvAppointmentExporter(),
    _ => throw new ArgumentOutOfRangeException(nameof(mode))
};
```

4. Update `SettingsResolver.ResolveMode` and the interactive prompt in `ApplicationRunner.PromptMode` to recognise the new mode string.

No other classes need to change. `AppointmentExportService` is already open for new formats.

### Adding a New Settings Field

1. Add a property to `AppSettings` with a `[JsonProperty]` attribute and a sensible default:

```csharp
[JsonProperty("maxEvents")]
public int? MaxEvents { get; set; } = null;
```

2. Add a resolver method to `SettingsResolver` if the field requires token resolution or defaulting:

```csharp
public int? ResolveMaxEvents(AppSettings settings) =>
    settings.MaxEvents is > 0 ? settings.MaxEvents : null;
```

3. Pass the resolved value through `ExportParameters` or wherever it is consumed.

4. Update `ApplicationRunner.BuildSettings` to include the new field when saving settings.

5. Update the `settings.json` reference section in `README.md` and section 4 of this wiki.

### Swapping the Calendar Backend

Implement `ICalendarService` with a different data source (e.g., an `.ics` file parser, a Graph API client, or a test stub):

```csharp
public sealed class IcsFileCalendarService : ICalendarService
{
    public IReadOnlyList<CalendarFolderInfo> GetCalendarFolders() { ... }
    public IReadOnlyList<AppointmentRecord>  GetAppointments(ExportParameters parameters) { ... }
}
```

Then swap the concrete type in `Program.cs`:

```csharp
var calService = new IcsFileCalendarService(icsFilePath);
```

No other code changes are needed.

---

## 8. Known Limitations and Notes

### COM Interop Requires Outlook Classic

`OutlookCalendarService` depends on the `Microsoft.Office.Interop.Outlook` COM automation interface. Outlook Classic must be installed, configured, and signed in on the machine where the tool runs. It does not work with the new Outlook (web-based) or Outlook for Mac. If Outlook is not running when the tool starts, Outlook will be launched silently, used, and then quit automatically.

### Target Frameworks

`SyncMaster.Core` and `CalImport` target `net10.0`. `CalExport` targets `net10.0-windows` because it depends on the Outlook COM interop, which is Windows-only. `init`-only setters and the `[DoesNotReturn]` attribute are provided natively by the .NET 10 runtime, so no polyfills are needed.

### `Environment.Exit` in Program.cs

There is exactly one place in the codebase where `Environment.Exit` is called directly outside of `ConsoleApplicationTerminator`: the `ArgumentParsingException` catch block in `Program.cs`. This is intentional and documented with a comment. At that point in execution, the composition root has not run, `IApplicationTerminator` has not been constructed, and there is no object to call. The direct exit is the only option. Callers should not introduce any other direct `Environment.Exit` calls.

### Recurring Events: IncludeRecurrences + Sort Before Restrict

The Outlook COM model requires a specific call order to expand recurring events correctly:

```csharp
items.IncludeRecurrences = true;   // must be set first
items.Sort("[Start]");             // must be called before Restrict
var filtered = items.Restrict(filter);
```

If `IncludeRecurrences` is not set, the filter matches only the series master and returns one item for the entire series. If `Sort` is not called before `Restrict`, `IncludeRecurrences` may have no effect on some Outlook builds. This is a known quirk of the Outlook COM object model.

### Deduplication by `(start, subject)` Key

Appointments are deduplicated using the composite key `"{start:yyyy-MM-dd HH:mm}|{subject}"`. This handles the case where the same calendar is scanned twice (e.g., because it appears in two stores, or because a shared calendar is visible under multiple accounts).

`GlobalAppointmentID` is not used as the dedup key despite being Outlook's canonical identity field. The reason is that accessing `GlobalAppointmentID` on a recurring-series occurrence after `IncludeRecurrences = true` can corrupt the COM occurrence context, causing subsequent reads of `Start` on the same item to return the series master start date instead of the occurrence date. The `(start, subject)` key is reliable and safe. The trade-off is that two genuinely different events at the same time with the same subject would be deduplicated as one — an acceptable limitation for the intended use case.

### `dynamic` Keyword

`OutlookCalendarService` uses the `dynamic` keyword to access `AppointmentItem.StartTimeZone.ID` and `.Name` without a strongly-typed COM reference to the timezone interface. On .NET 10 the `dynamic` binder ships with the runtime, so no separate `Microsoft.CSharp` reference is required.
