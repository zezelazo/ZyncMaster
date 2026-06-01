using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Server;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Registry of pluggable sync modules (Phase 4). Today only "calendar" registers; Phase 9
// modules plug in by registering another ISyncModule. These cover register + typed/generic
// lookup + the absent-module null contract.
public sealed class SyncModuleRegistryTests
{
    private sealed class StubCalendarModule : ICalendarSyncModule
    {
        public string ModuleId => CalendarSyncModule.Id;

        public Task<CalendarModuleResult> ExecuteAsync(
            SyncPair pair, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult(new CalendarModuleResult { Result = new MirrorResult() });
    }

    // A future, non-calendar module (e.g. files) used only to prove generic Get(id) routing.
    private sealed class StubFilesModule : ISyncModule
    {
        public string ModuleId => "files";
    }

    [Fact]
    public void GetCalendar_returns_the_registered_calendar_module()
    {
        var registry = new SyncModuleRegistry();
        var module = new StubCalendarModule();

        registry.Register(module);

        registry.GetCalendar().Should().BeSameAs(module);
    }

    [Fact]
    public void Get_returns_the_module_registered_under_its_id()
    {
        var registry = new SyncModuleRegistry();
        var calendar = new StubCalendarModule();
        var files = new StubFilesModule();
        registry.Register(calendar);
        registry.Register(files);

        registry.Get("calendar").Should().BeSameAs(calendar);
        registry.Get("files").Should().BeSameAs(files);
    }

    [Fact]
    public void Get_returns_null_for_an_unregistered_module_id()
    {
        var registry = new SyncModuleRegistry();
        registry.Register(new StubCalendarModule());

        registry.Get("clipboard").Should().BeNull();
    }

    [Fact]
    public void GetCalendar_returns_null_when_no_calendar_module_is_registered()
    {
        var registry = new SyncModuleRegistry();
        // Only a non-calendar module is present.
        registry.Register(new StubFilesModule());

        registry.GetCalendar().Should().BeNull();
    }

    [Fact]
    public void Register_replaces_a_module_registered_under_the_same_id()
    {
        var registry = new SyncModuleRegistry();
        var first = new StubCalendarModule();
        var second = new StubCalendarModule();

        registry.Register(first);
        registry.Register(second);

        registry.GetCalendar().Should().BeSameAs(second);
    }

    [Fact]
    public void Register_rejects_null()
    {
        var registry = new SyncModuleRegistry();
        Action act = () => registry.Register(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
