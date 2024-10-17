using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pool.Tests.Fakes;

namespace Pool.Tests;

public sealed class Startup
{
    private readonly IConfiguration configuration;

    public Startup() => configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "PoolOptions:PreparationRequired", "true" },
                { "PoolOptions:MinSize", "1" },
                { "PoolOptions:MaxSize", "1" },
                { "PoolOptions:LeaseTimeout", "00:00:00.01" },
                { "PoolOptions:PreparationTimeout", "00:00:00.01" },
            }!)
            .Build();

    public void ConfigureServices(IServiceCollection services) => _ = services
        .AddTestPool<IEcho, string, EchoFactory, EchoFactory, EchoConnectionFactory>(configuration);
}
