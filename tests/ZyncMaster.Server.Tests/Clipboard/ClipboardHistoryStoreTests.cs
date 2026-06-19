using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Clipboard;

public class ClipboardHistoryStoreTests
{
    private static ClipboardItem Text(string id, DateTimeOffset t) => new()
    {
        Id = id, UserId = "u1", Type = ClipboardItemType.Text, OriginDeviceId = "d1",
        CreatedUtc = t, Payload = new byte[] { 1, 2, 3 },
    };
    private static ClipboardItem Image(string id, DateTimeOffset t, long size) => new()
    {
        Id = id, UserId = "u1", Type = ClipboardItemType.Image, OriginDeviceId = "d1",
        CreatedUtc = t, SizeBytes = size, Payload = new byte[size], Thumbnail = new byte[] { 9 },
    };
    private static ClipboardItem FileItem(string id, DateTimeOffset t) => new()
    {
        Id = id, UserId = "u1", Type = ClipboardItemType.File, OriginDeviceId = "d1",
        CreatedUtc = t, Preview = id + ".bin", SizeBytes = 10, // File: metadata only; bytes live in the blob store
    };

    [Fact]
    public async Task Append_File_stores_metadata_only_no_inline_payload()
    {
        var store = ClipboardTestHarness.HistoryStore("u1");
        await store.AppendAsync(FileItem("f1", DateTimeOffset.UtcNow));
        var item = (await store.ListAsync()).Single();
        item.Type.Should().Be(ClipboardItemType.File);
        item.Preview.Should().Be("f1.bin");
        item.SizeBytes.Should().Be(10);
        item.Payload.Should().BeEmpty();
    }

    [Fact]
    public async Task Evicting_a_File_deletes_its_blob()
    {
        var blobs = ClipboardTestHarness.TempBlobStore();
        var store = ClipboardTestHarness.HistoryStore("u1", new ClipboardOptions { MaxItemsPerUser = 1 }, blobs: blobs);
        await blobs.SaveAsync("u1", "f-old", new System.IO.MemoryStream(new byte[] { 1, 2, 3 }));
        await store.AppendAsync(FileItem("f-old", DateTimeOffset.UtcNow.AddSeconds(1)));
        await store.AppendAsync(Text("newer", DateTimeOffset.UtcNow.AddSeconds(2))); // FIFO cap 1 evicts f-old
        (await blobs.OpenReadAsync("u1", "f-old")).Should().BeNull();
    }

    [Fact]
    public async Task Remove_a_File_deletes_its_blob()
    {
        var blobs = ClipboardTestHarness.TempBlobStore();
        var store = ClipboardTestHarness.HistoryStore("u1", blobs: blobs);
        await blobs.SaveAsync("u1", "f1", new System.IO.MemoryStream(new byte[] { 1 }));
        await store.AppendAsync(FileItem("f1", DateTimeOffset.UtcNow));
        await store.RemoveAsync("f1");
        (await blobs.OpenReadAsync("u1", "f1")).Should().BeNull();
    }

    [Fact]
    public async Task Append_then_List_returns_newest_first()
    {
        var store = ClipboardTestHarness.HistoryStore("u1");
        await store.AppendAsync(Text("a", DateTimeOffset.UtcNow.AddMinutes(-2)));
        await store.AppendAsync(Text("b", DateTimeOffset.UtcNow.AddMinutes(-1)));
        (await store.ListAsync()).Select(i => i.Id).Should().ContainInOrder("b", "a");
    }

    [Fact]
    public async Task Append_evicts_oldest_beyond_MaxItemsPerUser()
    {
        var store = ClipboardTestHarness.HistoryStore("u1", new ClipboardOptions { MaxItemsPerUser = 3 });
        for (var i = 0; i < 5; i++)
            await store.AppendAsync(Text($"i{i}", DateTimeOffset.UtcNow.AddSeconds(i)));
        var list = await store.ListAsync();
        list.Should().HaveCount(3);
        list.Select(i => i.Id).Should().ContainInOrder("i4", "i3", "i2");
    }

