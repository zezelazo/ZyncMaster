using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Devices;

// FU-1 — DeviceService.IsNameConflict must detect the unique-index violation by the provider's
// NUMERIC error code on the INNER exception, NOT by string-matching the message. These tests pin
// the detection to the SQLite codes (19 SQLITE_CONSTRAINT / 2067 SQLITE_CONSTRAINT_UNIQUE) that the
// in-memory test database raises, and prove the helper does not rely on the column/index name text.
public class DeviceNameConflictDetectionTests
{
    // Provokes the REAL (UserId, NameLower) unique-index violation against SQLite and returns the
    // DbUpdateException EF wraps it in. This is the exact exception RegisterAsync/RenameAsync catch.
    private static async Task<DbUpdateException> CaptureRealNameConflictAsync()
    {
        using var factory = new ServerTestFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();

        // Seed a user the two devices belong to, then two devices with the same NameLower.
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var user = await users.UpsertByLoginAsync(
            "local", "fu1-subject", "fu1@example.com", true, "FU1 User", CancellationToken.None);

        db.Devices.Add(new DeviceRow { Id = "fu1-a", UserId = user.Id, Name = "Frodo", NameLower = "frodo", ApiKeyHash = "h1" });
        db.Devices.Add(new DeviceRow { Id = "fu1-b", UserId = user.Id, Name = "frodo", NameLower = "frodo", ApiKeyHash = "h2" });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            return ex;
        }

        throw new Xunit.Sdk.XunitException("Expected the unique index to reject the duplicate insert.");
    }

    [Fact]
    public async Task IsNameConflict_detects_real_sqlite_unique_index_violation()
    {
        var ex = await CaptureRealNameConflictAsync();

        // The inner exception is a SqliteException carrying a constraint code; the helper must
        // recognise it. (This is the path the retry / name_taken flow depends on.)
        ex.InnerException.Should().BeOfType<SqliteException>();
        DeviceService.IsNameConflict(ex).Should().BeTrue();
    }

    [Fact]
    public async Task Real_violation_carries_the_constraint_codes_we_match_on()
    {
        var ex = await CaptureRealNameConflictAsync();

        // Detection is by CODE, not text: assert the actual codes are the ones the helper checks.
        var inner = (SqliteException)ex.InnerException!;
        (inner.SqliteErrorCode == 19 || inner.SqliteExtendedErrorCode == 2067)
            .Should().BeTrue("SQLITE_CONSTRAINT (19) / SQLITE_CONSTRAINT_UNIQUE (2067) identify the violation");
    }

    [Fact]
    public void Detection_does_not_depend_on_message_text()
    {
        // A SqliteException with the unique-constraint code but a message that contains NEITHER the
        // column name nor the index name must STILL be detected — proving we match on the code only.
        var inner = new SqliteException("opaque database error", 19, 2067);

        DeviceService.IsUniqueConstraintViolation(inner).Should().BeTrue();
    }

    [Fact]
    public void Non_constraint_sqlite_error_is_not_a_name_conflict()
    {
        // SQLITE_ERROR (1) is a generic error, not a unique-constraint violation — must NOT match,
        // even though, were we matching on text, an arbitrary message could accidentally contain
        // "NameLower".
        var inner = new SqliteException("NameLower mentioned but not a constraint", 1, 1);

        DeviceService.IsUniqueConstraintViolation(inner).Should().BeFalse();
    }

    [Fact]
    public void Unrelated_inner_exception_is_not_a_name_conflict()
    {
        DeviceService.IsUniqueConstraintViolation(new InvalidOperationException("boom")).Should().BeFalse();
    }
}
