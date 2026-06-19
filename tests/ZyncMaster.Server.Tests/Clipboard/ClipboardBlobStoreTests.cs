using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Clipboard;

public class ClipboardBlobStoreTests
{
    private static DiskClipboardBlobStore NewStore() =>
        new(Path.Combine(Path.GetTempPath(), "zm-blob-test", Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task Save_then_read_round_trips_the_bytes()
    {
        var store = NewStore();
        var bytes = Encoding.UTF8.GetBytes("hello blob");
        await store.SaveAsync("u1", "i1", new MemoryStream(bytes));

        await using var read = await store.OpenReadAsync("u1", "i1");
        read.Should().NotBeNull();
        using var ms = new MemoryStream();
        await read!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task OpenRead_missing_returns_null()
    {
        (await NewStore().OpenReadAsync("u1", "nope")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_removes_the_blob_and_is_idempotent()
    {
        var store = NewStore();
        await store.SaveAsync("u1", "i1", new MemoryStream(new byte[] { 1, 2, 3 }));
        await store.DeleteAsync("u1", "i1");
        (await store.OpenReadAsync("u1", "i1")).Should().BeNull();
        await store.DeleteAsync("u1", "i1"); // idempotent — must not throw
    }

    [Fact]
    public async Task Save_overwrites_existing_atomically()
    {
        var store = NewStore();
        await store.SaveAsync("u1", "i1", new MemoryStream(new byte[] { 1 }));
        await store.SaveAsync("u1", "i1", new MemoryStream(new byte[] { 9, 9 }));
        await using var read = await store.OpenReadAsync("u1", "i1");
        using var ms = new MemoryStream();
        await read!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(new byte[] { 9, 9 });
    }

    [Fact]
    public async Task A_blob_is_isolated_per_user()
    {
        var store = NewStore();
        await store.SaveAsync("u1", "i1", new MemoryStream(new byte[] { 1 }));
        (await store.OpenReadAsync("u2", "i1")).Should().BeNull();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task Rejects_unsafe_keys(string badId)
    {
        var store = NewStore();
        var act = () => store.SaveAsync("u1", badId, new MemoryStream(new byte[] { 1 }));
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
