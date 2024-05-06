using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.CodeAnalysis;

namespace Pool.DependencyInjection;

public static class ServiceCollectionExtensions
{
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPool<TPoolItem, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactoryImplementation>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TPoolItem : notnull
        where TFactoryImplementation : class, IPoolItemFactory<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        _ = services
            .AddOptions()
            .Configure<PoolOptions>(configuration.GetSection(nameof(PoolOptions)));
        services.TryAddSingleton<IPoolItemFactory<TPoolItem>, TFactoryImplementation>();
        services.TryAddSingleton<IPool<TPoolItem>, Pool<TPoolItem>>();

        return services;
    }

    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPoolWithDefaultFactory<TPoolItem>(
        this IServiceCollection services,
        IConfiguration configuration)
    where TPoolItem : notnull
    {
        return services.AddPool<TPoolItem, DefaultPoolItemFactory<TPoolItem>>(configuration);
    }

    public static IServiceCollection AddDefaultPoolItemFactory<TPoolItem>(this IServiceCollection services)
        where TPoolItem : notnull
    {
        services.TryAddSingleton<IPoolItemFactory<TPoolItem>, DefaultPoolItemFactory<TPoolItem>>();
        return services;
    }

    public static IServiceCollection AddReadyCheck<TPoolItem, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReadyCheck>(this IServiceCollection services)
        where TPoolItem : notnull
        where TReadyCheck : class, IReadyCheck<TPoolItem>
    {
        services.TryAddSingleton<IReadyCheck<TPoolItem>, TReadyCheck>();
        return services;
    }

    /// <summary>
    /// for unit testing
    /// </summary>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    internal static IServiceCollection AddTransientPool<T, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactoryImplementation>(
        this IServiceCollection services,
        IConfiguration configuration)
        where T : notnull
        where TFactoryImplementation : class, IPoolItemFactory<T>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        _ = services
            .AddOptions()
            .Configure<PoolOptions>(configuration.GetSection(nameof(PoolOptions)));
        services.TryAddTransient<IPoolItemFactory<T>, TFactoryImplementation>();
        services.TryAddTransient<IPool<T>, Pool<T>>();

        return services;
    }
}
