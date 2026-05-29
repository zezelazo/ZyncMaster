using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ZyncMaster.CalImport;
using ZyncMaster.Core;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.CalImport.Tests;

public sealed class ApplicationRunnerTests
{
    private readonly Mock<IConsoleIO>                          _console        = new Mock<IConsoleIO>();
    private readonly Mock<IApplicationTerminator>              _terminator     = new Mock<IApplicationTerminator>();
    private readonly Mock<IFileSystem>                         _fs             = new Mock<IFileSystem>();
    private readonly Mock<ISettingsRepository<ImportSettings>> _settingsRepo   = new Mock<ISettingsRepository<ImportSettings>>();
    private readonly Mock<IImportSource>                       _importSource   = new Mock<IImportSource>();
    private readonly Mock<ICalendarTarget>                     _calendarTarget = new Mock<ICalendarTarget>();

    private readonly ImportSettingsResolver _settingsResolver = new ImportSettingsResolver();
    private readonly ImportPlanBuilder      _planBuilder      = new ImportPlanBuilder();
    private readonly EventDraftBuilder      _draftBuilder     = new EventDraftBuilder(new ParticipantBodyRenderer());
    private readonly CalendarPicker         _calendarPicker;

    private sealed class TerminatedException : Exception
    {
        public int Code { get; }
        public TerminatedException(string m, int code) : base(m) { Code = code; }
    }

