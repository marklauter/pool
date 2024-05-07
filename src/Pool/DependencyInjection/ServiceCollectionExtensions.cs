using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.CodeAnalysis;

namespace Pool.DependencyInjection;
/// <summary>
/// IServiceCollection extensions for registering pool, factory, and ready check.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// AddPool registers <see cref="IPool{TPoolItem}"/> with a custom <see cref="IPoolItemFactory{TPoolItem}"/> implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TFactoryImplementation"></typeparam>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns><see cref="IServiceCollection"/></returns>
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

    /// <summary>
    /// AddPool registers <see cref="IPool{TPoolItem}"/> with the default <see cref="IPoolItemFactory{TPoolItem}"/> implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns><see cref="IServiceCollection"/></returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPoolWithDefaultFactory<TPoolItem>(
        this IServiceCollection services,
        IConfiguration configuration)
    where TPoolItem : notnull
    {
        return services.AddPool<TPoolItem, DefaultPoolItemFactory<TPoolItem>>(configuration);
    }

    /// <summary>
    /// AddReadyCheck registers <see cref="IReadyCheck{TPoolItem}"/> with a custom implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TReadyCheckImplementation"></typeparam>
    /// <param name="services"></param>
    /// <returns><see cref="IServiceCollection"/></returns>
    public static IServiceCollection AddReadyCheck<TPoolItem, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReadyCheckImplementation>(this IServiceCollection services)
        where TPoolItem : notnull
        where TReadyCheckImplementation : class, IReadyCheck<TPoolItem>
    {
        services.TryAddSingleton<IReadyCheck<TPoolItem>, TReadyCheckImplementation>();
        return services;
    }

    /// <summary>
    /// AddTransientPool is for unit testing only.
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
