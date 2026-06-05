# ZyncMaster.Cli — standalone tool (not on the primary sync path)

This project is a **standalone / developer** console tool. It is built by the solution, but
**nothing in the shipping product references it.** It is kept for local experimentation and because
it exercises the Engine end to end. This note exists so the project does not disorient anyone
reading the solution.

## What it does

A console **sync host** with three modes:

- `--pair`   — run device pairing, then exit.
- `--once`   — run a single sync cycle, then exit.
- (default)  — pair if needed, then loop forever.

Overrides: `--interval <minutes>`, `--server <url>`. It drives `ZyncMaster.Engine` directly against
a server URL — a developer / standalone runner, not the production client.

## Why it is NOT the production sync path

The production client is the **desktop App** (`ZyncMaster.App`), which drives sync for end users:

```
Desktop App  ->  Engine  ->  Microsoft Graph        (device-driven sync)
Server       ->  Graph                              (cloud fallback / server-side sync)
```

If you are tracing how a calendar event gets synced in production, follow the App / Server path
above and ignore this project.

See also `../ZyncMaster.CalImport/README.md` for the other standalone tool.
