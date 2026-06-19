using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Clipboard;

public class PublishItemRequestValidatorTests
{
    private static readonly PublishItemRequestValidator Sut = new();

    private static PublishItemRequest Valid(string type = "Text") =>
        new("id1", type, "dev1", "Dev One", 123, "cGF5bG9hZA==", null, null);

    [Fact]
    public void Text_publish_is_accepted()
    {
        Sut.Validate(Valid("Text")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Image_publish_is_accepted()
    {
        Sut.Validate(Valid("Image")).IsValid.Should().BeTrue();
    }

    // A File carries only metadata here (its bytes go via the lazy-blob endpoint): name + size are
    // required, payload is empty.
    private static PublishItemRequest ValidFile() =>
        new("id1", "File", "dev1", "Dev One", 4096, "", null, "report.pdf");

    [Fact]
    public void File_publish_is_accepted_with_name_and_size()
    {
        Sut.Validate(ValidFile()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void File_publish_without_a_name_is_rejected()
    {
        var result = Sut.Validate(ValidFile() with { Preview = null });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Preview");
    }

    [Fact]
    public void File_publish_without_a_size_is_rejected()
    {
        var result = Sut.Validate(ValidFile() with { SizeBytes = null });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "SizeBytes");
    }

    [Fact]
    public void Unknown_type_is_rejected()
    {
        Sut.Validate(Valid("Bogus")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_id_is_rejected()
    {
        Sut.Validate(Valid() with { Id = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_origin_device_id_is_rejected()
    {
        Sut.Validate(Valid() with { OriginDeviceId = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_payload_is_rejected()
    {
        Sut.Validate(Valid() with { PayloadBase64 = "" }).IsValid.Should().BeFalse();
    }
}

public class UpdateClipboardSettingsRequestValidatorTests
{
    private static readonly UpdateClipboardSettingsRequestValidator Sut = new();

    private static UpdateClipboardSettingsRequest Valid(string density = "rich", string hotkey = "Ctrl+Win+Q") =>
        new(AutoSync: true, Send: true, Receive: true, ViewerHotkey: hotkey, Density: density, ShowHints: true);

    [Fact]
    public void Valid_settings_are_accepted()
    {
        Sut.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Mini_density_is_accepted()
    {
        Sut.Validate(Valid(density: "mini")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Bad_density_is_rejected()
    {
        Sut.Validate(Valid(density: "huge")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_hotkey_is_rejected()
    {
        Sut.Validate(Valid(hotkey: "")).IsValid.Should().BeFalse();
    }
}

public class RelayKeyRequestValidatorTests
{
    private static readonly RelayKeyRequestValidator Sut = new();

    private static RelayKeyRequest Valid() => new("from1", "target1", "d3JhcHBlZA==");

    [Fact]
    public void Valid_relay_is_accepted()
    {
        Sut.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_target_is_rejected()
    {
        Sut.Validate(Valid() with { TargetDeviceId = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_from_is_rejected()
    {
        Sut.Validate(Valid() with { FromDeviceId = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_wrapped_key_is_rejected()
    {
        Sut.Validate(Valid() with { WrappedKeyBase64 = "" }).IsValid.Should().BeFalse();
    }
}
