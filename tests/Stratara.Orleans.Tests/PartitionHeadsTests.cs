using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.CommitOrder;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The silo's shared partition heads: readers that poll together cost the store one question per partition, a head
/// is asked again once it is older than the poll allows, and a reader whose head would walk the partition is never
/// asked, so every poll reads as before.
/// </summary>
public sealed class PartitionHeadsTests
{
    [Fact]
    public async Task Readers_that_ask_together_cost_the_store_one_question_per_partition()
    {
        var reader = new HeadReader(head: 7) { Gate = new TaskCompletionSource() };
        var heads = HeadsOver(reader);

        var asking = Enumerable.Range(0, 20).Select(_ => heads.HasAnythingPastAsync(3, 7, TimeSpan.FromMinutes(1))).ToList();
        reader.Gate.SetResult();
        var answers = await Task.WhenAll(asking);

        Assert.All(answers, Assert.False);
        Assert.Equal(1, reader.Questions);
        Assert.True(await heads.HasAnythingPastAsync(3, 6, TimeSpan.FromMinutes(1)));
        Assert.Equal(1, reader.Questions);
    }

    [Fact]
    public async Task A_head_older_than_the_poll_allows_is_asked_again()
    {
        var reader = new HeadReader(head: 7);
        var heads = HeadsOver(reader);

        Assert.False(await heads.HasAnythingPastAsync(0, 7, TimeSpan.FromMilliseconds(10)));
        reader.Head = 9;
        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.True(await heads.HasAnythingPastAsync(0, 7, TimeSpan.FromMilliseconds(10)));
        Assert.Equal(2, reader.Questions);
    }

    [Fact]
    public async Task A_head_that_cannot_be_read_lets_the_poll_read()
    {
        var heads = HeadsOver(new HeadReader(head: 7) { Failure = new InvalidOperationException("the store is away") });

        Assert.True(await heads.HasAnythingPastAsync(0, 100, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task A_reader_that_leaves_its_head_to_the_interface_is_never_asked()
    {
        var reader = new WalkingReader();
        var heads = HeadsOver(reader);

        Assert.False(PartitionHeads.AnswersHeadInOneQuery(reader));
        Assert.True(PartitionHeads.AnswersHeadInOneQuery(new HeadReader(head: 0)));
        Assert.True(await heads.HasAnythingPastAsync(0, 100, TimeSpan.FromMinutes(1)));
        Assert.True(await heads.HasAnythingPastAsync(0, 100, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, reader.Reads);
    }

    private static PartitionHeads HeadsOver(ICommittedPositionReader reader) =>
        new(new ServiceCollection().AddSingleton(reader).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    private sealed class HeadReader(long head) : ICommittedPositionReader
    {
        private int _questions;

        public long Head { get; set; } = head;

        public TaskCompletionSource? Gate { get; init; }

        public Exception? Failure { get; init; }

        public int Questions => Volatile.Read(ref _questions);

        public string Name => "head/16";

        public Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(CommittedBatch.Empty(afterPosition));

        public async Task<long> HeadAsync(int partition, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _questions);
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return Failure is { } failure ? throw failure : Head;
        }
    }

    private sealed class WalkingReader : ICommittedPositionReader
    {
        public int Reads { get; private set; }

        public string Name => "walking/16";

        public Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(CommittedBatch.Empty(afterPosition));
        }
    }
}
