using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.CodeAnalysis;

namespace Pool;

public static class DependencyInjectionExtensions
{
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPool<T>(
        this IServiceCollection services,
        IConfiguration configuration) where T : notnull, IDisposable
    {
        _ = services.Configure<PoolOptions>(configuration.GetSection(nameof(PoolOptions)));
        services.TryAddSingleton<IPool<T>, Pool<T>>();

        return services;
    }

    /// <summary>
    /// for unit testing
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    internal static IServiceCollection AddTransientPool<T>(
        this IServiceCollection services,
        IConfiguration configuration) where T : notnull, IDisposable
    {
        _ = services.Configure<PoolOptions>(configuration.GetSection(nameof(PoolOptions)));
        services.TryAddTransient<IPool<T>, Pool<T>>();

        return services;
    }
}
