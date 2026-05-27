using System;
using System.Collections.Generic;
using System.Text;
using SyncMaster.CalExport;
using SyncMaster.Core;
using FluentAssertions;
using Moq;
using Xunit;

namespace SyncMaster.CalExport.Tests;

public sealed class AppointmentExportServiceTests
{
    private readonly Mock<ICalendarService>     _calSvc      = new Mock<ICalendarService>();
    private readonly Mock<IAppointmentExporter> _exporter    = new Mock<IAppointmentExporter>();
    private readonly Mock<IFileSystem>          _fs          = new Mock<IFileSystem>();
    private readonly Mock<IConsoleIO>           _console     = new Mock<IConsoleIO>();

    private AppointmentExportService BuildSut()
    {
        _exporter.Setup(e => e.FileSuffix).Returns("simple");
        _exporter.Setup(e => e.FileExtension).Returns("txt");
        _exporter.Setup(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()))
                 .Returns("serialized content");

        return new AppointmentExportService(
            _calSvc.Object,
            mode => _exporter.Object,
            _fs.Object,
            _console.Object);
    }

    private static ExportParameters MakeParams(
        int        year            = 2025,
        int        month           = 5,
        ExportMode mode            = ExportMode.Simple,
        bool       inclCancelled   = false,
        IReadOnlyList<CalendarFolderInfo>? folders = null) =>
        new ExportParameters(year, month, mode, inclCancelled, folders);

    [Fact]
    public void Export_CallsCalendarServiceWithCorrectParameters()
    {
        var sut    = BuildSut();
        var @params = MakeParams(year: 2024, month: 3, mode: ExportMode.Complete);
        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        sut.Export(@params, "C:\\output");

        _calSvc.Verify(s => s.GetAppointments(It.Is<ExportParameters>(p =>
            p.Year == 2024 && p.Month == 3 && p.Mode == ExportMode.Complete)), Times.Once);
    }

    [Fact]
    public void Export_UsesExporterReturnedByFactory()
    {
        var factoryCalled = false;
        _exporter.Setup(e => e.FileSuffix).Returns("complete");
        _exporter.Setup(e => e.FileExtension).Returns("json");
        _exporter.Setup(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()))
                 .Returns("{}");

        var sut = new AppointmentExportService(
            _calSvc.Object,
            mode => { factoryCalled = true; return _exporter.Object; },
            _fs.Object,
            _console.Object);

        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        sut.Export(MakeParams(mode: ExportMode.Complete), "C:\\output");

        factoryCalled.Should().BeTrue();
        _exporter.Verify(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()), Times.Once);
    }

    [Fact]
    public void Export_WritesFileWithCorrectPathComponents()
    {
        var sut = BuildSut();
        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        string? capturedPath = null;
        _fs.Setup(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()))
           .Callback<string, string, Encoding>((path, _, _) => capturedPath = path);

        sut.Export(MakeParams(year: 2025, month: 5), "C:\\output");

        capturedPath.Should().NotBeNull();
        capturedPath.Should().Contain("2025");
        capturedPath.Should().Contain("May");
        capturedPath.Should().Contain("simple");
        capturedPath.Should().StartWith("C:\\output");
    }

    [Fact]
    public void Export_RecordsAreSortedByStartBeforeSerializing()
    {
        var sut = BuildSut();

        var r1 = new AppointmentRecord { Start = new DateTime(2025, 5, 20), Subject = "Later" };
        var r2 = new AppointmentRecord { Start = new DateTime(2025, 5, 10), Subject = "Earlier" };

        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(new[] { r1, r2 });

        IReadOnlyList<AppointmentRecord>? capturedRecords = null;
        _exporter.Setup(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()))
                 .Callback<IReadOnlyList<AppointmentRecord>, ExportContext>((recs, _) => capturedRecords = recs)
                 .Returns("content");

        sut.Export(MakeParams(), "C:\\output");

        capturedRecords.Should().NotBeNull();
        capturedRecords![0].Subject.Should().Be("Earlier");
        capturedRecords![1].Subject.Should().Be("Later");
    }

    [Fact]
    public void Export_FileNameIncludesCurrentDateyyyyMMdd()
    {
        var sut = BuildSut();
        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        string? capturedPath = null;
        _fs.Setup(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()))
           .Callback<string, string, Encoding>((path, _, _) => capturedPath = path);

        sut.Export(MakeParams(), "C:\\output");

        var expectedDate = DateTime.Now.ToString("yyyyMMdd");
        capturedPath.Should().Contain(expectedDate);
    }

    [Fact]
    public void Export_ReturnsWrittenFilePath()
    {
        var sut = BuildSut();
        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        var result = sut.Export(MakeParams(year: 2025, month: 5), "C:\\output");

        result.Should().StartWith("C:\\output");
        result.Should().Contain("2025");
        result.Should().Contain("May");
    }

    // ── Constructor null-guards ──────────────────────────────────────────

    [Fact]
    public void Ctor_NullCalendarService_Throws()
    {
        Action act = () => new AppointmentExportService(
            null!, mode => _exporter.Object, _fs.Object, _console.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("calendarService");
    }

    [Fact]
    public void Ctor_NullExporterFactory_Throws()
    {
        Action act = () => new AppointmentExportService(
            _calSvc.Object, null!, _fs.Object, _console.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("exporterFactory");
    }

    [Fact]
    public void Ctor_NullFileSystem_Throws()
    {
        Action act = () => new AppointmentExportService(
            _calSvc.Object, mode => _exporter.Object, null!, _console.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("fileSystem");
    }

    [Fact]
    public void Ctor_NullConsole_Throws()
    {
        Action act = () => new AppointmentExportService(
            _calSvc.Object, mode => _exporter.Object, _fs.Object, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("console");
    }

    // ── Export null-guards ───────────────────────────────────────────────

    [Fact]
    public void Export_NullParameters_Throws()
    {
        var sut = BuildSut();

        Action act = () => sut.Export(null!, "C:\\output");

        act.Should().Throw<ArgumentNullException>().WithParameterName("parameters");
    }

    [Fact]
    public void Export_NullOutputDirectory_Throws()
    {
        var sut = BuildSut();

        Action act = () => sut.Export(MakeParams(), null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("outputDirectory");
    }

    // ── SelectedFolders branches ─────────────────────────────────────────

    [Fact]
    public void Export_SelectedFoldersNull_PassesAllInContext()
    {
        var sut = BuildSut();
        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        ExportContext? capturedContext = null;
        _exporter.Setup(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()))
                 .Callback<IReadOnlyList<AppointmentRecord>, ExportContext>((_, ctx) => capturedContext = ctx)
                 .Returns("content");

        sut.Export(MakeParams(folders: null), "C:\\output");

        capturedContext.Should().NotBeNull();
        capturedContext!.CalendarDisplayNames.Should().BeEquivalentTo(new[] { "all" });
    }

    [Fact]
    public void Export_SelectedFoldersProvided_PassesDisplayNamesInContext()
    {
        var sut = BuildSut();
        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        ExportContext? capturedContext = null;
        _exporter.Setup(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()))
                 .Callback<IReadOnlyList<AppointmentRecord>, ExportContext>((_, ctx) => capturedContext = ctx)
                 .Returns("content");

        var folders = new[]
        {
            new CalendarFolderInfo { DisplayName = "Personal" },
            new CalendarFolderInfo { DisplayName = "Work" },
        };

        sut.Export(MakeParams(folders: folders), "C:\\output");

        capturedContext.Should().NotBeNull();
        capturedContext!.CalendarDisplayNames.Should().BeEquivalentTo(new[] { "Personal", "Work" });
    }

    [Fact]
    public void Export_FactoryReceivesCorrectMode_Complete()
    {
        ExportMode? requestedMode = null;
        _exporter.Setup(e => e.FileSuffix).Returns("complete");
        _exporter.Setup(e => e.FileExtension).Returns("json");
        _exporter.Setup(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()))
                 .Returns("{}");

        var sut = new AppointmentExportService(
            _calSvc.Object,
            mode => { requestedMode = mode; return _exporter.Object; },
            _fs.Object,
            _console.Object);

        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        sut.Export(MakeParams(mode: ExportMode.Complete), "C:\\output");

        requestedMode.Should().Be(ExportMode.Complete);
    }

    [Fact]
    public void Export_FactoryReceivesCorrectMode_Simple()
    {
        ExportMode? requestedMode = null;
        _exporter.Setup(e => e.FileSuffix).Returns("simple");
        _exporter.Setup(e => e.FileExtension).Returns("txt");
        _exporter.Setup(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()))
                 .Returns("data");

        var sut = new AppointmentExportService(
            _calSvc.Object,
            mode => { requestedMode = mode; return _exporter.Object; },
            _fs.Object,
            _console.Object);

        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        sut.Export(MakeParams(mode: ExportMode.Simple), "C:\\output");

        requestedMode.Should().Be(ExportMode.Simple);
    }

    [Fact]
    public void Export_WritesSerializedContentToFileSystemWithUtf8()
    {
        var sut = BuildSut();
        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        _exporter.Setup(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()))
                 .Returns("payload-xyz");

        string? writtenContent = null;
        Encoding? writtenEncoding = null;
        _fs.Setup(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()))
           .Callback<string, string, Encoding>((_, c, enc) => { writtenContent = c; writtenEncoding = enc; });

        sut.Export(MakeParams(), "C:\\output");

        writtenContent.Should().Be("payload-xyz");
        writtenEncoding.Should().Be(Encoding.UTF8);
    }

    [Fact]
    public void Export_EmptyRecords_StillWritesFileAndLogsCountZero()
    {
        var sut = BuildSut();
        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        sut.Export(MakeParams(), "C:\\output");

        _fs.Verify(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()), Times.Once);
        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("0 event"))), Times.Once);
    }

    [Fact]
    public void Export_ContextCarriesYearMonthAndMonthName()
    {
        var sut = BuildSut();
        _calSvc.Setup(s => s.GetAppointments(It.IsAny<ExportParameters>()))
               .Returns(Array.Empty<AppointmentRecord>());

        ExportContext? capturedContext = null;
        _exporter.Setup(e => e.Serialize(It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<ExportContext>()))
                 .Callback<IReadOnlyList<AppointmentRecord>, ExportContext>((_, ctx) => capturedContext = ctx)
                 .Returns("c");

        sut.Export(MakeParams(year: 2025, month: 7), "C:\\output");

        capturedContext.Should().NotBeNull();
        capturedContext!.Year.Should().Be(2025);
        capturedContext.Month.Should().Be(7);
        capturedContext.MonthName.Should().Be(MonthNames.Get(7));
    }
}
