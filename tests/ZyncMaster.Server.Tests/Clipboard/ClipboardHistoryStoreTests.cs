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
    public async Task Remove_deletes_only_the_targeted_item_for_the_user()
    {
        var store = ClipboardTestHarness.HistoryStore("u1");
        await store.AppendAsync(Text("keep", DateTimeOffset.UtcNow.AddSeconds(1)));
        await store.AppendAsync(Text("drop", DateTimeOffset.UtcNow.AddSeconds(2)));
        await store.RemoveAsync("drop");
        (await store.ListAsync()).Select(i => i.Id).Should().Equal("keep");
    }
}
