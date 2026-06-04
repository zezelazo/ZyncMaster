# Zync Master — CalExport + CalImport

The **Zync Master** suite is two complementary Windows console tools that round-trip Outlook calendar data:

- **CalExport** — connects to **Outlook Classic** on the source machine via COM and exports a month of events to a plain-text file (Simple mode) or a structured JSON file (Complete mode). No Graph API, no EWS, no credentials required.
- **CalImport** — on the target machine, reads the JSON produced by CalExport's Complete mode and creates / updates / deletes events in a chosen calendar of an outlook.com (or work/school) account via the **Microsoft Graph API**. Works whether the target uses Outlook Classic *or* the "new Outlook for Windows" (which does not expose COM). Idempotent: re-importing the same file updates existing events instead of duplicating them.

Both tools share a `ZyncMaster.Core` library and follow the same layered SOLID architecture with dependency-injected interfaces, enabling full unit-test coverage of business logic without requiring Outlook or network access.

---

## Requirements

| Requirement | Details |
|---|---|
| OS | Windows 10 / 11 |
| .NET | .NET 10 SDK/runtime |
| Outlook | Outlook Classic (2016 / 2019 / Microsoft 365 desktop) must be installed and signed in |

---

## Build

```
dotnet build -c Release
```

The output executable is placed in `src\ZyncMaster.CalExport\bin\Release\net10.0-windows\ZyncMaster.CalExport.exe`.  
Copy `ZyncMaster.CalExport.exe` to any folder — it is self-contained for runtime use.

---

## Quick start

Run the executable with no arguments for the fully interactive flow:

```
ZyncMaster.CalExport.exe
```

On first run a `settings.json` file is created next to the executable with sensible defaults. The tool will then walk you through every option.

---

## Usage

```
ZyncMaster.CalExport.exe [-a|--auto] [-c|--config <path>] [-o|--output <path>]
```

| Flag | Description |
|---|---|
| *(none)* | Interactive mode — shows current defaults and asks for confirmation or custom selection |
| `-a` / `--auto` | Auto mode — runs silently using defaults, no prompts |
| `-c <path>` / `--config <path>` | Use a specific `settings.json` file instead of the one next to the exe |
| `-o <path>` / `--output <path>` | Write exported files to this directory instead of the exe directory |

All flags are **independent** — any combination is valid:

```
# Silent export with all defaults
ZyncMaster.CalExport.exe -a

# Interactive export using a custom config file
ZyncMaster.CalExport.exe -c "D:\work\my-settings.json"

# Silent export to a specific folder using a custom config
ZyncMaster.CalExport.exe -a -c "D:\work\my-settings.json" -o "D:\exports\calendar"
```

### Missing config file (`-c` with a non-existent path)

If the path given to `-c` does not exist, the tool guides you through setting it up:

1. Asks whether to start with default settings or go fully interactive
2. If defaults: shows them and asks for confirmation; if not happy, goes interactive
3. After all settings are confirmed, asks whether to save them to the new file
4. When saving, asks how the month should be stored (fixed value, always current, always previous)
5. Creates the directory structure if needed, then writes the file

In **auto mode** (`-a -c path`) the file is created silently with the resolved defaults.

### Missing output directory (`-o` with a non-existent path)

If the output directory does not exist the tool notifies you and asks for permission to create it (in auto mode it creates it silently). If creation fails an error is shown and the process exits.

---

## Interactive flow

```
=== Outlook Calendar Export ===

Default settings (settings.json):
  Calendars  : All calendars
  Year       : 2026
  Month      : May
  Mode       : Complete
  Cancelled  : Included
  Output dir : C:\tools\CalExport

Proceed with these settings? [Y/n]:
```

Press **Enter** (or type `Y`) to export immediately. Type `N` to choose your own options:

1. **Calendars** — lists all calendars found across all configured Outlook accounts; pick one, several (comma-separated), or all
2. **Year** — previous or current year
3. **Month** — 1–12
4. **Export mode** — Simple or Complete (see below)
5. **Cancelled events** — include or exclude

After selecting, you are asked whether to save these as new defaults. If yes, one more question:

> *How should the month be saved?*
> 1. Fixed — always the month you just chose
> 2. Current month — always the month when you run the tool
> 3. Previous month — always the month before the current one

---

## Export modes

### Simple — `.txt`

One line per event, pipe-delimited:

```
yyyy-MM-dd | HH:mm | Xh YYm | Subject | Organizer <email>
```

All-day events use `All day` for both time and duration.  
Cancelled events (when included) are marked at the end of the line:

