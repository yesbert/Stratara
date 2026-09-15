namespace Stratara.Orleans.IntegrationTests.Fixtures;

/// <summary>One PostgreSQL, one Redis and one RabbitMQ container for every test class in the collection.</summary>
[CollectionDefinition(Name)]
public sealed class InfrastructureCollection :
    ICollectionFixture<PostgreSqlFixture>,
    ICollectionFixture<RedisFixture>,
    ICollectionFixture<RabbitMqFixture>
{
    public const string Name = nameof(InfrastructureCollection);
}
