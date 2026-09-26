using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Stratara.Orleans.Hosting;

/// <summary>
/// Lets the framework's own exceptions cross from one silo to another with their type. Orleans serializes an
/// exception with its type only when the type's namespace is one it supports; any other arrives as a different
/// exception, so a caller on another silo — a bus worker that forwarded a command, a transport deciding whether to
/// deliver a message again — could not tell a concurrency conflict, or a save that committed but could not publish,
/// from any other failure. The type, the message and the inner exception cross; properties of the framework's own
/// do not, and read as empty on the far side.
/// </summary>
internal static class FrameworkExceptionSerialization
{
    public const string NamespacePrefix = "Stratara";

    public static void Register(IServiceCollection services) =>
        services.Configure<ExceptionSerializationOptions>(options => options.SupportedNamespacePrefixes.Add(NamespacePrefix));
}