    public ApplicationRunnerTests()
    {
        _calendarPicker = new CalendarPicker(_console.Object, _terminator.Object);

        _terminator.Setup(t => t.Exit(It.IsAny<int>()))
                   .Callback<int>((c) => throw new TerminatedException($"Exit({c})", c));
        _terminator.Setup(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()))
                   .Callback<string, int>((m, c) => throw new TerminatedException(m, c));
    }

    private ApplicationRunner BuildSut() => new ApplicationRunner(
        _console.Object,
        _terminator.Object,
        _fs.Object,
        _settingsRepo.Object,
        _settingsResolver,
        _importSource.Object,
        _planBuilder,
        _draftBuilder,
        _calendarPicker,
        _ => _calendarTarget.Object,
        @"C:\exe");

    private static ImportSettings MakeSettings(string? defaultCalendarId = null)
        => new ImportSettings
        {
            DefaultCalendarId = defaultCalendarId,
            ReminderMinutes   = 30,
        };

    private static AppointmentRecord MakeRecord(
        string id,
        string subject = "Meeting",
        bool isCancelled = false,
        string description = "")
        => new AppointmentRecord
        {
            Id              = id,
            Subject         = subject,
            Start           = new DateTime(2026, 1, 15, 10, 0, 0),
            StartOffset     = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero),
            EndOffset       = new DateTimeOffset(2026, 1, 15, 11, 0, 0, TimeSpan.Zero),
            StartTimeZoneId = "UTC",
            Duration        = 60,
            IsCancelled     = isCancelled,
            Description     = description,
        };

    private static ImportPayload MakePayload(params AppointmentRecord[] records)
        => new ImportPayload
        {
            Year   = 2026,
            Month  = 1,
            Events = records,
        };

    private static CalendarTargetInfo MakeCal(string id = "cal-1", string name = "Cal", bool isDefault = true)
        => new CalendarTargetInfo { Id = id, DisplayName = name, IsDefault = isDefault, Owner = "user@example.com" };

    private void SetupCalendarsList(params CalendarTargetInfo[] calendars)
    {
        _calendarTarget
            .Setup(c => c.ListCalendarsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<CalendarTargetInfo>)calendars);
    }

    private void SetupNoExisting()
    {
        _calendarTarget
            .Setup(c => c.FindByExternalIdsAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, ExistingEventLookup>)new Dictionary<string, ExistingEventLookup>());
    }

    private void SetupExisting(params (string id, string eventId, string body)[] items)
    {
        var dict = new Dictionary<string, ExistingEventLookup>();
        foreach (var (id, eventId, body) in items)
            dict[id] = new ExistingEventLookup { Id = eventId, BodyHtml = body };
        _calendarTarget
            .Setup(c => c.FindByExternalIdsAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, ExistingEventLookup>)dict);
    }

    // ─── Config path explícito que no existe ──────────────────────────────────

    [Fact]
    public void LoadSettings_ExplicitConfigPathMissing_ExitsWithError()
    {
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);

        Action act = () => BuildSut().Run(new ParsedImportArguments { ConfigPath = "missing.json" });

        act.Should().Throw<TerminatedException>();
        _terminator.Verify(
            t => t.ExitWithError(It.Is<string>(s => s.Contains("Config file not found")), It.IsAny<int>()),
            Times.Once);
    }

    // ─── 3. Auto sin source ───────────────────────────────────────────────────

    [Fact]
    public void ResolveSourcePath_AutoWithoutSource_ExitsWithError()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());

        Action act = () => BuildSut().Run(new ParsedImportArguments { AutoMode = true });

        act.Should().Throw<TerminatedException>();
        _terminator.Verify(
            t => t.ExitWithError(It.Is<string>(s => s.Contains("--auto") && s.Contains("--source")), It.IsAny<int>()),
            Times.Once);
    }

    // ─── 4. Source path en arg ────────────────────────────────────────────────

    [Fact]
    public void ResolveSourcePath_ExplicitSource_FlowContinuesToLoad()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        var rec = MakeRecord("id-1");
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(rec));
        SetupCalendarsList(MakeCal());
        SetupNoExisting();
        _calendarTarget
            .Setup(c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("new-event-id");

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _importSource.Verify(s => s.Load(It.Is<string>(p => p.EndsWith("x.json"))), Times.Once);
    }

    // ─── 5. Source path por prompt ────────────────────────────────────────────

    [Fact]
    public void ResolveSourcePath_PromptValid_ContinuesFlow()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _console.SetupSequence(c => c.ReadLine())
                .Returns("path-from-prompt.json")
                .Returns("1");
        var rec = MakeRecord("id-1");
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(rec));
        SetupCalendarsList(MakeCal());
        SetupNoExisting();
        _calendarTarget
            .Setup(c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("new-event-id");

        Action act = () => BuildSut().Run(new ParsedImportArguments());

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _importSource.Verify(
            s => s.Load(It.Is<string>(p => p.EndsWith("path-from-prompt.json"))),
            Times.Once);
    }

    // ─── 6. Source vacío del prompt ───────────────────────────────────────────

    [Fact]
    public void ResolveSourcePath_PromptEmpty_ExitsWithError()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _console.Setup(c => c.ReadLine()).Returns("");

        Action act = () => BuildSut().Run(new ParsedImportArguments());

        act.Should().Throw<TerminatedException>();
        _terminator.Verify(
            t => t.ExitWithError(It.Is<string>(s => s.Contains("Source path")), It.IsAny<int>()),
            Times.Once);
    }

    // ─── 7. ImportSourceException durante Load ───────────────────────────────

    [Fact]
    public void LoadPayload_ImportSourceException_ExitsWithError()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>()))
                     .Throws(new ImportSourceException("bad json"));

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<TerminatedException>();
        _terminator.Verify(
            t => t.ExitWithError(It.Is<string>(s => s.Contains("Error reading source file") && s.Contains("bad json")), It.IsAny<int>()),
            Times.Once);
    }

    // ─── 8. --new-calendar crea calendario ───────────────────────────────────

    [Fact]
    public void ResolveCalendarTarget_NewCalendarName_CallsCreateCalendar()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload());

        var created = new CalendarTargetInfo { Id = "new-id", DisplayName = "MyNew", IsDefault = false, Owner = "u" };
        _calendarTarget
            .Setup(c => c.CreateCalendarAsync("MyNew", It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        _calendarTarget
            .Setup(c => c.FindByExternalIdsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ExistingEventLookup>());

        Action act = () => BuildSut().Run(new ParsedImportArguments
        {
            SourcePath      = "x.json",
            AutoMode        = true,
            NewCalendarName = "MyNew",
        });

        // Payload empty + everything skipped → exit 3
        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(3);
        _calendarTarget.Verify(c => c.CreateCalendarAsync("MyNew", It.IsAny<CancellationToken>()), Times.Once);
        _calendarTarget.Verify(c => c.ListCalendarsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── 9. --calendar id pasa por picker ────────────────────────────────────

    [Fact]
    public void ResolveCalendarTarget_ExplicitCalendarId_ListsAndResolves()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        SetupCalendarsList(MakeCal("abc", "ABC", true), MakeCal("xyz", "XYZ", false));
        SetupNoExisting();
        _calendarTarget
            .Setup(c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ev-id");

        Action act = () => BuildSut().Run(new ParsedImportArguments
        {
            SourcePath = "x.json",
            AutoMode   = true,
            CalendarId = "abc",
        });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(c => c.ListCalendarsAsync(It.IsAny<CancellationToken>()), Times.Once);
        _calendarTarget.Verify(
            c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── 10. Interactivo: ReadLine="1" elige primer calendario ───────────────

    [Fact]
    public void ResolveCalendarTarget_Interactive_PicksFromPrompt()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        // ReadLine 1: source path. ReadLine 2: calendar choice.
        _console.SetupSequence(c => c.ReadLine())
                .Returns("x.json")
                .Returns("1");
        SetupCalendarsList(MakeCal("abc", "ABC", true), MakeCal("xyz", "XYZ", false));
        SetupNoExisting();
        _calendarTarget
            .Setup(c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ev-id");

        Action act = () => BuildSut().Run(new ParsedImportArguments());

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(
            c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── 11. Dry-run ────────────────────────────────────────────────────────

    [Fact]
    public void DryRun_ReportsButDoesNotMutate_Exit0()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        SetupCalendarsList(MakeCal());
        SetupNoExisting();

        Action act = () => BuildSut().Run(new ParsedImportArguments
        {
            SourcePath = "x.json",
            AutoMode   = true,
            DryRun     = true,
        });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(
            c => c.FindByExternalIdsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _calendarTarget.Verify(
            c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _calendarTarget.Verify(
            c => c.UpdateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _calendarTarget.Verify(
            c => c.DeleteEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("dry-run"))), Times.Once);
    }

    // ─── 12. Plan ejecutado todo Create exitoso ──────────────────────────────

    [Fact]
    public void ExecutePlan_AllCreate_Success_Exit0()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>()))
                     .Returns(MakePayload(MakeRecord("id-1"), MakeRecord("id-2")));
        SetupCalendarsList(MakeCal());
        SetupNoExisting();
        _calendarTarget
            .Setup(c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ev-id");

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(
            c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ─── 13. Plan con Update ─────────────────────────────────────────────────

    [Fact]
    public void ExecutePlan_WithUpdate_CallsUpdate()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        SetupCalendarsList(MakeCal());
        SetupExisting(("id-1", "graph-event-id", "<p>existing</p>"));
        _calendarTarget
            .Setup(c => c.UpdateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(
            c => c.UpdateEventAsync("graph-event-id", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── 14. Plan con Cancel ─────────────────────────────────────────────────

    [Fact]
    public void ExecutePlan_WithCancel_CallsDelete()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>()))
                     .Returns(MakePayload(MakeRecord("id-1", isCancelled: true)));
        SetupCalendarsList(MakeCal());
        SetupExisting(("id-1", "graph-event-id", ""));
        _calendarTarget
            .Setup(c => c.DeleteEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(
            c => c.DeleteEventAsync("graph-event-id", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── 15. Plan con Skip ───────────────────────────────────────────────────

    [Fact]
    public void ExecutePlan_WithSkipOnly_NoGraphCalls_Exit3()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>()))
                     .Returns(MakePayload(MakeRecord("id-1", isCancelled: true)));
        SetupCalendarsList(MakeCal());
        SetupNoExisting(); // cancelled + no existing → skip

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(3);
        _calendarTarget.Verify(
            c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _calendarTarget.Verify(
            c => c.UpdateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _calendarTarget.Verify(
            c => c.DeleteEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _console.Verify(c => c.WriteError(It.Is<string>(s => s.Contains("WARNING") && s.Contains("No changes applied"))), Times.Once);
    }

    // ─── 16. Plan con un Failed (GraphRequestException) ─────────────────────

    [Fact]
    public void ExecutePlan_PerItemGraphRequestException_RecordsFailure_Exit2()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>()))
                     .Returns(MakePayload(MakeRecord("id-1"), MakeRecord("id-2")));
        SetupCalendarsList(MakeCal());
        SetupNoExisting();
        _calendarTarget
            .SetupSequence(c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GraphRequestException("HTTP 404"))
            .ReturnsAsync("ev-2");

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(2);
        _calendarTarget.Verify(
            c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("FAIL") && s.Contains("HTTP 404"))), Times.Once);
    }

    // ─── 17. AuthenticationFailedException propaga ───────────────────────────

    [Fact]
    public void ExecutePlan_AuthenticationFailedException_Propagates()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>()))
                     .Returns(MakePayload(MakeRecord("id-1"), MakeRecord("id-2")));
        SetupCalendarsList(MakeCal());
        SetupNoExisting();
        _calendarTarget
            .Setup(c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationFailedException("token revoked"));

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<AuthenticationFailedException>().WithMessage("*token revoked*");
        // Only the first item is attempted before the auth failure aborts the loop.
        _calendarTarget.Verify(
            c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── 18. OperationCanceledException propaga ──────────────────────────────

    [Fact]
    public void ExecutePlan_OperationCanceledException_Propagates()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        SetupCalendarsList(MakeCal());
        SetupNoExisting();
        _calendarTarget
            .Setup(c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("user ctrl-c"));

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<OperationCanceledException>();
    }

    // ─── 19. Todo Skip → Exit(3) con warning (cubierto por test 15) ─────────
    //          se mantiene este test explícito que valida el mensaje exacto.

    [Fact]
    public void ExecutePlan_AllSkip_PrintsWarningAndExits3()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>()))
                     .Returns(MakePayload(
                         MakeRecord("id-1", isCancelled: true),
                         MakeRecord("id-2", isCancelled: true)));
        SetupCalendarsList(MakeCal());
        SetupNoExisting();

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        var ex = act.Should().Throw<TerminatedException>().Which;
        ex.Code.Should().Be(3);
        _console.Verify(
            c => c.WriteError(It.Is<string>(s => s.Contains("Source had 2 event"))),
            Times.Once);
    }

    // ─── 20. defaultCalendarId en settings en auto ───────────────────────────

    [Fact]
    public void DefaultCalendarIdInSettings_AutoMode_Honored()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(defaultCalendarId: "abc"));
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        SetupCalendarsList(MakeCal("zzz", "ZZZ", true), MakeCal("abc", "ABC", false));
        SetupNoExisting();
        _calendarTarget
            .Setup(c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ev-id");

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(
            c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── Adicionales ─────────────────────────────────────────────────────────

    [Fact]
    public void Run_NullArgs_ThrowsArgumentNullException()
    {
        Action act = () => BuildSut().Run(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        Action act1 = () => new ApplicationRunner(
            null!, _terminator.Object, _fs.Object, _settingsRepo.Object, _settingsResolver,
            _importSource.Object, _planBuilder, _draftBuilder, _calendarPicker,
            _ => _calendarTarget.Object, "exe");
        act1.Should().Throw<ArgumentNullException>();

        Action act2 = () => new ApplicationRunner(
            _console.Object, null!, _fs.Object, _settingsRepo.Object, _settingsResolver,
            _importSource.Object, _planBuilder, _draftBuilder, _calendarPicker,
            _ => _calendarTarget.Object, "exe");
        act2.Should().Throw<ArgumentNullException>();

        Action act3 = () => new ApplicationRunner(
            _console.Object, _terminator.Object, _fs.Object, _settingsRepo.Object, _settingsResolver,
            _importSource.Object, _planBuilder, _draftBuilder, _calendarPicker,
            null!, "exe");
        act3.Should().Throw<ArgumentNullException>();

        Action act4 = () => new ApplicationRunner(
            _console.Object, _terminator.Object, _fs.Object, _settingsRepo.Object, _settingsResolver,
            _importSource.Object, _planBuilder, _draftBuilder, _calendarPicker,
            _ => _calendarTarget.Object, null!);
        act4.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void MixedPlan_CreateUpdateCancelSkip_AllProcessed_Exit0()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>()))
                     .Returns(MakePayload(
                         MakeRecord("new-id"),                                  // Create
                         MakeRecord("upd-id"),                                  // Update
                         MakeRecord("cnc-id", isCancelled: true),               // Cancel
                         MakeRecord("skp-id", isCancelled: true)));             // Skip
        SetupCalendarsList(MakeCal());
        SetupExisting(
            ("upd-id", "ev-upd", "<p>old</p>"),
            ("cnc-id", "ev-cnc", ""));
        _calendarTarget
            .Setup(c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("new-ev");
        _calendarTarget
            .Setup(c => c.UpdateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _calendarTarget
            .Setup(c => c.DeleteEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(c => c.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()), Times.Once);
        _calendarTarget.Verify(c => c.UpdateEventAsync("ev-upd", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()), Times.Once);
        _calendarTarget.Verify(c => c.DeleteEventAsync("ev-cnc", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Export-style settings flow (show / confirm / save) ──────────────────

    [Fact]
    public void Interactive_ValidDefault_ProceedYes_UsesDefaultWithoutPicker()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(defaultCalendarId: "abc"));
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        // ReadLine 1: source path. ReadLine 2: "Proceed?" → empty = yes.
        _console.SetupSequence(c => c.ReadLine()).Returns("x.json").Returns("");
        SetupCalendarsList(MakeCal("zzz", "ZZZ", true), MakeCal("abc", "ABC", false));
        SetupNoExisting();
        _calendarTarget.Setup(c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync("ev");

        Action act = () => BuildSut().Run(new ParsedImportArguments());

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()), Times.Once);
        _settingsRepo.Verify(r => r.Save(It.IsAny<ImportSettings>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Interactive_ValidDefault_ProceedNo_FallsToPicker()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(defaultCalendarId: "abc"));
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        // source, Proceed=n, picker choice=2, reminder(empty), save(empty)
        _console.SetupSequence(c => c.ReadLine())
                .Returns("x.json").Returns("n").Returns("2").Returns("").Returns("");
        SetupCalendarsList(MakeCal("abc", "ABC", true), MakeCal("xyz", "XYZ", false));
        SetupNoExisting();
        _calendarTarget.Setup(c => c.CreateEventAsync("xyz", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync("ev");

        Action act = () => BuildSut().Run(new ParsedImportArguments());

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(c => c.CreateEventAsync("xyz", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Interactive_SaveDefaultsYes_PersistsCalendarAndReminder()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        // source, picker=1, reminder=15, overwrite=n, save=y
        _console.SetupSequence(c => c.ReadLine())
                .Returns("x.json").Returns("1").Returns("15").Returns("n").Returns("y");
        SetupCalendarsList(MakeCal("abc", "ABC", true));
        SetupNoExisting();
        _calendarTarget.Setup(c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync("ev");

        Action act = () => BuildSut().Run(new ParsedImportArguments());

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _settingsRepo.Verify(r => r.Save(
            It.Is<ImportSettings>(s => s.DefaultCalendarId == "abc" && s.ReminderMinutes == 15),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Interactive_SaveDefaultsNo_DoesNotPersist()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        // source, picker=1, reminder(empty), save=n
        _console.SetupSequence(c => c.ReadLine())
                .Returns("x.json").Returns("1").Returns("").Returns("n");
        SetupCalendarsList(MakeCal("abc", "ABC", true));
        SetupNoExisting();
        _calendarTarget.Setup(c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync("ev");

        Action act = () => BuildSut().Run(new ParsedImportArguments());

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _settingsRepo.Verify(r => r.Save(It.IsAny<ImportSettings>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Interactive_DefaultIdNotFound_WarnsAndFallsToPicker()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(defaultCalendarId: "missing"));
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        // source, picker=1, reminder(empty), save(empty)
        _console.SetupSequence(c => c.ReadLine())
                .Returns("x.json").Returns("1").Returns("").Returns("");
        SetupCalendarsList(MakeCal("abc", "ABC", true));
        SetupNoExisting();
        _calendarTarget.Setup(c => c.CreateEventAsync("abc", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync("ev");

        Action act = () => BuildSut().Run(new ParsedImportArguments());

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _console.Verify(
            c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("WARNING") && s.Contains("missing"))),
            Times.Once);
    }

    [Fact]
    public void Interactive_CreateNewFromPicker_CreatesAndSavesAsDefault()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>())).Returns(MakePayload(MakeRecord("id-1")));
        // source, picker=N, new name, reminder(empty), overwrite(empty), save=y
        _console.SetupSequence(c => c.ReadLine())
                .Returns("x.json").Returns("N").Returns("MyNewCal").Returns("").Returns("").Returns("y");
        SetupCalendarsList(MakeCal("abc", "ABC", true));
        SetupNoExisting();
        var created = new CalendarTargetInfo { Id = "new-id", DisplayName = "MyNewCal", Owner = "u" };
        _calendarTarget.Setup(c => c.CreateCalendarAsync("MyNewCal", It.IsAny<CancellationToken>()))
                       .ReturnsAsync(created);
        _calendarTarget.Setup(c => c.CreateEventAsync("new-id", It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync("ev");

        Action act = () => BuildSut().Run(new ParsedImportArguments());

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        _calendarTarget.Verify(c => c.CreateCalendarAsync("MyNewCal", It.IsAny<CancellationToken>()), Times.Once);
        _settingsRepo.Verify(r => r.Save(
            It.Is<ImportSettings>(s => s.DefaultCalendarId == "new-id"),
            It.IsAny<string>()), Times.Once);
    }

    // ─── Overwrite (--overwrite) ─────────────────────────────────────────────

    [Fact]
    public void ExecutePlan_Overwrite_RebuildsBodyFromFile()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>()))
                     .Returns(MakePayload(MakeRecord("id-1", description: "NEW DESC FROM FILE")));
        SetupCalendarsList(MakeCal());
        SetupExisting(("id-1", "graph-ev", "<p>OLD USER EDIT</p>"));
        EventDraft? captured = null;
        _calendarTarget
            .Setup(c => c.UpdateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .Callback<string, EventDraft, CancellationToken>((_, d, _) => captured = d)
            .Returns(Task.CompletedTask);

        Action act = () => BuildSut().Run(new ParsedImportArguments
        {
            SourcePath = "x.json",
            AutoMode   = true,
            Overwrite  = true,
        });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.BodyHtml.Should().Contain("NEW DESC FROM FILE");
        captured.BodyHtml.Should().NotContain("OLD USER EDIT");
    }

    [Fact]
    public void ExecutePlan_NoOverwrite_PreservesExistingBody()
    {
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _importSource.Setup(s => s.Load(It.IsAny<string>()))
                     .Returns(MakePayload(MakeRecord("id-1", description: "NEW DESC FROM FILE")));
        SetupCalendarsList(MakeCal());
        SetupExisting(("id-1", "graph-ev", "<p>OLD USER EDIT</p>"));
        EventDraft? captured = null;
        _calendarTarget
            .Setup(c => c.UpdateEventAsync(It.IsAny<string>(), It.IsAny<EventDraft>(), It.IsAny<CancellationToken>()))
            .Callback<string, EventDraft, CancellationToken>((_, d, _) => captured = d)
            .Returns(Task.CompletedTask);

        Action act = () => BuildSut().Run(new ParsedImportArguments { SourcePath = "x.json", AutoMode = true });

        act.Should().Throw<TerminatedException>().Which.Code.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.BodyHtml.Should().Contain("OLD USER EDIT");
        captured.BodyHtml.Should().NotContain("NEW DESC FROM FILE");
    }
}
