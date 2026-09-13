using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// Pins finding SF-003 of change <c>prove-an-orleans-execution-model</c>: the event source recognises a
/// duplicate stream version only through the PostgreSQL unique-violation state, so on the SQLite test
/// host the same collision surfaces as a plain <see cref="DbUpdateException"/> rather than as
/// <see cref="ConcurrencyException"/>. The test records the behaviour; it does not endorse it. When the
/// detection moves behind a provider port, this test flips to expecting <see cref="ConcurrencyException"/>.
/// </summary>
public class EventSourceSqliteConcurrencyTests
{
    private sealed class Counter
    {
        public int Value { get; set; }
    }

    private sealed record Incremented(int By);

    [Fact]
    public async Task SaveChangesAsync_OnSqliteDuplicateVersion_SurfacesDbUpdateException_NotConcurrencyException()
    {
        await using var host = EventStoreTestHost.Create(services => services
            .AddTrustedType<Counter>()
            .AddTrustedType<Incremented>());
        var streamId = Guid.CreateVersion7();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Counter>(streamId, new Incremented(1));
            await events.SaveChangesAsync();
        });

        await using var first = host.Services.CreateAsyncScope();
        await using var second = host.Services.CreateAsyncScope();
        var firstWriter = first.ServiceProvider.GetRequiredService<IEventSource>();
        var secondWriter = second.ServiceProvider.GetRequiredService<IEventSource>();

        await firstWriter.AppendAsync<Counter>(streamId, new Incremented(2));
        await secondWriter.AppendAsync<Counter>(streamId, new Incremented(3));
        await firstWriter.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => secondWriter.SaveChangesAsync());

        Assert.IsNotType<ConcurrencyException>(ex);
        Assert.Contains("UNIQUE", ex.InnerException?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
