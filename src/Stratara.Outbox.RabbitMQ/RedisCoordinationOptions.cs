namespace Stratara.Outbox.RabbitMQ;

/// <summary>
/// Options naming the product whose hosts coordinate over a shared Redis: the projection replay state and the
/// Redis-leased outbox lock. <c>AddProjectionReplayState()</c> and <c>AddRedisOutboxLock()</c> read them from the
/// configuration section <see cref="SectionName"/> (<c>"RedisCoordination"</c>) of the host's configuration; a service
/// collection that holds no configuration gets the defaults. A value set with
/// <c>services.Configure&lt;RedisCoordinationOptions&gt;(...)</c> after those calls takes precedence over the section.
/// </summary>
public sealed class RedisCoordinationOptions
{
    /// <summary>Configuration section name (<c>"RedisCoordination"</c>) used to bind these options.</summary>
    public const string SectionName = "RedisCoordination";

    private const string Root = "stratara:";

    /// <summary>
    /// The name of the product this host belongs to, carried by every Redis key and channel the framework writes.
    /// Products with different names share a Redis without seeing each other's replay requests, replay state or outbox
    /// lock. Every host of a product must have the same name, compared exactly, so <c>Acme</c> and <c>acme</c> are
    /// different products; a host with no name or another name does not coordinate with it, so set the name on all
    /// hosts of a product together. Null or empty, the default, keeps the names of earlier releases. May hold letters,
    /// digits, <c>.</c>, <c>_</c> and <c>-</c>; anything else is refused when the host starts.
    /// </summary>
    public string? ProductName { get; set; }

    internal string Name(string fixedName) =>
        string.IsNullOrEmpty(ProductName) ? fixedName : $"{Root}{ProductName}:{fixedName[Root.Length..]}";
}
