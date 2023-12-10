using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pool.Tests;

internal interface IEcho
    : IDisposable
{
    string Shout(string message);
}

internal sealed class Echo
    : IEcho
{
    public void Dispose()
    {
        // nothing to do
    }

    public string Shout(string message)
    {
        return message;
    }
}

public sealed class Startup
{
    private readonly IConfiguration configuration;

    public Startup()
    {
        configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "PoolOptions:InitialSize", "10" },
                { "PoolOptions:MaxSize", "20" }
            })
            .Build();
    }

    public void ConfigureServices(IServiceCollection services)
    {
        _ = services.AddTransient<IEcho, Echo>();
        _ = services.AddPool<IEcho>(configuration);
    }
}

public class PoolTests
{
    [Fact]
    public void Test1()
    {

    }
}
