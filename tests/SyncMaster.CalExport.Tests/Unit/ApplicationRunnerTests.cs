using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Moq;
using Newtonsoft.Json.Linq;
using SyncMaster.CalExport;
using SyncMaster.Core;
using Xunit;

namespace SyncMaster.CalExport.Tests;

public sealed class ApplicationRunnerTests
{
    // ── Sentinel exception so terminator calls are observable in tests ──
    private sealed class TerminatedException : Exception
    {
        public int Code { get; }
        public TerminatedException(string? message = null, int code = 0) : base(message ?? "terminated")
        {
            Code = code;
        }
    }

    private readonly Mock<IConsoleIO>                       _console        = new Mock<IConsoleIO>();
    private readonly Mock<ICalendarService>                 _calSvc         = new Mock<ICalendarService>();
    private readonly Mock<ISettingsRepository<AppSettings>> _settingsRepo   = new Mock<ISettingsRepository<AppSettings>>();
    private readonly Mock<IFileSystem>                      _fs             = new Mock<IFileSystem>();
    private readonly Mock<IAppointmentExporter>             _exporter       = new Mock<IAppointmentExporter>();
    private readonly Mock<IApplicationTerminator>           _terminator     = new Mock<IApplicationTerminator>();

    private const string ExeDir = "C:\\app";

    public ApplicationRunnerTests()
    {
        // Default: terminator throws so tests can assert on it & abort execution.
        _terminator.Setup(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()))
                   .Callback<string, int>((m, c) => throw new TerminatedException(m, c));
        _terminator.Setup(t => t.Exit(It.IsAny<int>()))
                   .Callback<int>(c => throw new TerminatedException(null, c));

