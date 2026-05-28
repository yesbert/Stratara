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
}
