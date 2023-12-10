using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pool.DependencyInjection;
using Pool.Tests.Fakes;

namespace Pool.Tests;

public sealed class Startup
{
    private readonly IConfiguration configuration;

    public Startup()
    {
        configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "PoolOptions:MinSize", "1" },
                { "PoolOptions:MaxSize", "1" }
            })
            .Build();
    }

    public void ConfigureServices(IServiceCollection services)
    {
        _ = services.AddTransientPool<IEcho, EchoFactory>(configuration);
    }
}
