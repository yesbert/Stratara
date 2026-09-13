using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Xunit;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

public class SqliteConflictDetectorTests
{
    private readonly SqliteConflictDetector _detector = new();

    [Fact]
    public void IsUniqueViolation_UniqueConstraintExtendedCode_IsRecognised()
    {
        var ex = new DbUpdateException("save failed", new SqliteException("SQLite Error 19: 'constraint failed'.", 19, 2067));

        Assert.True(_detector.IsUniqueViolation(ex));
    }

    [Fact]
    public void IsUniqueViolation_PrimaryKeyConstraintExtendedCode_IsRecognised()
    {
        var ex = new DbUpdateException("save failed", new SqliteException("SQLite Error 19: 'constraint failed'.", 19, 1555));

        Assert.True(_detector.IsUniqueViolation(ex));
    }

    [Fact]
    public void IsUniqueViolation_ConstraintCodeWithUniqueInMessage_IsRecognised()
    {
        var ex = new SqliteException("SQLite Error 19: 'UNIQUE constraint failed: event_stream_entry.version'.", 19);

        Assert.True(_detector.IsUniqueViolation(ex));
    }

    [Fact]
    public void IsUniqueViolation_OtherConstraint_IsNotRecognised()
    {
        var ex = new DbUpdateException("save failed", new SqliteException("SQLite Error 19: 'NOT NULL constraint failed'.", 19, 1299));

        Assert.False(_detector.IsUniqueViolation(ex));
    }

    [Fact]
    public void AddStrataraTestingEventStore_RegistersTheDetectorOnce()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var services = new ServiceCollection();

        services.AddStrataraTestingEventStore<StrataraTestWriteDbContext>(connection, Guid.NewGuid());
        services.AddStrataraTestingEventStore<StrataraTestWriteDbContext>(connection, Guid.NewGuid());

        var detector = Assert.Single(services, d => d.ServiceType == typeof(IStoreConflictDetector));
        Assert.Equal(typeof(SqliteConflictDetector), detector.ImplementationType);
    }
}
