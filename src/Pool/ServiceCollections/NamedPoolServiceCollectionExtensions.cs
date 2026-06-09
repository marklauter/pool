using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pool.DefaultStrategies;
using Pool.Metrics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Pool;
#pragma warning restore IDE0130 // Namespace does not match folder structure

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
                var serviceKey = ServiceKey.Create<TPoolItem>(name);
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

        var serviceKey = ServiceKey.Create<TPoolItem>(name);

        // eager bind drives the registration-time UseDefault* decisions below
        var options = configuration.GetSection($"{serviceKey}_{nameof(PoolOptions)}").Get<PoolOptions>()
            ?? configuration.GetSection(nameof(PoolOptions)).Get<PoolOptions>()
            ?? new PoolOptions();

        configureOptions?.Invoke(options);

        // named, validated options pipeline keyed by serviceKey; the pool resolves it via IOptionsMonitor.
        // bind the general section first, then let the pool-specific section override it per-property.
        var optionsBuilder = services
            .AddOptions<PoolOptions>(serviceKey)
            .Bind(configuration.GetSection(nameof(PoolOptions)))
            .Bind(configuration.GetSection($"{serviceKey}_{nameof(PoolOptions)}"));
        if (configureOptions is not null)
        {
            _ = optionsBuilder.Configure(configureOptions);
        }

        _ = optionsBuilder
            .ValidateDataAnnotations()
            .ValidateOnStart();

        _ = services
            .AddPoolFactory<TPoolItem>()
            // the keyed PoolOptions stays resolvable, but now delegates to the validated named-options
            // pipeline above (single source of truth) instead of holding a raw, unvalidated instance
            .AddKeyedSingleton(serviceKey, static (serviceProvider, key) =>
                serviceProvider.GetRequiredService<IOptionsMonitor<PoolOptions>>().Get((string)key!))
            .AddDefaultPreparationStrategy<TPoolItem>(options, serviceKey)
            .AddDefaultItemFactory<TPoolItem>(options, serviceKey)
            .AddDefaultPoolMetrics<TPoolItem>(name)
            .AddKeyedSingleton<IPool<TPoolItem>>(serviceKey, (services, serviceKey) =>
            {
                var itemFactory =
                    services.GetKeyedService<IItemFactory<TPoolItem>>(serviceKey)
                    ?? services.GetRequiredService<IItemFactory<TPoolItem>>();

                var logger =
                    services.GetKeyedService<ILogger<Pool<TPoolItem>>>(serviceKey)
                    ?? services.GetRequiredService<ILogger<Pool<TPoolItem>>>();

                var metrics =
                    services.GetRequiredKeyedService<IPoolMetrics>(serviceKey);

                var preparationStrategy =
                    services.GetKeyedService<IPreparationStrategy<TPoolItem>>(serviceKey)
                    ?? services.GetService<IPreparationStrategy<TPoolItem>>();

                var options =
                    services.GetRequiredKeyedService<PoolOptions>(serviceKey);

                return new Pool<TPoolItem>(itemFactory, logger, metrics, preparationStrategy, options);
            });

        return services;
    }

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

        var serviceKey = ServiceKey.Create<TPoolItem>(name);
        services
            .AddMetrics()
            .TryAddKeyedSingleton<IPoolMetrics>(serviceKey,
            (services, serviceKey) =>
            new DefaultPoolMetrics(
                $"{name}.{Pool<TPoolItem>.PoolName}",
                services.GetRequiredService<IMeterFactory>(),
                services.GetRequiredService<ILogger<DefaultPoolMetrics>>()));
        return services;
    }
}
