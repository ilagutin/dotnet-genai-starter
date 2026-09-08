namespace GenAIPlatform.IntegrationTests;

[CollectionDefinition("PostgreSQL repository", DisableParallelization = true)]
public sealed class PostgresRepositoryCollection
    : ICollectionFixture<PostgresRepositoryFixture>
{
    public const string CollectionName = "PostgreSQL repository";
}
