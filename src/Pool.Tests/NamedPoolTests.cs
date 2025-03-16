using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pool.Tests;

public sealed class NamedPoolTests
{
    private sealed class PoolItem;

    private sealed class PoolClient(IPool<PoolItem> pool)
    {
        public IPool<PoolItem> Pool { get; } = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    [Fact]
    public void NamedPool_Registration_Succeeds()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "PoolOptions:UseDefaultFactory", "true" },
                { "PoolOptions:UseDefaultPreparationStrategy", "true" },
                { "PoolOptions:PreparationRequired", "true" },
                { "PoolOptions:MinSize", "1" },
                { "PoolOptions:MaxSize", "2" },
                { "PoolOptions:LeaseTimeout", "00:00:00.01" },
                { "PoolOptions:PreparationTimeout", "00:00:00.01" },
            }!)
            .Build();

        using var services = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddPool<PoolItem, PoolClient>(configuration)
            .BuildServiceProvider();

        var client = services.GetRequiredService<PoolClient>();
        Assert.NotNull(client.Pool);
    }
}
