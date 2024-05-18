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
    /// AddPool registers <see cref="IPool{TPoolItem}"/>.
    /// Provide a configure action to specify whether or not to register default <see cref="IPoolItemReadyCheck{TPoolItem}"/> and <see cref="IPoolItemFactory{TPoolItem}"/> implementations.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <param name="services"><see cref="IServiceCollection"/></param>
    /// <param name="configuration"><see cref="IConfiguration"/></param>
    /// <param name="configure"><see cref="Action{T}"/> and <see cref="PoolRegistrationOptions"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "<Pending>")]
    public static IServiceCollection AddPool<TPoolItem>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<PoolRegistrationOptions>? configure = null)
        where TPoolItem : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new PoolRegistrationOptions();
        configure?.Invoke(options);

        if (options.UseDefaultReadyCheck)
        {
            services.TryAddSingleton<IPoolItemReadyCheck<TPoolItem>, DefaultReadyCheck<TPoolItem>>();
        }

        if (options.UseDefaultFactory)
        {
            services.TryAddSingleton<IPoolItemFactory<TPoolItem>, DefaultPoolItemFactory<TPoolItem>>();
        }

        services.TryAddSingleton(configuration.GetSection(nameof(PoolOptions)).Get<PoolOptions>() ?? new PoolOptions());
        services.TryAddSingleton<IPool<TPoolItem>, Pool<TPoolItem>>();

        return services;
    }

    /// <summary>
    /// AddPoolItemReadyCheck registers a custom <see cref="IPoolItemReadyCheck{TPoolItem}"/> implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TReadyCheckImplementation"><see cref="IPoolItemReadyCheck{TPoolItem}"/></typeparam>
    /// <param name="services"><see cref="IServiceCollection"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPoolItemReadyCheck<TPoolItem, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReadyCheckImplementation>(
        this IServiceCollection services)
        where TPoolItem : notnull
        where TReadyCheckImplementation : class, IPoolItemReadyCheck<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPoolItemReadyCheck<TPoolItem>, TReadyCheckImplementation>();
        return services;
    }

    /// <summary>
    /// AddPoolItemFactory registers a custom <see cref="IPoolItemFactory{TPoolItem}"/> implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TFactoryImplementation"><see cref="IPoolItemFactory{TPoolItem}"/></typeparam>
    /// <param name="services"><see cref="IServiceCollection"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPoolItemFactory<TPoolItem, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactoryImplementation>(
        this IServiceCollection services)
        where TPoolItem : notnull
        where TFactoryImplementation : class, IPoolItemFactory<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPoolItemFactory<TPoolItem>, TFactoryImplementation>();
        return services;
    }

    /// <summary>
    /// AddTestPool is for unit testing only.
    /// </summary>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "the case is handled in the conditional compile directives above")]
    internal static IServiceCollection AddTestPool<
        TPoolItem,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactoryImplementation,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReadyCheckImplementation>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TPoolItem : notnull
        where TFactoryImplementation : class, IPoolItemFactory<TPoolItem>
        where TReadyCheckImplementation : class, IPoolItemReadyCheck<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration.GetSection(nameof(PoolOptions)).Get<PoolOptions>() ?? new PoolOptions());
        services.TryAddTransient<IPoolItemFactory<TPoolItem>, TFactoryImplementation>();
        services.TryAddTransient<IPoolItemReadyCheck<TPoolItem>, TReadyCheckImplementation>();
        services.TryAddTransient<IPool<TPoolItem>, Pool<TPoolItem>>();

        return services;
    }
}