        // Default exporter contract so the export pipeline can complete.
        _exporter.Setup(e => e.FileSuffix).Returns("simple");
        _exporter.Setup(e => e.FileExtension).Returns("txt");
        _exporter.Setup(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()))
                 .Returns("payload");
    }

    private ApplicationRunner BuildSut()
    {
        var resolver        = new SettingsResolver();
        var folderMatcher   = new CalendarFolderMatcher();
        var outputDirSvc    = new OutputDirectoryService(_fs.Object, _console.Object, _terminator.Object);
        var exportService   = new AppointmentExportService(
            _calSvc.Object,
            _ => _exporter.Object,
            _fs.Object,
            _console.Object);

        return new ApplicationRunner(
            _console.Object,
            _calSvc.Object,
            _settingsRepo.Object,
            _fs.Object,
            resolver,
            folderMatcher,
            outputDirSvc,
            exportService,
            _terminator.Object,
            ExeDir);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static AppSettings MakeSettings(
        JToken?  year             = null,
        JToken?  month            = null,
        string   mode             = "simple",
        bool     inclCancelled    = false,
        JToken?  calendars        = null,
        string?  outputPath       = null) =>
        new AppSettings
        {
            Year             = year      ?? new JValue(2025),
            Month            = month     ?? new JValue(6),
            Mode             = mode,
            IncludeCancelled = inclCancelled,
            Calendars        = calendars ?? new JValue("all"),
            OutputPath       = outputPath,
        };

    private static ParsedArguments MakeArgs(
        bool    autoMode   = false,
        string? configPath = null,
        string? outputPath = null) =>
        new ParsedArguments { AutoMode = autoMode, ConfigPath = configPath, OutputPath = outputPath };

    private static List<CalendarFolderInfo> MakeFolders(params string[] names) =>
        names.Select((n, i) => new CalendarFolderInfo
        {
            DisplayName = n,
            EntryId     = $"entry-{i}",
            StoreId     = $"store-{i}",
        }).ToList();

    /// <summary>Configures the standard "everything happy" setup so Run() reaches DoExport.</summary>
    private void SetupHappyFs(string outDir = ExeDir)
    {
        _fs.Setup(f => f.DirectoryExists(outDir)).Returns(true);
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(true);
    }

    // ─────────────────────────────────────────────────────────────────────
    // RunAutoMode
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AutoMode_LoadsDefaults_RunsExport()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(year: new JValue(2024), month: new JValue(3)));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        var sut = BuildSut();
        sut.Run(MakeArgs(autoMode: true));

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p =>
            p.Year == 2024 && p.Month == 3)), Times.Once);
        _fs.Verify(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()), Times.Once);
    }

    [Fact]
    public void AutoMode_AllCalendars_PassesNullSelectedFolders()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(calendars: new JValue("all")));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        BuildSut().Run(MakeArgs(autoMode: true));

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p => p.SelectedFolders == null)), Times.Once);
        // Should not ask Outlook for folder list when calendars == "all".
        _calSvc.Verify(c => c.GetCalendarFolders(), Times.Never);
    }

    [Fact]
    public void AutoMode_NamedCalendar_ResolvesViaICalendarService()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(calendars: JArray.FromObject(new[] { "Work" })));
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("Personal", "Work"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        BuildSut().Run(MakeArgs(autoMode: true));

        _calSvc.Verify(c => c.GetCalendarFolders(), Times.Once);
        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p =>
            p.SelectedFolders != null &&
            p.SelectedFolders.Count == 1 &&
            p.SelectedFolders[0].DisplayName == "Work")), Times.Once);
    }

    [Fact]
    public void AutoMode_PendingCreatePath_WritesNewSettingsSilently()
    {
        SetupHappyFs();
        var pendingPath = "C:\\custom\\settings.json";
        _fs.Setup(f => f.FileExists(It.Is<string>(s => s.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase))))
           .Returns(false);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        BuildSut().Run(MakeArgs(autoMode: true, configPath: pendingPath));

        // In auto + pending: must write settings without prompting.
        _settingsRepo.Verify(r => r.Save(It.IsAny<AppSettings>(), It.Is<string>(p => p.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase))), Times.Once);
        // ReadLine never called in auto mode.
        _console.Verify(c => c.ReadLine(), Times.Never);
    }

    [Fact]
    public void AutoMode_ExportThrows_ExitsWithError()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Throws(new InvalidOperationException("boom"));

        Action act = () => BuildSut().Run(MakeArgs(autoMode: true));

        act.Should().Throw<TerminatedException>().Which.Message.Should().Contain("boom");
        _terminator.Verify(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void Auto_CustomOutputPath_OverridesSettings()
    {
        _fs.Setup(f => f.DirectoryExists("C:\\custom-out")).Returns(true);
        _fs.Setup(f => f.DirectoryExists(It.Is<string>(s => s != "C:\\custom-out"))).Returns(true);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(outputPath: "C:\\from-settings"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        string? writtenPath = null;
        _fs.Setup(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()))
           .Callback<string, string, Encoding>((p, _, _) => writtenPath = p);

        BuildSut().Run(MakeArgs(autoMode: true, outputPath: "C:\\custom-out"));

        writtenPath.Should().NotBeNull();
        writtenPath!.Should().StartWith("C:\\custom-out");
    }

    [Fact]
    public void Auto_NoOutputPath_UsesExeDir()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(outputPath: null));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        string? writtenPath = null;
        _fs.Setup(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()))
           .Callback<string, string, Encoding>((p, _, _) => writtenPath = p);

        BuildSut().Run(MakeArgs(autoMode: true));

        writtenPath.Should().NotBeNull();
        writtenPath!.Should().StartWith(ExeDir);
    }

    // ─────────────────────────────────────────────────────────────────────
    // RunNewConfigFlow
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewConfig_UserConfirmsDefaults_RunsExport()
    {
        SetupHappyFs();
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        // Prompts: "Start with defaults? Y", "These look good? Y", "Save to pending? N"
        _console.SetupSequence(c => c.ReadLine())
                .Returns("")     // start defaults: Y (empty => default yes)
                .Returns("")     // defaults look good: Y
                .Returns("n");   // don't save pending file

        BuildSut().Run(MakeArgs(configPath: "C:\\missing\\settings.json"));

        _calSvc.Verify(c => c.GetAppointments(It.IsAny<ExportParameters>()), Times.Once);
        _settingsRepo.Verify(r => r.Save(It.IsAny<AppSettings>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void NewConfig_UserRejectsDefaults_GoesInteractive()
    {
        SetupHappyFs();
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("CalA"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n")      // Start with defaults? -> No
                .Returns("0")      // All calendars
                .Returns("2")      // year: current
                .Returns("6")      // month
                .Returns("1")      // simple
                .Returns("1")      // exclude cancelled
                .Returns("n");     // don't save pending file

        BuildSut().Run(MakeArgs(configPath: "C:\\missing\\settings.json"));

        _calSvc.Verify(c => c.GetCalendarFolders(), Times.Once);
        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p =>
            p.Year == DateTime.Today.Year && p.Month == 6 && p.Mode == ExportMode.Simple)), Times.Once);
    }

    [Fact]
    public void NewConfig_AskCreateFile_YesSavesFile()
    {
        SetupHappyFs();
        var pendingPath = "C:\\new\\settings.json";
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("")        // start with defaults: Y
                .Returns("")        // defaults look good: Y
                .Returns("y")       // save to pending file? Y
                .Returns("1");      // month save mode: fixed

        BuildSut().Run(MakeArgs(configPath: pendingPath));

        _settingsRepo.Verify(r => r.Save(It.IsAny<AppSettings>(), It.Is<string>(p => p == Path.GetFullPath(pendingPath))), Times.Once);
    }

    [Fact]
    public void NewConfig_AskCreateFile_NoSkipsSave()
    {
        SetupHappyFs();
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("")     // start defaults: Y
                .Returns("")     // looks good: Y
                .Returns("n");   // save pending: N

        BuildSut().Run(MakeArgs(configPath: "C:\\new\\settings.json"));

        _settingsRepo.Verify(r => r.Save(It.IsAny<AppSettings>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void NewConfig_PromptMonthSaveMode_FixedValue()
    {
        SetupHappyFs();
        var pendingPath = "C:\\new\\settings.json";
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(month: new JValue(4)));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("").Returns("").Returns("y").Returns("1");

        AppSettings? saved = null;
        _settingsRepo.Setup(r => r.Save(It.IsAny<AppSettings>(), It.IsAny<string>()))
                     .Callback<AppSettings, string>((s, _) => saved = s);

        BuildSut().Run(MakeArgs(configPath: pendingPath));

        saved.Should().NotBeNull();
        saved!.Month.Type.Should().Be(JTokenType.Integer);
        saved.Month.Value<int>().Should().Be(4);
    }

    [Fact]
    public void NewConfig_PromptMonthSaveMode_Current()
    {
        SetupHappyFs();
        var pendingPath = "C:\\new\\settings.json";
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("").Returns("").Returns("y").Returns("2");

        AppSettings? saved = null;
        _settingsRepo.Setup(r => r.Save(It.IsAny<AppSettings>(), It.IsAny<string>()))
                     .Callback<AppSettings, string>((s, _) => saved = s);

        BuildSut().Run(MakeArgs(configPath: pendingPath));

        saved.Should().NotBeNull();
        saved!.Month.Value<string>().Should().Be("current");
    }

    [Fact]
    public void NewConfig_PromptMonthSaveMode_Previous()
    {
        SetupHappyFs();
        var pendingPath = "C:\\new\\settings.json";
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("").Returns("").Returns("y").Returns("3");

        AppSettings? saved = null;
        _settingsRepo.Setup(r => r.Save(It.IsAny<AppSettings>(), It.IsAny<string>()))
                     .Callback<AppSettings, string>((s, _) => saved = s);

        BuildSut().Run(MakeArgs(configPath: pendingPath));

        saved.Should().NotBeNull();
        saved!.Month.Value<string>().Should().Be("previous");
    }

    // ─────────────────────────────────────────────────────────────────────
    // RunNormalFlow
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Normal_UserConfirmsDefaults_RunsExport()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(year: new JValue(2025), month: new JValue(7)));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine()).Returns("");   // Proceed? Y

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p =>
            p.Year == 2025 && p.Month == 7)), Times.Once);
        // No save was triggered because user accepted defaults silently.
        _settingsRepo.Verify(r => r.Save(It.IsAny<AppSettings>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Normal_UserRejectsDefaults_GoesInteractive()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("CalA", "CalB"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n")    // Proceed? -> No
                .Returns("1")    // calendar selection: CalA
                .Returns("1")    // year: previous
                .Returns("3")    // month
                .Returns("2")    // mode: Complete
                .Returns("2")    // include cancelled: Yes
                .Returns("n");   // Save defaults? -> No

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p =>
            p.Year == DateTime.Today.Year - 1 &&
            p.Month == 3 &&
            p.Mode == ExportMode.Complete &&
            p.IncludeCancelled == true &&
            p.SelectedFolders != null &&
            p.SelectedFolders.Count == 1 &&
            p.SelectedFolders[0].DisplayName == "CalA")), Times.Once);
    }

    [Fact]
    public void Normal_AskSaveDefaults_Yes_SavesSettings()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("CalA"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n")    // proceed with defaults? -> No (go interactive)
                .Returns("0")    // all calendars
                .Returns("2")    // year: current
                .Returns("5")    // month
                .Returns("1")    // simple
                .Returns("1")    // exclude cancelled
                .Returns("y")    // save defaults? Yes
                .Returns("1");   // month save mode: fixed

        BuildSut().Run(MakeArgs());

        _settingsRepo.Verify(r => r.Save(It.IsAny<AppSettings>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Normal_AskSaveDefaults_No_DoesNotSave()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("CalA"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n")    // proceed? No
                .Returns("0").Returns("2").Returns("5").Returns("1").Returns("1")
                .Returns("n");   // save defaults? No

        BuildSut().Run(MakeArgs());

        _settingsRepo.Verify(r => r.Save(It.IsAny<AppSettings>(), It.IsAny<string>()), Times.Never);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Individual prompts (via RunNormalFlow path)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PromptYear_Choice1_ReturnsPrevious()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("1").Returns("5").Returns("1").Returns("1").Returns("n");

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p => p.Year == DateTime.Today.Year - 1)), Times.Once);
    }

    [Fact]
    public void PromptYear_Choice2_ReturnsCurrent()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("2").Returns("5").Returns("1").Returns("1").Returns("n");

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p => p.Year == DateTime.Today.Year)), Times.Once);
    }

    [Fact]
    public void PromptYear_Invalid_Terminates()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("99");

        Action act = () => BuildSut().Run(MakeArgs());

        act.Should().Throw<TerminatedException>().Which.Message.Should().Contain("Invalid");
    }

    [Fact]
    public void PromptMonth_Valid_Returns()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("2").Returns("11").Returns("1").Returns("1").Returns("n");

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p => p.Month == 11)), Times.Once);
    }

    [Fact]
    public void PromptMonth_Invalid_Terminates()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("2").Returns("13");

        Action act = () => BuildSut().Run(MakeArgs());

        act.Should().Throw<TerminatedException>().Which.Message.Should().Contain("Invalid");
    }

    [Fact]
    public void PromptMode_1_Simple()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("2").Returns("5").Returns("1").Returns("1").Returns("n");

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p => p.Mode == ExportMode.Simple)), Times.Once);
    }

    [Fact]
    public void PromptMode_2_Complete()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("2").Returns("5").Returns("2").Returns("1").Returns("n");

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p => p.Mode == ExportMode.Complete)), Times.Once);
    }

    [Fact]
    public void PromptMode_Invalid_Terminates()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("2").Returns("5").Returns("9");

        Action act = () => BuildSut().Run(MakeArgs());

        act.Should().Throw<TerminatedException>().Which.Message.Should().Contain("Invalid");
    }

    [Fact]
    public void PromptIncludeCancelled_1_False()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("2").Returns("5").Returns("1").Returns("1").Returns("n");

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p => p.IncludeCancelled == false)), Times.Once);
    }

    [Fact]
    public void PromptIncludeCancelled_2_True()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("2").Returns("5").Returns("1").Returns("2").Returns("n");

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p => p.IncludeCancelled == true)), Times.Once);
    }

    [Fact]
    public void PromptIncludeCancelled_Invalid_Terminates()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("X"));

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("2").Returns("5").Returns("1").Returns("99");

        Action act = () => BuildSut().Run(MakeArgs());

        act.Should().Throw<TerminatedException>().Which.Message.Should().Contain("Invalid");
    }

    [Fact]
    public void PromptCalendarSelection_0_ReturnsNull()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("A", "B"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("0").Returns("2").Returns("5").Returns("1").Returns("1").Returns("n");

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p => p.SelectedFolders == null)), Times.Once);
    }

    [Fact]
    public void PromptCalendarSelection_Comma_ReturnsList()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("A", "B", "C"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("1,3").Returns("2").Returns("5").Returns("1").Returns("1").Returns("n");

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p =>
            p.SelectedFolders != null &&
            p.SelectedFolders.Count == 2 &&
            p.SelectedFolders[0].DisplayName == "A" &&
            p.SelectedFolders[1].DisplayName == "C")), Times.Once);
    }

    [Fact]
    public void PromptCalendarSelection_DupesDeduplicated()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("A", "B"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("n").Returns("1,1,2").Returns("2").Returns("5").Returns("1").Returns("1").Returns("n");

        BuildSut().Run(MakeArgs());

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p =>
            p.SelectedFolders != null && p.SelectedFolders.Count == 2)), Times.Once);
    }

    [Fact]
    public void PromptCalendarSelection_InvalidNumber_Terminates()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("A", "B"));

        _console.SetupSequence(c => c.ReadLine()).Returns("n").Returns("foo");

        Action act = () => BuildSut().Run(MakeArgs());

        act.Should().Throw<TerminatedException>().Which.Message.Should().Contain("not a valid");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Settings file resolution
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfigPathExistsButCorrupt_UserDeclines_Exits()
    {
        var custom = "C:\\some\\corrupt.json";
        _fs.Setup(f => f.FileExists(It.Is<string>(s => s == Path.GetFullPath(custom)))).Returns(true);
        _settingsRepo.Setup(r => r.TryLoad(It.IsAny<string>())).Returns((AppSettings?)null);

        _console.Setup(c => c.ReadLine()).Returns("n");

        Action act = () => BuildSut().Run(MakeArgs(configPath: custom));

        act.Should().Throw<TerminatedException>();
        _terminator.Verify(t => t.Exit(0), Times.Once);
    }

    [Fact]
    public void ConfigPathExistsButCorrupt_UserAccepts_UsesDefault()
    {
        SetupHappyFs();
        var custom = "C:\\some\\corrupt.json";
        _fs.Setup(f => f.FileExists(It.Is<string>(s => s == Path.GetFullPath(custom)))).Returns(true);
        _settingsRepo.Setup(r => r.TryLoad(It.IsAny<string>())).Returns((AppSettings?)null);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.SetupSequence(c => c.ReadLine())
                .Returns("y")    // continue with defaults? Y
                .Returns("");    // normal flow: proceed with defaults? Y

        BuildSut().Run(MakeArgs(configPath: custom));

        _settingsRepo.Verify(r => r.LoadOrCreateDefault(It.IsAny<string>()), Times.Once);
        _calSvc.Verify(c => c.GetAppointments(It.IsAny<ExportParameters>()), Times.Once);
    }

    [Fact]
    public void ConfigPathExists_Loaded()
    {
        SetupHappyFs();
        var custom   = "C:\\my\\settings.json";
        var loaded   = MakeSettings(year: new JValue(2099), month: new JValue(8));
        _fs.Setup(f => f.FileExists(It.Is<string>(s => s == Path.GetFullPath(custom)))).Returns(true);
        _settingsRepo.Setup(r => r.TryLoad(It.IsAny<string>())).Returns(loaded);
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        _console.Setup(c => c.ReadLine()).Returns("");   // proceed Y

        BuildSut().Run(MakeArgs(configPath: custom));

        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p =>
            p.Year == 2099 && p.Month == 8)), Times.Once);
    }

    [Fact]
    public void NoConfigPath_DefaultsFromExeDir()
    {
        SetupHappyFs();
        string? loadedFrom = null;
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Callback<string>(p => loadedFrom = p)
                     .Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());
        _console.Setup(c => c.ReadLine()).Returns("");

        BuildSut().Run(MakeArgs());

        loadedFrom.Should().NotBeNull();
        loadedFrom.Should().Be(Path.Combine(ExeDir, "settings.json"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Misc helpers / paths
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DisplaySettings_OutputContainsAllFields()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(year: new JValue(2025), month: new JValue(6)));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        BuildSut().Run(MakeArgs(autoMode: true));

        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("Calendars"))), Times.AtLeastOnce);
        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("Year"))), Times.AtLeastOnce);
        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("Month"))), Times.AtLeastOnce);
        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("Mode"))), Times.AtLeastOnce);
        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("Cancelled"))), Times.AtLeastOnce);
        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("Output dir"))), Times.AtLeastOnce);
    }

    [Fact]
    public void WriteSettingsWithDirCreation_DirExists_Writes()
    {
        SetupHappyFs();
        var pendingPath = "C:\\already\\here\\settings.json";
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _fs.Setup(f => f.DirectoryExists(It.Is<string>(s => s.Contains("already")))).Returns(true);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        BuildSut().Run(MakeArgs(autoMode: true, configPath: pendingPath));

        _fs.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Never);
        _settingsRepo.Verify(r => r.Save(It.IsAny<AppSettings>(), It.Is<string>(s => s.Contains("settings.json"))), Times.Once);
    }

    [Fact]
    public void WriteSettingsWithDirCreation_DirMissing_CreatesDir()
    {
        // exe dir + output dir exist, but pending settings dir does not.
        _fs.Setup(f => f.DirectoryExists(ExeDir)).Returns(true);
        _fs.Setup(f => f.DirectoryExists(It.Is<string>(s => s.Contains("brandnew")))).Returns(false);
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        var pendingPath = "C:\\brandnew\\settings.json";
        BuildSut().Run(MakeArgs(autoMode: true, configPath: pendingPath));

        _fs.Verify(f => f.CreateDirectory(It.Is<string>(s => s.Contains("brandnew"))), Times.Once);
        _settingsRepo.Verify(r => r.Save(It.IsAny<AppSettings>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void WriteSettingsWithDirCreation_Unauthorized_WritesError()
    {
        _fs.Setup(f => f.DirectoryExists(ExeDir)).Returns(true);
        _fs.Setup(f => f.DirectoryExists(It.Is<string>(s => s.Contains("denied")))).Returns(false);
        _fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _fs.Setup(f => f.CreateDirectory(It.Is<string>(s => s.Contains("denied"))))
           .Throws<UnauthorizedAccessException>();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        var pendingPath = "C:\\denied\\settings.json";
        BuildSut().Run(MakeArgs(autoMode: true, configPath: pendingPath));

        _console.Verify(c => c.WriteError(It.Is<string>(s => s.Contains("Access denied"))), Times.Once);
        _settingsRepo.Verify(r => r.Save(It.IsAny<AppSettings>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void GetCalendarFoldersOrExit_Empty_Exits()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>())).Returns(MakeSettings());
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(Array.Empty<CalendarFolderInfo>());

        _console.SetupSequence(c => c.ReadLine()).Returns("n");   // user rejects defaults -> goes to folder list

        Action act = () => BuildSut().Run(MakeArgs());

        act.Should().Throw<TerminatedException>().Which.Message.Should().Contain("No calendar folders");
    }

    [Fact]
    public void ResolveNamedCalendars_NoMatch_Warns()
    {
        SetupHappyFs();
        _settingsRepo.Setup(r => r.LoadOrCreateDefault(It.IsAny<string>()))
                     .Returns(MakeSettings(calendars: JArray.FromObject(new[] { "Missing" })));
        _calSvc.Setup(c => c.GetCalendarFolders()).Returns(MakeFolders("Other"));
        _calSvc.Setup(c => c.GetAppointments(It.IsAny<ExportParameters>())).Returns(Array.Empty<AppointmentRecord>());

        BuildSut().Run(MakeArgs(autoMode: true));

        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("Warning") && s.Contains("Missing"))), Times.Once);
        // No matches -> matcher returns null -> exports against all calendars.
        _calSvc.Verify(c => c.GetAppointments(It.Is<ExportParameters>(p => p.SelectedFolders == null)), Times.Once);
    }

    // ── Constructor null-guards ──────────────────────────────────────────

    [Fact]
    public void Ctor_NullArgs_Throw()
    {
        var resolver      = new SettingsResolver();
        var folderMatcher = new CalendarFolderMatcher();
        var outputDirSvc  = new OutputDirectoryService(_fs.Object, _console.Object, _terminator.Object);
        var exportService = new AppointmentExportService(_calSvc.Object, _ => _exporter.Object, _fs.Object, _console.Object);

        Action act1 = () => new ApplicationRunner(null!, _calSvc.Object, _settingsRepo.Object, _fs.Object, resolver, folderMatcher, outputDirSvc, exportService, _terminator.Object, ExeDir);
        Action act2 = () => new ApplicationRunner(_console.Object, null!, _settingsRepo.Object, _fs.Object, resolver, folderMatcher, outputDirSvc, exportService, _terminator.Object, ExeDir);
        Action act3 = () => new ApplicationRunner(_console.Object, _calSvc.Object, null!, _fs.Object, resolver, folderMatcher, outputDirSvc, exportService, _terminator.Object, ExeDir);
        Action act4 = () => new ApplicationRunner(_console.Object, _calSvc.Object, _settingsRepo.Object, null!, resolver, folderMatcher, outputDirSvc, exportService, _terminator.Object, ExeDir);
        Action act5 = () => new ApplicationRunner(_console.Object, _calSvc.Object, _settingsRepo.Object, _fs.Object, null!, folderMatcher, outputDirSvc, exportService, _terminator.Object, ExeDir);
        Action act6 = () => new ApplicationRunner(_console.Object, _calSvc.Object, _settingsRepo.Object, _fs.Object, resolver, null!, outputDirSvc, exportService, _terminator.Object, ExeDir);
        Action act7 = () => new ApplicationRunner(_console.Object, _calSvc.Object, _settingsRepo.Object, _fs.Object, resolver, folderMatcher, null!, exportService, _terminator.Object, ExeDir);
        Action act8 = () => new ApplicationRunner(_console.Object, _calSvc.Object, _settingsRepo.Object, _fs.Object, resolver, folderMatcher, outputDirSvc, null!, _terminator.Object, ExeDir);
        Action act9 = () => new ApplicationRunner(_console.Object, _calSvc.Object, _settingsRepo.Object, _fs.Object, resolver, folderMatcher, outputDirSvc, exportService, null!, ExeDir);
        Action act10= () => new ApplicationRunner(_console.Object, _calSvc.Object, _settingsRepo.Object, _fs.Object, resolver, folderMatcher, outputDirSvc, exportService, _terminator.Object, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
        act6.Should().Throw<ArgumentNullException>();
        act7.Should().Throw<ArgumentNullException>();
        act8.Should().Throw<ArgumentNullException>();
        act9.Should().Throw<ArgumentNullException>();
        act10.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Run_NullArgs_Throws()
    {
        var sut = BuildSut();
        Action act = () => sut.Run(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("args");
    }
}
