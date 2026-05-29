using System;
using System.IO;
using ZyncMaster.CalExport;
using ZyncMaster.Core;
using FluentAssertions;
using Moq;
using Xunit;

namespace ZyncMaster.CalExport.Tests;

public sealed class OutputDirectoryServiceTests
{
    private readonly Mock<IFileSystem>            _fs         = new Mock<IFileSystem>();
    private readonly Mock<IConsoleIO>             _console    = new Mock<IConsoleIO>();
    private readonly Mock<IApplicationTerminator> _terminator = new Mock<IApplicationTerminator>();

    private OutputDirectoryService BuildSut() =>
        new OutputDirectoryService(_fs.Object, _console.Object, _terminator.Object);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmptyRequestedPath_ReturnsFallback(string? requested)
    {
        var sut = BuildSut();
        var result = sut.Resolve(requested, "C:\\fallback", createSilently: false);
        result.Should().Be("C:\\fallback");
    }

    [Fact]
    public void PathExists_ReturnsFullPath()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(true);
        var result = sut.Resolve("C:\\existing", "C:\\fallback", createSilently: false);
        result.Should().Be("C:\\existing");
    }

    [Fact]
    public void PathNotExists_CreateSilentlyTrue_CreatesAndReturns()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        var result = sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: true);
        _fs.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Once);
        result.Should().Contain("newdir");
    }

    [Fact]
    public void PathNotExists_NotSilent_UserSaysY_CreatesAndReturns()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _console.Setup(c => c.ReadLine()).Returns("y");
        var result = sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: false);
        _fs.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Once);
        result.Should().Contain("newdir");
    }

    [Fact]
    public void PathNotExists_NotSilent_UserSaysUppercaseY_CreatesAndReturns()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _console.Setup(c => c.ReadLine()).Returns("Y");
        var result = sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: false);
        _fs.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Once);
        result.Should().Contain("newdir");
    }

    [Fact]
    public void PathNotExists_NotSilent_UserSaysN_CallsTerminator()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _console.Setup(c => c.ReadLine()).Returns("n");
        _terminator.Setup(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()))
                   .Throws(new OperationCanceledException("Simulated exit"));

        Action act = () => sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: false);

        act.Should().Throw<OperationCanceledException>();
        _terminator.Verify(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()), Times.Once);
        _fs.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void CreateDirectoryThrows_UnauthorizedAccess_CallsTerminator()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _fs.Setup(f => f.CreateDirectory(It.IsAny<string>())).Throws<UnauthorizedAccessException>();
        _terminator.Setup(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()))
                   .Throws(new OperationCanceledException("Simulated exit"));

        Action act = () => sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: true);

        act.Should().Throw<OperationCanceledException>();
        _terminator.Verify(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void CreateDirectorySuccess_WritesSuccessMessage()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: true);
        _console.Verify(c => c.WriteLine(It.Is<string?>(s => s != null && s.Contains("created"))), Times.Once);
    }

    // ── Constructor null-guards ───────────────────────────────────────────

    [Fact]
    public void Ctor_NullFileSystem_Throws()
    {
        Action act = () => new OutputDirectoryService(null!, _console.Object, _terminator.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("fs");
    }

    [Fact]
    public void Ctor_NullConsole_Throws()
    {
        Action act = () => new OutputDirectoryService(_fs.Object, null!, _terminator.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("console");
    }

    [Fact]
    public void Ctor_NullTerminator_Throws()
    {
        Action act = () => new OutputDirectoryService(_fs.Object, _console.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("terminator");
    }

    // ── User prompt branches ──────────────────────────────────────────────

    [Fact]
    public void PathNotExists_NotSilent_EmptyAnswer_TreatsAsYes()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _console.Setup(c => c.ReadLine()).Returns("");

        var result = sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: false);

        _fs.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Once);
        result.Should().Contain("newdir");
        _terminator.Verify(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void PathNotExists_NotSilent_NullAnswer_TreatsAsYes()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _console.Setup(c => c.ReadLine()).Returns((string?)null);

        var result = sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: false);

        _fs.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Once);
        result.Should().Contain("newdir");
    }

    [Fact]
    public void PathNotExists_NotSilent_YesWord_CreatesDirectory()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _console.Setup(c => c.ReadLine()).Returns("yes");

        var result = sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: false);

        _fs.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Once);
        result.Should().Contain("newdir");
    }

    [Fact]
    public void PathNotExists_NotSilent_AnswerWithWhitespace_IsTrimmedAndAccepted()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _console.Setup(c => c.ReadLine()).Returns("   y   ");

        var result = sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: false);

        _fs.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Once);
        result.Should().Contain("newdir");
    }

    // ── CreateDirectory exception branches ────────────────────────────────

    [Fact]
    public void CreateDirectoryThrows_ArgumentException_CallsTerminator()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _fs.Setup(f => f.CreateDirectory(It.IsAny<string>())).Throws<ArgumentException>();
        _terminator.Setup(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()))
                   .Throws(new OperationCanceledException("Simulated exit"));

        Action act = () => sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: true);

        act.Should().Throw<OperationCanceledException>();
        _terminator.Verify(t => t.ExitWithError(
            It.Is<string>(s => s != null && s.Contains("Invalid path")),
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void CreateDirectoryThrows_IOException_CallsTerminator()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _fs.Setup(f => f.CreateDirectory(It.IsAny<string>()))
           .Throws(new IOException("disk is full"));
        _terminator.Setup(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()))
                   .Throws(new OperationCanceledException("Simulated exit"));

        Action act = () => sut.Resolve("C:\\newdir", "C:\\fallback", createSilently: true);

        act.Should().Throw<OperationCanceledException>();
        _terminator.Verify(t => t.ExitWithError(
            It.Is<string>(s => s != null && s.Contains("disk is full")),
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void PathExists_DoesNotPromptOrCreate()
    {
        var sut = BuildSut();
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(true);

        sut.Resolve("C:\\existing", "C:\\fallback", createSilently: false);

        _console.Verify(c => c.ReadLine(), Times.Never);
        _fs.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Never);
        _terminator.Verify(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }
}
