namespace ZyncMaster.Server;

// Registry of the available sync modules, keyed on ModuleId. Today only "calendar" is
// registered; Phase 9 modules (files, clipboard, …) plug in by registering another ISyncModule
// here — the run engine resolves a pair's module by id and never needs to grow a switch.
public sealed class SyncModuleRegistry
{
    private readonly Dictionary<string, ISyncModule> _modules = new(StringComparer.Ordinal);

    // Registers (or replaces) the module for its ModuleId.
    public void Register(ISyncModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(module.ModuleId))
            throw new ArgumentException("ModuleId is required.", nameof(module));
        _modules[module.ModuleId] = module;
    }

    // The module registered under the given id, or null when none is registered. Generic
    // lookup the future run engine uses to dispatch a pair to its module.
    public ISyncModule? Get(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        return _modules.TryGetValue(moduleId, out var module) ? module : null;
    }

    // Convenience accessor for the calendar module, or null when none is registered. Returns
    // it already typed as ICalendarSyncModule so callers can ExecuteAsync without a cast.
    public ICalendarSyncModule? GetCalendar()
        => Get(CalendarSyncModule.Id) as ICalendarSyncModule;
}
