namespace ZyncMaster.Server;

// A pluggable sync module. Each module owns the execution of ONE kind of payload (calendar
// today; files / clipboard in the future) for a single pair. The pair carries an implicit
// "module" — calendar — until other kinds exist; the registry maps a module id to its
// implementation so adding a new kind is "new module + tile in the UI", not a rewrite of the
// run engine. The destructive run-lock and the no-server-reader routing stay in the endpoint;
// a module only performs the read + mirror for its own kind.
public interface ISyncModule
{
    // Stable identifier for the kind of sync this module performs (e.g. "calendar").
    string ModuleId { get; }
}
