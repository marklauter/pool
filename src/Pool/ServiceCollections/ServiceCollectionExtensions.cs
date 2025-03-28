﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pool.DefaultStrategies;
using Pool.Metrics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Pool;
#pragma warning restore IDE0130 // Namespace does not match folder structure

/// <summary>
/// IServiceCollection extensions for registering pool, factory, and ready check.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// AddPool registers <see cref="IPool{TPoolItem}"/>.
    /// Provide a configure action to specify whether or not to register default <see cref="IPreparationStrategy{TPoolItem}"/> and <see cref="IItemFactory{TPoolItem}"/> implementations.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <param name="services"><see cref="IServiceCollection"/></param>
    /// <param name="configuration"><see cref="IConfiguration"/></param>
    /// <param name="configureOptions"><see cref="Action{T}"/> and <see cref="PoolOptions"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPool<TPoolItem>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<PoolOptions>? configureOptions = null)
        where TPoolItem : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration
            .GetSection(nameof(PoolOptions))
            .Get<PoolOptions>()
            ?? new PoolOptions();

        configureOptions?.Invoke(options);

        services
            .AddDefaultPreparationStrategy<TPoolItem>(options)
            .AddDefaultItemFactory<TPoolItem>(options)
            .AddDefaultPoolMetrics<TPoolItem>()
            .TryAddSingleton(options);
        services.TryAddSingleton<IPool<TPoolItem>, Pool<TPoolItem>>();

        return services;
    }

    /// <summary>
    /// AddPreparationStrategy registers a custom <see cref="IPreparationStrategy{TPoolItem}"/> implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TPreparationStrategy"><see cref="IPreparationStrategy{TPoolItem}"/></typeparam>
    /// <param name="services"><see cref="IServiceCollection"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPreparationStrategy<TPoolItem, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPreparationStrategy>(
        this IServiceCollection services)
        where TPoolItem : class
        where TPreparationStrategy : class, IPreparationStrategy<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddSingleton<IPreparationStrategy<TPoolItem>, TPreparationStrategy>();
    }

    /// <summary>
    /// AddPoolItemFactory registers a custom <see cref="IItemFactory{TPoolItem}"/> implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TItemFactory"><see cref="IItemFactory{TPoolItem}"/></typeparam>
    /// <param name="services"><see cref="IServiceCollection"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPoolItemFactory<TPoolItem, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TItemFactory>(
        this IServiceCollection services)
        where TPoolItem : class
        where TItemFactory : class, IItemFactory<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddSingleton<IItemFactory<TPoolItem>, TItemFactory>();
    }

    /// <summary>
    /// AddDefaultPoolMetrics registers <see cref="IPoolMetrics"/> with the default implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <param name="services"><see cref="IServiceCollection"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddDefaultPoolMetrics<TPoolItem>(this IServiceCollection services)
        where TPoolItem : class
    {
        ArgumentNullException.ThrowIfNull(services);
        services
            .AddMetrics()
            .TryAddSingleton<IPoolMetrics>((services) =>
            new DefaultPoolMetrics(
                Pool<TPoolItem>.PoolName,
                services.GetRequiredService<IMeterFactory>(),
                services.GetRequiredService<ILogger<DefaultPoolMetrics>>()));
        return services;
    }

    /// <summary>
    /// AddTestPool is for unit testing only.
    /// </summary>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "the case is handled in the conditional compile directives above")]
    internal static IServiceCollection AddTestPool<
        TPoolItem,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TItemFactory,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPreparationStrategy>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TPoolItem : class
        where TItemFactory : class, IItemFactory<TPoolItem>
        where TPreparationStrategy : class, IPreparationStrategy<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration.GetSection(nameof(PoolOptions)).Get<PoolOptions>() ?? new PoolOptions());
        services.TryAddTransient<IItemFactory<TPoolItem>, TItemFactory>();
        services.TryAddTransient<IPreparationStrategy<TPoolItem>, TPreparationStrategy>();
        services.TryAddTransient<IPool<TPoolItem>, Pool<TPoolItem>>();
        return services.AddDefaultPoolMetrics<TPoolItem>();
    }

    private static IServiceCollection AddDefaultPreparationStrategy<TPoolItem>(this IServiceCollection services, PoolOptions options)
        where TPoolItem : class
    {
        if (options.UseDefaultPreparationStrategy)
        {
            services.TryAddSingleton<IPreparationStrategy<TPoolItem>, DefaultPreparationStrategy<TPoolItem>>();
        }

        return services;
    }

    private static IServiceCollection AddDefaultItemFactory<TPoolItem>(this IServiceCollection services, PoolOptions options)
        where TPoolItem : class
    {
        if (options.UseDefaultFactory)
        {
            services.TryAddSingleton<IItemFactory<TPoolItem>, DefaultItemFactory<TPoolItem>>();
        }

        return services;
    }
}