```
2026-05-15 | 09:00 | 1h 00m | Weekly Sync | Jane Doe <jane@company.com> | CANCELADO
```

Example output file: `Calendar_2026_May_simple_20260523.txt`

---

### Complete — `.json`

Full structured export intended for import into other systems.

Example output file: `Calendar_2026_May_complete_20260523.json`

```json
{
  "exportedAt": "2026-05-23T10:30:00.000-06:00",
  "period": {
    "year": 2026,
    "month": 5,
    "monthName": "May"
  },
  "calendars": [ "all" ],
  "events": [
    {
      "id": "040000008200E00074C5B7101A82E00800000000C0E3...",
      "subject": "Project Review",
      "isAllDay": false,
      "isCancelled": false,
      "start": "2026-05-23T09:00:00-06:00",
      "startUtc": "2026-05-23T15:00:00Z",
      "startTimeZoneId": "Central Standard Time",
      "startTimeZoneDisplayName": "(UTC-06:00) Central Time (US & Canada)",
      "end": "2026-05-23T10:00:00-06:00",
      "endUtc": "2026-05-23T16:00:00Z",
      "durationMinutes": 60,
      "organizer": {
        "name": "Jane Doe",
        "email": "jane@company.com"
      },
      "description": "Quarterly review agenda...",
      "participants": [
        { "name": "Alice Smith",  "email": "alice@company.com",  "type": "required", "response": "accepted"      },
        { "name": "Bob Jones",   "email": "bob@company.com",    "type": "required", "response": "notResponded"  },
        { "name": "Carol White", "email": "carol@company.com",  "type": "optional", "response": "declined"      }
      ]
    }
  ]
}
```

#### Event `id` field

Every event includes a stable `id`. Source: Outlook's `GlobalAppointmentID` when readable; otherwise a deterministic UUID v5 derived from `organizerEmail | startUtc | subject`. This is the key CalImport uses for upsert — when you re-import the same file, events with a matching `id` are updated rather than duplicated. Do not remove or alter this field.

#### Timezone fields

| Field | Description |
|---|---|
| `start` | Local start time with UTC offset (ISO 8601) — e.g. `2026-05-23T09:00:00-06:00` |
| `startUtc` | Exact same moment in UTC — e.g. `2026-05-23T15:00:00Z` |
| `startTimeZoneId` | Windows timezone ID — e.g. `"Central Standard Time"` |
| `startTimeZoneDisplayName` | Human-readable timezone name |
| `end` / `endUtc` | Same pattern for the end time |

Both local+offset and UTC are stored so an importing system can reconstruct the correct time regardless of the timezone it operates in.

#### Participant fields

| Field | Values |
|---|---|
| `type` | `required`, `optional`, `resource` |
| `response` | `accepted`, `tentative`, `declined`, `notResponded`, `organizer`, `none` |

---

## `settings.json` reference

The file is created automatically on first run next to the executable. All fields are optional — missing fields fall back to their defaults.

```json
{
  "year": "current",
  "month": "current",
  "mode": "complete",
  "includeCancelled": true,
  "calendars": "all",
  "outputPath": null
}
```

| Field | Type | Values | Default |
|---|---|---|---|
| `year` | string or number | `"current"`, `"previous"`, or a year number like `2025` | `"current"` |
| `month` | string or number | `"current"`, `"previous"`, or `1`–`12` | `"current"` |
| `mode` | string | `"simple"`, `"complete"` | `"complete"` |
| `includeCancelled` | boolean | `true`, `false` | `true` |
| `calendars` | string or array | `"all"` or `["Calendar [user@company.com]", "..."]` | `"all"` |
| `outputPath` | string or null | Absolute path to output directory, or `null` for exe directory | `null` |

### Multiple config files

You can maintain separate configs for different scenarios:

```
ZyncMaster.CalExport.exe -a -c "D:\configs\work.json"     -o "D:\timesheets"
ZyncMaster.CalExport.exe -a -c "D:\configs\personal.json" -o "D:\personal\calendar"
```

---

## Multiple Outlook accounts

The tool automatically scans all stores (accounts) configured in Outlook. The calendar picker shows the account each calendar belongs to:

```
Available calendars:
    0. All calendars
    1. Calendar [work@company.com]
    2. Calendar [personal@outlook.com]
    3. Shared Team Calendar [work@company.com]

Your choice (0 = all, or comma-separated numbers e.g. 1,3):
```

To persist a specific selection in `settings.json`, use the exact display name shown in the list:

```json
{
  "calendars": ["Calendar [work@company.com]", "Shared Team Calendar [work@company.com]"]
}
```

---

## Recurring events

