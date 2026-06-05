# ZyncMaster.CalImport — standalone tool (not on the primary sync path)

This project is a **standalone / legacy** console tool. It is built and tested by the solution, but
**nothing in the live calendar-sync path references it.** It is kept because it works on its own and
its test suite (`ZyncMaster.CalImport.Tests`) carries valuable coverage. This note exists so the
project does not disorient anyone reading the solution.

## What it does

Reads a Complete-mode JSON export and creates / updates / deletes events on a target Microsoft
account through the Microsoft Graph API. The upsert is idempotent, keyed on a stable per-event id;
cancellations map to deletes; source participants are rendered into the event body. It is a
self-contained **JSON -> Graph importer**.

## Why it is NOT the production sync path

Production sync mirrors events directly:

```
Desktop App  ->  Engine  ->  Microsoft Graph        (device-driven sync)
Server       ->  Graph                              (cloud fallback / server-side sync)
```

The live path never round-trips through a CalImport JSON file. If you are tracing how an event gets
synced for an end user, follow the App / Server path above and ignore this project.

See also `../ZyncMaster.Cli/README.md` for the other standalone tool.
