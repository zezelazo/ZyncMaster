using System;
using System.Text;
using FluentAssertions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace SyncMaster.Core.Tests;

public sealed class SettingsRepositoryTests
{
    public sealed class DummySettings
    {
        public string Mode { get; set; } = "";
        public bool   Flag { get; set; }
    }

    private readonly Mock<IFileSystem> _fs = new Mock<IFileSystem>();

    private SettingsRepository<DummySettings> BuildSut() =>
        new SettingsRepository<DummySettings>(_fs.Object);

    [Fact]
    public void TryLoad_FileDoesNotExist_ReturnsNull()
    {
        _fs.Setup(f => f.FileExists("settings.json")).Returns(false);

        BuildSut().TryLoad("settings.json").Should().BeNull();
    }

    [Fact]
    public void TryLoad_ValidJson_ReturnsDeserialized()
    {
        var json = JsonConvert.SerializeObject(new DummySettings { Mode = "simple", Flag = true }, Formatting.Indented);
        _fs.Setup(f => f.FileExists("settings.json")).Returns(true);
        _fs.Setup(f => f.ReadAllText("settings.json")).Returns(json);

        var result = BuildSut().TryLoad("settings.json");

        result.Should().NotBeNull();
        result!.Mode.Should().Be("simple");
        result.Flag.Should().BeTrue();
    }

    [Fact]
    public void TryLoad_InvalidJson_ReturnsNull()
    {
        _fs.Setup(f => f.FileExists("settings.json")).Returns(true);
        _fs.Setup(f => f.ReadAllText("settings.json")).Returns("{ not valid json ]]]");

        BuildSut().TryLoad("settings.json").Should().BeNull();
    }

    [Fact]
    public void LoadOrCreateDefault_WhenFileExists_LoadsIt()
    {
        var json = JsonConvert.SerializeObject(new DummySettings { Mode = "simple" }, Formatting.Indented);
        _fs.Setup(f => f.FileExists("settings.json")).Returns(true);
        _fs.Setup(f => f.ReadAllText("settings.json")).Returns(json);

        BuildSut().LoadOrCreateDefault("settings.json").Mode.Should().Be("simple");
    }

    [Fact]
    public void LoadOrCreateDefault_WhenFileMissing_CreatesDefaultAndSaves()
    {
        _fs.Setup(f => f.FileExists("settings.json")).Returns(false);

        var result = BuildSut().LoadOrCreateDefault("settings.json");

        result.Should().NotBeNull();
        _fs.Verify(f => f.WriteAllText("settings.json", It.IsAny<string>(), It.IsAny<Encoding>()), Times.Once);
    }

    [Fact]
    public void LoadOrCreateDefault_WhenFileExistsButDeserializesToNull_Throws()
    {
        _fs.Setup(f => f.FileExists("settings.json")).Returns(true);
        _fs.Setup(f => f.ReadAllText("settings.json")).Returns("null");

        var act = () => BuildSut().LoadOrCreateDefault("settings.json");

        act.Should().Throw<SettingsLoadException>()
           .WithMessage("*settings.json*could not be deserialized*");
        _fs.Verify(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()), Times.Never);
    }

    [Fact]
    public void LoadOrCreateDefault_WhenFileExistsWithInvalidJson_Throws()
    {
        _fs.Setup(f => f.FileExists("settings.json")).Returns(true);
        _fs.Setup(f => f.ReadAllText("settings.json")).Returns("{ broken");

        var act = () => BuildSut().LoadOrCreateDefault("settings.json");

        act.Should().Throw<SettingsLoadException>()
           .WithMessage("*settings.json*could not be deserialized*");
        _fs.Verify(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()), Times.Never);
    }

    [Fact]
    public void LoadOrCreateDefault_FileExists_DoesNotOverwrite()
    {
        var json = JsonConvert.SerializeObject(new DummySettings { Mode = "simple" }, Formatting.Indented);
        _fs.Setup(f => f.FileExists("settings.json")).Returns(true);
        _fs.Setup(f => f.ReadAllText("settings.json")).Returns(json);

        BuildSut().LoadOrCreateDefault("settings.json");

        _fs.Verify(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()), Times.Never);
    }

    [Fact]
    public void Save_WritesSerializedJson()
    {
        string? written = null;
        _fs.Setup(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()))
           .Callback<string, string, Encoding>((_, content, _) => written = content);

        BuildSut().Save(new DummySettings { Mode = "complete", Flag = true }, "settings.json");

        written.Should().NotBeNull();
        var parsed = JsonConvert.DeserializeObject<DummySettings>(written!);
        parsed!.Mode.Should().Be("complete");
        parsed.Flag.Should().BeTrue();
    }

    [Fact]
    public void Exists_DelegatesToFileSystem()
    {
        _fs.Setup(f => f.FileExists("settings.json")).Returns(true);

        BuildSut().Exists("settings.json").Should().BeTrue();
        _fs.Verify(f => f.FileExists("settings.json"), Times.Once);
    }

    // ── Null-argument guards ──────────────────────────────────────────────

    [Fact]
    public void Ctor_NullFileSystem_Throws()
    {
        Action act = () => new SettingsRepository<DummySettings>(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("fs");
    }

    [Fact]
    public void TryLoad_NullPath_Throws()
    {
        Action act = () => BuildSut().TryLoad(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("path");
    }

    [Fact]
    public void LoadOrCreateDefault_NullPath_Throws()
    {
        Action act = () => BuildSut().LoadOrCreateDefault(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("path");
    }

    [Fact]
    public void Save_NullSettings_Throws()
    {
        Action act = () => BuildSut().Save(null!, "settings.json");

        act.Should().Throw<ArgumentNullException>().WithParameterName("settings");
    }

    [Fact]
    public void Save_NullPath_Throws()
    {
        Action act = () => BuildSut().Save(new DummySettings(), null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("path");
    }

    [Fact]
    public void Exists_NullPath_Throws()
    {
        Action act = () => BuildSut().Exists(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("path");
    }
}