Recurring events are fully expanded — each occurrence is exported as an individual entry for the selected month. The tool sets `IncludeRecurrences = true` and applies the date filter after expansion, so no occurrences are missed or duplicated.

---

## Troubleshooting

**"Could not connect to Outlook"**  
Outlook Classic must be running and signed in. Start Outlook and try again.

**No events exported, but events exist**  
Verify the selected month and year. Check whether the events are in a calendar that was included in the selection.

**Calendar not found when using a saved name**  
Calendar display names include the account name (`Calendar [user@company.com]`). If the account was renamed or reconfigured, update the `calendars` array in `settings.json` or run interactively to re-select.

**Access denied creating output directory**  
Choose a path your user account has write access to, or run the tool as administrator.

---

# CalImport

CalImport takes the JSON file produced by CalExport's **Complete** mode and writes the events into a calendar of a Microsoft account, without sending any invitations to the participants listed in the source.

## Requirements

| Requirement | Details |
|---|---|
| OS | Windows 10 / 11 |
| .NET | .NET 10 SDK/runtime |
| Network | Internet access to `login.microsoftonline.com` and `graph.microsoft.com` |
| Account | A personal Microsoft account (`outlook.com`, `hotmail.com`, `live.com`, `msn.com`) **or** a work/school account. Outlook does **not** need to be installed on the target machine |
| Azure app | A one-time app registration (5–10 minutes, no Azure subscription needed) |

## One-time setup: register the Azure app

Microsoft Graph requires every app to identify itself with a `client_id`. To obtain one:

1. Sign in at <https://portal.azure.com> with the Microsoft account you'll use as the target.
2. **Azure Active Directory** → **App registrations** → **New registration**.
3. *Name*: `CalImport` (anything works).
4. *Supported account types*: **Personal Microsoft accounts only** (use **Personal Microsoft accounts and accounts in any organizational directory** if you also need work accounts).
5. *Redirect URI*: select **Public client/native (mobile & desktop)** and enter `http://localhost`.
6. Click **Register**.
7. In the new app: **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated permissions** → add `Calendars.ReadWrite` and `User.Read` (the `offline_access` permission is automatic).
8. **Authentication** → set **Allow public client flows** to **Yes** → **Save**.
9. Copy the **Application (client) ID** GUID into the `clientId` field of CalImport's `settings.json`.

No client secret is required — the public-client + PKCE flow is the correct one for a desktop app.

## Build

```
dotnet build -c Release
```

The output is at `src\ZyncMaster.CalImport\bin\Release\net10.0\ZyncMaster.CalImport.exe`.

## Usage

```
ZyncMaster.CalImport.exe [-s|--source <path>] [-a|--auto] [-c|--config <path>]
              [-k|--calendar <id>] [-n|--new-calendar <name>] [-w|--overwrite] [--dry-run]
```

| Flag | Description |
|---|---|
| *(none)* | Interactive — prompts for the source path and the target calendar |
| `-s <path>` / `--source <path>` | Path to the CalExport JSON to import |
| `-a` / `--auto` | Silent: no prompts. Requires `-s` and either `-k`/`-n` or a `defaultCalendarId` in settings |
| `-c <path>` / `--config <path>` | Use a specific `settings.json` instead of the one next to the exe |
| `-k <id>` / `--calendar <id>` | Use this calendar id as the destination |
| `-n <name>` / `--new-calendar <name>` | Create a new calendar with this name and use it (mutually exclusive with `-k`) |
| `-w` / `--overwrite` | On re-import, rebuild each updated event's body from the file (description + participants), replacing what's in the destination. Without it, your manual edits to the body are preserved and only the participants table is refreshed. Interactive mode asks this as a question. |
| `--dry-run` | Print the plan (Create / Update / Cancel / Skip per event) without touching Graph |

### Examples

```
# Fully interactive (asks for file and calendar)
ZyncMaster.CalImport.exe

# Import to the default calendar, no prompts
ZyncMaster.CalImport.exe -a -s "D:\export\Calendar_2026_May_complete_20260523.json"

# Preview what would happen
ZyncMaster.CalImport.exe -s "D:\export\Calendar_2026_May_complete_20260523.json" --dry-run

# Send everything into a brand-new calendar named "Migrated-2026-05"
ZyncMaster.CalImport.exe -a -s "D:\export\Calendar_2026_May_complete_20260523.json" -n "Migrated-2026-05"

# Import to an explicit existing calendar id
ZyncMaster.CalImport.exe -a -s "D:\export\Calendar_2026_May_complete_20260523.json" -k "AAMkAGI0MTU2ZGEx..."
```

