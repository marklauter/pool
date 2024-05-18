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
    /// AddPool registers <see cref="IPool{TPoolItem}"/> with custom <see cref="IReadyCheck{TPoolItem}"/> and <see cref="IPoolItemFactory{TPoolItem}"/> implementations.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TFactoryImplementation"><see cref="IPoolItemFactory{TPoolItem}"/></typeparam>
    /// <typeparam name="TReadyCheckImplementation"><see cref="IReadyCheck{TPoolItem}"/></typeparam>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns><see cref="IServiceCollection"/></returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "<Pending>")]
    public static IServiceCollection AddPool<TPoolItem,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactoryImplementation,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReadyCheckImplementation>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TPoolItem : notnull
        where TFactoryImplementation : class, IPoolItemFactory<TPoolItem>
        where TReadyCheckImplementation : class, IReadyCheck<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration.GetSection(nameof(PoolOptions)).Get<PoolOptions>() ?? new PoolOptions());
        services.TryAddSingleton<IPoolItemFactory<TPoolItem>, TFactoryImplementation>();
        services.TryAddSingleton<IReadyCheck<TPoolItem>, TReadyCheckImplementation>();
        services.TryAddSingleton<IPool<TPoolItem>, Pool<TPoolItem>>();

        return services;
    }

    /// <summary>
    /// AddPool registers <see cref="IPool{TPoolItem}"/> with default <see cref="IReadyCheck{TPoolItem}"/> and <see cref="IPoolItemFactory{TPoolItem}"/> implementations.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns><see cref="IServiceCollection"/></returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "<Pending>")]
    public static IServiceCollection AddPool<TPoolItem>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TPoolItem : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration.GetSection(nameof(PoolOptions)).Get<PoolOptions>() ?? new PoolOptions());
        services.TryAddSingleton<IPoolItemFactory<TPoolItem>, DefaultPoolItemFactory<TPoolItem>>();
        services.TryAddSingleton<IReadyCheck<TPoolItem>, DefaultReadyCheck<TPoolItem>>();
        services.TryAddSingleton<IPool<TPoolItem>, Pool<TPoolItem>>();

        return services;
    }

    /// <summary>
    /// AddPool registers <see cref="IPool{TPoolItem}"/> with the default <see cref="IPoolItemFactory{TPoolItem}"/> implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TReadyCheckImplementation"><see cref="IReadyCheck{TPoolItem}"/></typeparam>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns><see cref="IServiceCollection"/></returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPoolWithDefaultFactory<
        TPoolItem,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReadyCheckImplementation>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TPoolItem : notnull
        where TReadyCheckImplementation : class, IReadyCheck<TPoolItem> =>
            services.AddPool<TPoolItem, DefaultPoolItemFactory<TPoolItem>, TReadyCheckImplementation>(configuration);

    /// <summary>
    /// AddPool registers <see cref="IPool{TPoolItem}"/> with the default <see cref="IPoolItemFactory{TPoolItem}"/> implementation.
    /// </summary>
    /// <typeparam name="TPoolItem"></typeparam>
    /// <typeparam name="TFactoryImplementation"><see cref="IPoolItemFactory{TPoolItem}"/></typeparam>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns><see cref="IServiceCollection"/></returns>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPoolWithDefaultReadyCheck<
        TPoolItem,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactoryImplementation>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TPoolItem : notnull
        where TFactoryImplementation : class, IPoolItemFactory<TPoolItem> =>
            services.AddPool<TPoolItem, TFactoryImplementation, DefaultReadyCheck<TPoolItem>>(configuration);

    /// <summary>
    /// AddTransientPool is for unit testing only.
    /// </summary>
#if NET7_0_OR_GREATER
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
#endif
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "the case is handled in the conditional compile directives above")]
    internal static IServiceCollection AddTransientPool<
        TPoolItem,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactoryImplementation,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReadyCheckImplementation>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TPoolItem : notnull
        where TFactoryImplementation : class, IPoolItemFactory<TPoolItem>
        where TReadyCheckImplementation : class, IReadyCheck<TPoolItem>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration.GetSection(nameof(PoolOptions)).Get<PoolOptions>() ?? new PoolOptions());
        services.TryAddTransient<IPoolItemFactory<TPoolItem>, TFactoryImplementation>();
        services.TryAddTransient<IReadyCheck<TPoolItem>, TReadyCheckImplementation>();
        services.TryAddTransient<IPool<TPoolItem>, Pool<TPoolItem>>();

        return services;
    }
}
