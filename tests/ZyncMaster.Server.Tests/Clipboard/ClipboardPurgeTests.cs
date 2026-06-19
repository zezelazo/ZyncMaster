using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server.Tests.Clipboard;

// Drives EphemeralPurgeService.PurgeOnceAsync directly with a fixed clock so the new
// clipboard-age pass is fully unit-testable: seeds a user with ClipboardRetentionHours set
// (override path), or unset (fallback to the server default), drops the File's blob into a
// temp DiskClipboardBlobStore, and asserts which rows survive.
public class ClipboardPurgeTests : IDisposable
{
    private readonly ServerTestFactory _factory;
    private readonly string _blobRoot;

    public ClipboardPurgeTests()
    {
        _factory = new ServerTestFactory();
        _blobRoot = Path.Combine(Path.GetTempPath(), "zm-purge-test-blobs", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_blobRoot, recursive: true); } catch { /* best-effort */ }
    }

    private IDbContextFactory<ZyncMasterDbContext> DbFactory() =>
        _factory.Services.GetRequiredService<IDbContextFactory<ZyncMasterDbContext>>();

    private IClipboardBlobStore NewBlobStore() => new DiskClipboardBlobStore(_blobRoot);

    private static EphemeralPurgeService BuildService(
        IDbContextFactory<ZyncMasterDbContext> factory,
        IClipboardBlobStore blobs,
        ClipboardOptions? clipboardOptions = null)
        => new(factory, NullLogger<EphemeralPurgeService>.Instance, options: null,
               blobs: blobs, clipboardOptions: Options.Create(clipboardOptions ?? new ClipboardOptions()));

    [Fact]
    public async Task Purge_deletes_aged_items_using_user_override_and_removes_file_blob()
    {
        var factory = DbFactory();
        var now = DateTimeOffset.UtcNow;

        // Seed a user with a 1-hour retention window.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRow
            {
                Id = "u-aged",
                Provider = "microsoft",
                Subject = "subject-aged",
                PrimaryEmail = "aged@test",
                CreatedUtc = now,
                ClipboardRetentionHours = 1,
            });
            // Two text items: one 2h old (must go), one 10min old (must survive). Plus a File
            // item 2h old whose blob must also disappear.
            db.ClipboardItems.Add(new ClipboardItemRow
            {
                Id = "stale-text", UserId = "u-aged", Type = "Text", OriginDeviceId = "d1",
                CreatedUtc = now.AddHours(-2), Payload = new byte[] { 1 },
            });
            db.ClipboardItems.Add(new ClipboardItemRow
            {
                Id = "fresh-text", UserId = "u-aged", Type = "Text", OriginDeviceId = "d1",
                CreatedUtc = now.AddMinutes(-10), Payload = new byte[] { 2 },
            });
            db.ClipboardItems.Add(new ClipboardItemRow
            {
                Id = "stale-file", UserId = "u-aged", Type = "File", OriginDeviceId = "d1",
                CreatedUtc = now.AddHours(-2), Preview = "stale.bin", SizeBytes = 3,
            });
            await db.SaveChangesAsync();
        }

        // Stage a blob under the (userId, itemId) the File lives at — the purge MUST delete it.
        var blobs = NewBlobStore();
        await blobs.SaveAsync("u-aged", "stale-file", new MemoryStream(new byte[] { 9, 9, 9 }));

        var service = BuildService(factory, blobs);

        var purged = await service.PurgeOnceAsync(now);

        // The 2 stale items (stale-text + stale-file) are purged; fresh-text survives the window.
        purged.Should().Be(2, "the two stale items are deleted in one pass");

        await using (var db = await factory.CreateDbContextAsync())
        {
            var ids = await db.ClipboardItems.Select(x => x.Id).ToListAsync();
            ids.Should().Contain("fresh-text");
            ids.Should().NotContain("stale-text");
            ids.Should().NotContain("stale-file");
        }

        (await blobs.OpenReadAsync("u-aged", "stale-file")).Should().BeNull(
            "the File's blob is best-effort cleaned up after the row is deleted");
    }

    [Fact]
    public async Task Purge_falls_back_to_server_default_when_user_has_no_window()
    {
        var factory = DbFactory();
        var now = DateTimeOffset.UtcNow;

        // Seed a user with NO override (ClipboardRetentionHours = null). Server default is 24h, so
        // a 30h-old item is purged and a 2h-old one survives.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRow
            {
                Id = "u-default",
                Provider = "microsoft",
                Subject = "subject-default",
                PrimaryEmail = "default@test",
                CreatedUtc = now,
                ClipboardRetentionHours = null,
            });
            db.ClipboardItems.Add(new ClipboardItemRow
            {
                Id = "very-old", UserId = "u-default", Type = "Text", OriginDeviceId = "d1",
                CreatedUtc = now.AddHours(-30), Payload = new byte[] { 1 },
            });
            db.ClipboardItems.Add(new ClipboardItemRow
            {
                Id = "still-here", UserId = "u-default", Type = "Text", OriginDeviceId = "d1",
                CreatedUtc = now.AddHours(-2), Payload = new byte[] { 2 },
            });
            await db.SaveChangesAsync();
        }

        var service = BuildService(factory, NewBlobStore(), new ClipboardOptions
        {
            RetentionMaxAge = TimeSpan.FromHours(24),
        });

        await service.PurgeOnceAsync(now);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var ids = await db.ClipboardItems.Select(x => x.Id).ToListAsync();
            ids.Should().Contain("still-here");
            ids.Should().NotContain("very-old");
        }
    }
}