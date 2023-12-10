using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pool.Tests;

public sealed class Startup
{
    private readonly IConfiguration configuration;

    public Startup()
    {
        configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "PoolOptions:MinSize", "10" },
                { "PoolOptions:MaxSize", "20" }
            })
            .Build();
    }

    public void ConfigureServices(IServiceCollection services)
    {
        _ = services.AddTransient<IEcho, Echo>();
        _ = services.AddTransientPool<IEcho>(configuration);
    }
}
