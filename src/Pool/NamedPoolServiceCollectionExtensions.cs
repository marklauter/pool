using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pool.DefaultStrategies;
using Pool.Metrics;
using System.Diagnostics.CodeAnalysis;

namespace Pool;

/// <summary>
/// Extension methods for setting up named pools in an <see cref="IServiceCollection"/>.
/// </summary>
public static class NamedPoolServiceCollectionExtensions
{
    /// <summary>
    /// Adds a factory service that provides instances of <see cref="IPool{TPoolItem}"/>.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddPoolFactory<TPoolItem>(this IServiceCollection services)
        where TPoolItem : class
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPoolFactory<TPoolItem>, PoolFactory<TPoolItem>>();
        return services;
    }

    /// <summary>
    /// Adds a named <see cref="IPool{TPoolItem}"/> instance to the specified <see cref="IServiceCollection"/>
    /// and registers a typed client that uses this pool.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <typeparam name="TClient">
    /// The type of the typed client. This client will be registered with the DI container
    /// and will have the appropriate pool injected into its constructor.
    /// </typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="configuration">The <see cref="IConfiguration"/> to bind options from.</param>
    /// <param name="configureOptions">The action to configure the <see cref="PoolOptions"/>.</param>
    /// <param name="configureClient">The action to configure the client.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "<Pending>")]
    public static IServiceCollection AddPool<TPoolItem, TClient>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<PoolOptions>? configureOptions = null,
        Action<TClient>? configureClient = null)
        where TPoolItem : class
        where TClient : class
    {
        var name = typeof(TClient).FullName ?? typeof(TClient).Name;
        services
            .AddNamedPool<TPoolItem>(name, configuration, configureOptions)
            .TryAddTransient(serviceProvider =>
            {
                var poolFactory = serviceProvider.GetRequiredService<IPoolFactory<TPoolItem>>();
                var serviceKey = ServiceKey<TPoolItem>(name);
                var pool = poolFactory.CreatePool(serviceKey);
                var client = ActivatorUtilities.CreateInstance<TClient>(serviceProvider, pool);
                configureClient?.Invoke(client);
                return client;
            });

        return services;
    }

    /// <summary>
    /// Adds a named <see cref="IPool{TPoolItem}"/> instance to the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="name">The name of the pool.</param>
    /// <param name="configuration">The <see cref="IConfiguration"/> to bind options from.</param>
    /// <param name="configureOptions">The action to configure the <see cref="PoolOptions"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "<Pending>")]
    public static IServiceCollection AddNamedPool<TPoolItem>(
        this IServiceCollection services,
        string name,
        IConfiguration configuration,
        Action<PoolOptions>? configureOptions = null)
        where TPoolItem : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceKey = ServiceKey<TPoolItem>(name);

        var options = configuration.GetSection($"{serviceKey}_{nameof(PoolOptions)}").Get<PoolOptions>()
            ?? configuration.GetSection(nameof(PoolOptions)).Get<PoolOptions>()
            ?? new PoolOptions();

        configureOptions?.Invoke(options);

        _ = services
            .AddPoolFactory<TPoolItem>()
            .AddKeyedSingleton(serviceKey, options)
            .AddDefaultPreparationStrategy<TPoolItem>(options, serviceKey)
            .AddDefaultItemFactory<TPoolItem>(options, serviceKey)
            .AddDefaultPoolMetrics<TPoolItem>(name)
            .AddKeyedSingleton<IPool<TPoolItem>>(serviceKey, (services, serviceKey) =>
            {
                var options = services.GetRequiredKeyedService<PoolOptions>(serviceKey);
                var metrics = services.GetRequiredKeyedService<IPoolMetrics>(serviceKey);
                var itemFactory =
                    services.GetKeyedService<IItemFactory<TPoolItem>>(serviceKey)
                    ?? services.GetRequiredService<IItemFactory<TPoolItem>>();
                var preparationStrategy =
                    services.GetKeyedService<IPreparationStrategy<TPoolItem>>(serviceKey)
                    ?? services.GetService<IPreparationStrategy<TPoolItem>>();

                return new Pool<TPoolItem>(metrics, itemFactory, preparationStrategy, options);
            });

        return services;
    }

    private static string ServiceKey<TPoolItem>(string name)
        where TPoolItem : class
        => $"{name}.{typeof(TPoolItem).Name}.pool";

    private static IServiceCollection AddDefaultPreparationStrategy<TPoolItem>(
        this IServiceCollection services,
        PoolOptions options,
        string serviceKey)
        where TPoolItem : class
    {
        if (options.UseDefaultPreparationStrategy)
        {
            services.TryAddKeyedSingleton<IPreparationStrategy<TPoolItem>, DefaultPreparationStrategy<TPoolItem>>(serviceKey);
        }

        return services;
    }

    private static IServiceCollection AddDefaultItemFactory<TPoolItem>(
        this IServiceCollection services,
        PoolOptions options,
        string serviceKey)
        where TPoolItem : class
    {
        if (options.UseDefaultFactory)
        {
            services.TryAddKeyedSingleton<IItemFactory<TPoolItem>, DefaultItemFactory<TPoolItem>>(serviceKey);
        }

        return services;
    }

    /// <summary>
    /// Registers <see cref="IPoolMetrics"/> with the default implementation.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="name">The name of the pool.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddDefaultPoolMetrics<TPoolItem>(
        this IServiceCollection services,
        string name)
        where TPoolItem : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));

        var serviceKey = ServiceKey<TPoolItem>(name);

        services.TryAddKeyedSingleton<IPoolMetrics>(serviceKey,
            (services, serviceKey) => new DefaultPoolMetrics($"{name}.{Pool<TPoolItem>.PoolName}", services.GetRequiredService<ILogger<DefaultPoolMetrics>>()));
        return services;
    }
}
