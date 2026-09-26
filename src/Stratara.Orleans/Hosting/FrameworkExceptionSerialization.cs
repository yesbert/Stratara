using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Stratara.Orleans.Hosting;

/// <summary>
/// Lets the framework's own exceptions cross from one silo to another with their type. Orleans serializes an
/// exception with its type only when the type's namespace is one it supports; any other arrives as a different
/// exception, so a caller on another silo — a bus worker that forwarded a command, a transport deciding whether to
/// deliver a message again — could not tell a concurrency conflict, or a save that committed but could not publish,
/// from any other failure. Every exception type is let through, not only the framework's: a framework exception
/// carries the provider's failure as its inner exception — a database or a broker exception — and a chain with one
/// type Orleans refuses does not cross at all, so the caller would see a serialization failure instead. The type,
/// the message, the stack trace and the inner exceptions cross; other properties do not, and the framework's own read
/// as empty on the far side. The filter only decides what a silo sends: what a silo accepts is Orleans' own type
/// filter, which this does not widen. A host that replaces the filter after this registration takes that decision
/// over, and must let the framework's exception chains through.
/// </summary>
internal static class FrameworkExceptionSerialization
{
    public const string NamespacePrefix = "Stratara.";

    public static void Register(IServiceCollection services) =>
        services.Configure<ExceptionSerializationOptions>(options =>
        {
            options.SupportedNamespacePrefixes.Add(NamespacePrefix);
            var hosts = options.SupportedExceptionTypeFilter;
            options.SupportedExceptionTypeFilter = type =>
                typeof(Exception).IsAssignableFrom(type) || (hosts?.Invoke(type) ?? false);
        });
}
