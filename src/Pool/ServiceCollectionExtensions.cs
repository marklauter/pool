using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.CodeAnalysis;

namespace Pool;

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
    /// <param name="configure"><see cref="Action{T}"/> and <see cref="ItemLeaseOptions"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "<Pending>")]
    public static IServiceCollection AddPool<TPoolItem>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<ItemLeaseOptions>? configure = null)
        where TPoolItem : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ItemLeaseOptions();
        configure?.Invoke(options);

        if (options.UseDefaultPreparationStrategy)
        {
            services.TryAddSingleton<IPreparationStrategy<TPoolItem>, DefaultPreparationStrategy<TPoolItem>>();
        }

        if (options.UseDefaultFactory)
        {
            services.TryAddSingleton<IItemFactory<TPoolItem>, DefaultItemFactory<TPoolItem>>();
        }

        services.TryAddSingleton(configuration.GetSection(nameof(PoolOptions)).Get<PoolOptions>() ?? new PoolOptions());
        services.TryAddSingleton<IPool<TPoolItem>, Pool<TPoolItem>>();

        return services;
    }

    /// <summary>
    /// AddPoolItemReadyCheck registers a custom <see cref="IPreparationStrategy{TPoolItem}"/> implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TReadyCheckImplementation"><see cref="IPreparationStrategy{TPoolItem}"/></typeparam>
    /// <param name="services"><see cref="IServiceCollection"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPoolItemReadyCheck<TPoolItem, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReadyCheckImplementation>(
        this IServiceCollection services)
        where TPoolItem : class
        where TReadyCheckImplementation : class, IPreparationStrategy<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPreparationStrategy<TPoolItem>, TReadyCheckImplementation>();
        return services;
    }

    /// <summary>
    /// AddPoolItemFactory registers a custom <see cref="IItemFactory{TPoolItem}"/> implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TFactoryImplementation"><see cref="IItemFactory{TPoolItem}"/></typeparam>
    /// <param name="services"><see cref="IServiceCollection"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPoolItemFactory<TPoolItem, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactoryImplementation>(
        this IServiceCollection services)
        where TPoolItem : class
        where TFactoryImplementation : class, IItemFactory<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IItemFactory<TPoolItem>, TFactoryImplementation>();
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
        where TPoolItem : class
        where TFactoryImplementation : class, IItemFactory<TPoolItem>
        where TReadyCheckImplementation : class, IPreparationStrategy<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration.GetSection(nameof(PoolOptions)).Get<PoolOptions>() ?? new PoolOptions());
        services.TryAddTransient<IItemFactory<TPoolItem>, TFactoryImplementation>();
        services.TryAddTransient<IPreparationStrategy<TPoolItem>, TReadyCheckImplementation>();
        services.TryAddTransient<IPool<TPoolItem>, Pool<TPoolItem>>();

        return services;
    }
}
