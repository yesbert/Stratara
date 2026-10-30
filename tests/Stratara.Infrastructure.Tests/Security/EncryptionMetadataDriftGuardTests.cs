using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Infrastructure.Security.Serialization;

namespace Stratara.Infrastructure.Tests.Security;

public class EncryptionMetadataDriftGuardTests
{
    private sealed record PlainCommand(string Payload);

    [EncryptData]
    private sealed record EncryptedClassCommand(string Payload);

    private sealed record EncryptedPropertyCommand([property: EncryptData] string Secret, string Public);

    private sealed record EncryptedCtorParameterCommand([EncryptData] string Secret, string Public);

    [Fact]
    public async Task StartAsync_EmptyResolver_DoesNotThrow()
    {
        var resolver = new TrustedTypeResolver();
        var sut = new EncryptionMetadataDriftGuard(resolver);

        var exception = await Record.ExceptionAsync(async () => await sut.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAsync_PlainType_DoesNotThrow()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(PlainCommand));
        var sut = new EncryptionMetadataDriftGuard(resolver);

        var exception = await Record.ExceptionAsync(async () => await sut.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAsync_ClassEncrypted_DoesNotThrow()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(EncryptedClassCommand));
        var sut = new EncryptionMetadataDriftGuard(resolver);

        var exception = await Record.ExceptionAsync(async () => await sut.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAsync_PropertyEncrypted_DoesNotThrow()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(EncryptedPropertyCommand));
        var sut = new EncryptionMetadataDriftGuard(resolver);

        var exception = await Record.ExceptionAsync(async () => await sut.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAsync_CtorParameterEncrypted_DoesNotThrow()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(EncryptedCtorParameterCommand));
        var sut = new EncryptionMetadataDriftGuard(resolver);

        var exception = await Record.ExceptionAsync(async () => await sut.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutError()
    {
        var resolver = new TrustedTypeResolver();
        var sut = new EncryptionMetadataDriftGuard(resolver);

        var exception = await Record.ExceptionAsync(async () => await sut.StopAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [EncryptData]
    private sealed record NoteWrittenAsAWhole(string Text);

    private sealed record NoteWrittenFieldByField([property: EncryptData] string Text, string Title);

    private sealed class Notebook : IAggregate
    {
        public Guid Id { get; set; }

        public string? Text { get; set; }

        public void Apply(NoteWrittenFieldByField @event) => Text = @event.Text;

        public void Apply(IEvent<NoteWrittenAsAWhole> @event) => Text = @event.Data.Text;
    }

    private sealed class FieldByFieldNotebook : IAggregate
    {
        public Guid Id { get; set; }

        public string? Text { get; set; }

        public void Apply(NoteWrittenFieldByField @event) => Text = @event.Text;
    }

    [EncryptData]
    private sealed class SealedNotebook : IAggregate
    {
        public Guid Id { get; set; }

        public string? Text { get; set; }

        public void Apply(NoteWrittenFieldByField @event) => Text = @event.Text;
    }

    [Fact]
    public async Task StartAsync_AnEventAnAggregateApplies_MarkedAsAWhole_Throws()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(Notebook));
        var sut = new EncryptionMetadataDriftGuard(resolver);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(CancellationToken.None));

        Assert.Contains(typeof(NoteWrittenAsAWhole).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(NoteWrittenFieldByField).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("mark the properties", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_AnAggregateMarkedAsAWhole_Throws()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(SealedNotebook));
        var sut = new EncryptionMetadataDriftGuard(resolver);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(CancellationToken.None));

        Assert.Contains($"aggregate {typeof(SealedNotebook).FullName}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_AnAggregateWhoseEventsMarkTheirProperties_DoesNotThrow()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(FieldByFieldNotebook));
        resolver.Register(typeof(NoteWrittenFieldByField));
        var sut = new EncryptionMetadataDriftGuard(resolver);

        var exception = await Record.ExceptionAsync(async () => await sut.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }
}