    [Fact]
    public async Task Append_evicts_items_older_than_RetentionMaxAge()
    {
        var store = ClipboardTestHarness.HistoryStore("u1",
            new ClipboardOptions { RetentionMaxAge = TimeSpan.FromHours(1), MaxItemsPerUser = 100 });
        await store.AppendAsync(Text("stale", DateTimeOffset.UtcNow.AddHours(-2))); // older than the 1h window
        await store.AppendAsync(Text("fresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        (await store.ListAsync()).Select(i => i.Id).Should().BeEquivalentTo(new[] { "fresh" });
    }

    [Fact]
    public async Task Append_keeps_aged_items_when_RetentionMaxAge_disabled()
    {
        var store = ClipboardTestHarness.HistoryStore("u1",
            new ClipboardOptions { RetentionMaxAge = TimeSpan.Zero, MaxItemsPerUser = 100 });
        await store.AppendAsync(Text("ancient", DateTimeOffset.UtcNow.AddDays(-5)));
        await store.AppendAsync(Text("now", DateTimeOffset.UtcNow));
        (await store.ListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Append_image_over_hard_max_throws()
    {
        var store = ClipboardTestHarness.HistoryStore("u1", new ClipboardOptions { HardMaxImageBytes = 10 });
        Func<Task> act = () => store.AppendAsync(Image("big", DateTimeOffset.UtcNow, 20));
        await act.Should().ThrowAsync<ClipboardImageTooLargeException>();
    }

    [Fact]
    public async Task Append_evicts_oldest_images_when_user_image_total_exceeded()
    {
        var store = ClipboardTestHarness.HistoryStore("u1", new ClipboardOptions { MaxImageTotalBytesPerUser = 30, MaxItemsPerUser = 100, HardMaxImageBytes = 1000 });
        await store.AppendAsync(Image("img1", DateTimeOffset.UtcNow.AddSeconds(1), 20));
        await store.AppendAsync(Image("img2", DateTimeOffset.UtcNow.AddSeconds(2), 20)); // total 40 > 30 -> evict img1
        var ids = (await store.ListAsync()).Select(i => i.Id).ToList();
        ids.Should().Contain("img2");
        ids.Should().NotContain("img1");
    }

    [Fact]
    public async Task List_is_user_scoped()
    {
        var s1 = ClipboardTestHarness.HistoryStore("u1", shareDb: true);
        var s2 = ClipboardTestHarness.HistoryStore("u2", shareDb: true); // same DB, different user
        await s1.AppendAsync(Text("only-u1", DateTimeOffset.UtcNow));
        (await s2.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetNewest_returns_null_for_empty_history()
    {
        var store = ClipboardTestHarness.HistoryStore("u1");
        (await store.GetNewestAsync()).Should().BeNull();
    }

    [Fact]
    public async Task GetNewest_returns_the_most_recent_item_with_its_payload()
    {
        var store = ClipboardTestHarness.HistoryStore("u1");
        await store.AppendAsync(Text("old", DateTimeOffset.UtcNow.AddMinutes(-2)));
        await store.AppendAsync(Text("new", DateTimeOffset.UtcNow.AddMinutes(-1)));

        var newest = await store.GetNewestAsync();

        newest.Should().NotBeNull();
        newest!.Id.Should().Be("new");
        newest.Payload.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task GetNewest_is_user_scoped()
    {
        var s1 = ClipboardTestHarness.HistoryStore("u1", shareDb: true);
        var s2 = ClipboardTestHarness.HistoryStore("u2", shareDb: true);
        await s1.AppendAsync(Text("u1-newest", DateTimeOffset.UtcNow));

        (await s2.GetNewestAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Remove_deletes_only_the_targeted_item_for_the_user()
    {
        var store = ClipboardTestHarness.HistoryStore("u1");
        await store.AppendAsync(Text("keep", DateTimeOffset.UtcNow.AddSeconds(1)));
        await store.AppendAsync(Text("drop", DateTimeOffset.UtcNow.AddSeconds(2)));
        await store.RemoveAsync("drop");
        (await store.ListAsync()).Select(i => i.Id).Should().Equal("keep");
    }
}