### First run

On the first run the tool opens your default browser, you sign in with your Microsoft account, and Microsoft asks you to consent to read/write your calendar. The refresh token is then cached encrypted with Windows DPAPI under `%LOCALAPPDATA%\Zync Master\CalImport\msal.cache`; later runs sign in silently.

## `settings.json` reference

Created on first run next to the exe. All fields except `clientId` have sensible defaults.

```json
{
  "clientId": "00000000-0000-0000-0000-000000000000",
  "authority": "https://login.microsoftonline.com/consumers",
  "accountHint": "you@outlook.com",
  "defaultCalendarId": null,
  "reminderMinutes": 30,
  "extendedPropertyGuid": "1c5e1a3f-6d7b-4f1a-9c2e-3a4b5c6d7e8f"
}
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `clientId` | string | `""` | **Required** — Application (client) ID from your Azure app registration |
| `authority` | string | `"https://login.microsoftonline.com/consumers"` | Use `"https://login.microsoftonline.com/organizations"` or a tenant GUID for work/school accounts |
| `accountHint` | string or null | `null` | Pre-fills the sign-in dialog and disambiguates if multiple accounts are cached |
| `defaultCalendarId` | string or null | `null` | When set and `-k`/`-n` is not used, the calendar prompt is skipped |
| `reminderMinutes` | integer | `30` | One reminder per event — Outlook does not support multiple |
| `extendedPropertyGuid` | string (GUID) | fixed | Namespace for the singleValueExtendedProperty that stores the source `id` on each Graph event. **Never change** after the first import or existing events become unfindable |

## Idempotency: how the upsert works

1. Every event written to Graph carries a `singleValueExtendedProperty` named `CalImportSourceId` whose value is the source `id` from the JSON.
2. On every run, CalImport queries the target calendar for events that have a `CalImportSourceId` matching any `id` in the file.
3. For each input event:
   - **Not found** + active → **Create** new event.
   - **Found** + active → **Update** existing event. By default the body merge preserves user-edited text and only refreshes the participants table; pass `-w`/`--overwrite` to rebuild the body from the file instead.
   - **Found** + `isCancelled: true` → **Delete** existing event.
   - **Not found** + `isCancelled: true` → **Skip**.

Because matching is by stable `id`, re-running the same file is a no-op for unchanged events; editing an event in source and re-exporting + re-importing propagates the change.

## Participants: no invitations sent

Participants from the source event are **not** added as Graph `attendees` (that would send invitation emails). Instead, CalImport renders them inside the event body inside a marker-delimited HTML block:

```html
<p><b>Participantes (no invitados, solo referencia):</b></p>
<!-- calimport:participants:start -->
<ul>
  <li>Bob Smith &lt;bob@company.com&gt; — required — accepted</li>
  <li>Conference Room &lt;room@company.com&gt; — resource — none</li>
</ul>
<!-- calimport:participants:end -->
```

On **Update**, only the content between the two markers is replaced — any text you typed manually before or after the markers in Outlook is preserved.

## Limitations (v1)

- Outlook supports **one reminder per event**. The reminder is fixed by `reminderMinutes` (default 30); you cannot have both 30 and 15.
- **Recurring events** are imported as individual occurrences (the same way CalExport exports them). The series itself is not recreated.
- **Cancelled events** are **deleted** in the destination (no audit trail). Because the event is yours alone (no attendees), no cancellation emails are sent.
- v1 has only been exercised against personal Microsoft accounts. Work/school accounts should work with `authority` set to `organizations` or a tenant GUID, but are not tested in v1.
- A new event in the target calendar that was created outside CalImport (no `CalImportSourceId` extended property) is invisible to CalImport — duplicates are possible only if you manually create an event matching one in the source.

## Troubleshooting

**"clientId is empty in settings.json"**  
Complete the Azure app registration above and paste the GUID into `settings.json`.

**Browser opens but sign-in fails with `AADSTS650052` or `AADSTS50194`**  
The app's *Supported account types* does not match the account you're using. Re-create the registration choosing the correct option, or change `authority` accordingly.

**"Calendar id ... not found"**  
The id you passed to `-k` or stored in `defaultCalendarId` doesn't exist in the account. Run interactively once and pick from the list to grab the correct id.

**"Graph throttled" after several retries**  
You hit Microsoft Graph rate limits. Wait a few minutes and re-run; CalImport will skip already-imported events thanks to the upsert.

**Want to start over from scratch**  
Delete the `%LOCALAPPDATA%\Zync Master\CalImport\msal.cache` file (re-prompts for sign-in) and/or the destination calendar in outlook.com.

---

## Development

### Project structure

```
Zync Master/
├── Zync Master.sln
│
├── src/
│   ├── ZyncMaster.Core/                        # Shared library: models, contracts, helpers
│   │   ├── Models/      AppointmentRecord, ParticipantRecord, ExportMode
│   │   ├── Contracts/   IFileSystem, IConsoleIO, IApplicationTerminator, ISettingsRepository<T>
│   │   ├── Services/    MonthNames, UuidV5, SettingsRepository<T>
│   │   ├── Infrastructure/ PhysicalFileSystem
│   │   └── Presentation/   ConsoleIO, ConsoleApplicationTerminator
│   │
│   ├── ZyncMaster.CalExport/                   # Outlook Classic → JSON / TXT
│   │   ├── Program.cs                          # Composition root
│   │   ├── Core/        ExportContext, IAppointmentExporter, ICalendarService,
│   │   │                AppointmentExportService, CalendarFolderMatcher,
│   │   │                CompleteAppointmentExporter, SimpleAppointmentExporter, OutputDirectoryService
│   │   ├── Configuration/  AppSettings, SettingsResolver
│   │   ├── Infrastructure/Outlook/OutlookCalendarService   # COM interop
│   │   └── Presentation/   ApplicationRunner, ArgumentParser
│   │
│   └── ZyncMaster.CalImport/                   # JSON → Microsoft Graph
│       ├── Program.cs                          # Composition root
│       ├── Core/Contracts/  IImportSource, IImportAuthenticator, ICalendarTarget, IParticipantRenderer
│       ├── Core/Models/     ImportPayload, ImportPlanItem, ImportAction, ImportResult,
│       │                    CalendarTargetInfo, EventDraft, ExistingEventLookup, ParsedImportArguments
│       ├── Core/Services/   JsonImportSource, ImportPlanBuilder, ParticipantBodyRenderer,
│       │                    EventDraftBuilder, CalendarPicker
│       ├── Configuration/   ImportSettings, ImportSettingsResolver
│       ├── Infrastructure/Graph/  GraphAuthenticator (MSAL), GraphCalendarTarget (REST)
│       └── Presentation/    ApplicationRunner, ArgumentParser
│
└── tests/
    ├── ZyncMaster.Core.Tests/                  # Tests for shared library
    ├── ZyncMaster.CalExport.Tests/             # Tests for CalExport
    └── ZyncMaster.CalImport.Tests/             # Tests for CalImport
```

For a full architectural description of every CalExport class, see [WIKI.md](WIKI.md).

### Running the tests

```
dotnet test tests/ZyncMaster.CalExport.Tests/ZyncMaster.CalExport.Tests.csproj
```

Or from the solution root (builds and runs all test projects):

```
dotnet test
```

### Test coverage

The test suite contains **1340 unit tests** across seven projects (`ZyncMaster.Core.Tests` 67, `ZyncMaster.Graph.Tests` 111, `ZyncMaster.Engine.Tests` 106, `ZyncMaster.CalExport.Tests` 183, `ZyncMaster.CalImport.Tests` 104, `ZyncMaster.App.Tests` 145, `ZyncMaster.Server.Tests` 624) covering Core services, configuration, CLI parsing, export formatting, import planning, HTML body merge, the sync engine, the desktop app bridge and the server endpoints. Infrastructure wrappers around external systems (`OutlookCalendarService`, `PhysicalFileSystem`, `GraphAuthenticator`, `GraphCalendarTarget`) are excluded from unit tests by design — these require Outlook COM, the local filesystem, MSAL, or live Microsoft Graph, respectively.

To generate a coverage report, run the tests with the coverlet collector (configured centrally in `Directory.Build.props` and `coverage.runsettings`); it writes one `coverage.cobertura.xml` per test project under `./coverage/<guid>/` (targets 80%+ line coverage):

```
dotnet test --settings coverage.runsettings --results-directory ./coverage
```

---

## Dependencies

CalExport:

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.Office.Interop.Outlook` | 15.0.4797.1003 | Outlook COM interop |
| `Newtonsoft.Json` | 13.0.3 | JSON serialization |

CalImport:

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.Identity.Client` | 4.66.2 | MSAL — OAuth 2.0 public-client flow |
| `Microsoft.Identity.Client.Extensions.Msal` | 4.66.2 | DPAPI-encrypted token cache |
| `Newtonsoft.Json` | 13.0.3 | JSON serialization |

Test dependencies: xUnit 2.9, Moq 4.20, FluentAssertions 6.12, coverlet.collector 6.0.2.
