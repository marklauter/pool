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
/// IServiceCollection extensions for registering <see cref="UnboundedPool{TPoolItem}"/>, its factory,
/// and its ready check. Parallels <see cref="ServiceCollectionExtensions"/> and
/// <see cref="NamedPoolServiceCollectionExtensions"/>, but binds <see cref="UnboundedPoolOptions"/>.
/// </summary>
public static class UnboundedPoolServiceCollectionExtensions
{
    /// <summary>
    /// AddUnboundedPool registers <see cref="IPool{TPoolItem}"/> backed by <see cref="UnboundedPool{TPoolItem}"/>.
    /// Provide a configure action to specify whether or not to register default
    /// <see cref="IPreparationStrategy{TPoolItem}"/> and <see cref="IItemFactory{TPoolItem}"/> implementations.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <param name="services"><see cref="IServiceCollection"/></param>
    /// <param name="configuration"><see cref="IConfiguration"/></param>
    /// <param name="configureOptions"><see cref="Action{T}"/> and <see cref="UnboundedPoolOptions"/></param>
    /// <returns><see cref="IServiceCollection"/></returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddUnboundedPool<TPoolItem>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<UnboundedPoolOptions>? configureOptions = null)
        where TPoolItem : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // eager bind drives the registration-time UseDefault* decisions below
        var options = configuration
            .GetSection(nameof(UnboundedPoolOptions))
            .Get<UnboundedPoolOptions>()
            ?? new UnboundedPoolOptions();

        configureOptions?.Invoke(options);

        // the validated options pipeline is the runtime source the pool consumes:
        // ValidateDataAnnotations enforces the [Range] constraints, ValidateOnStart fails fast at host startup
        var optionsBuilder = services
            .AddOptions<UnboundedPoolOptions>()
            .Bind(configuration.GetSection(nameof(UnboundedPoolOptions)));
        if (configureOptions is not null)
        {
            _ = optionsBuilder.Configure(configureOptions);
        }

        _ = optionsBuilder
            .ValidateDataAnnotations()
            .ValidateOnStart();

        _ = services
            .AddDefaultPreparationStrategy<TPoolItem>(options)
            .AddDefaultItemFactory<TPoolItem>(options)
            .AddDefaultUnboundedPoolMetrics<TPoolItem>();
        services.TryAddSingleton(static sp => sp.GetRequiredService<IOptions<UnboundedPoolOptions>>().Value);
        services.TryAddSingleton<IPool<TPoolItem>, UnboundedPool<TPoolItem>>();

        return services;
    }

    /// <summary>
    /// Adds a named <see cref="IPool{TPoolItem}"/> backed by <see cref="UnboundedPool{TPoolItem}"/>
    /// and registers a typed client that uses this pool.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <typeparam name="TClient">The type of the typed client that has the pool injected into its constructor.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="configuration">The <see cref="IConfiguration"/> to bind options from.</param>
    /// <param name="configureOptions">The action to configure the <see cref="UnboundedPoolOptions"/>.</param>
    /// <param name="configureClient">The action to configure the client.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "matches the bounded named-pool registration path")]
    public static IServiceCollection AddUnboundedPool<TPoolItem, TClient>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<UnboundedPoolOptions>? configureOptions = null,
        Action<TClient>? configureClient = null)
        where TPoolItem : class
        where TClient : class
    {
        var name = typeof(TClient).FullName ?? typeof(TClient).Name;
        services
            .AddNamedUnboundedPool<TPoolItem>(name, configuration, configureOptions)
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
    /// Adds a named <see cref="IPool{TPoolItem}"/> backed by <see cref="UnboundedPool{TPoolItem}"/>.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="name">The name of the pool.</param>
    /// <param name="configuration">The <see cref="IConfiguration"/> to bind options from.</param>
    /// <param name="configureOptions">The action to configure the <see cref="UnboundedPoolOptions"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "matches the bounded named-pool registration path")]
    public static IServiceCollection AddNamedUnboundedPool<TPoolItem>(
        this IServiceCollection services,
        string name,
        IConfiguration configuration,
        Action<UnboundedPoolOptions>? configureOptions = null)
        where TPoolItem : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceKey = ServiceKey.Create<TPoolItem>(name);

        // eager bind drives the registration-time UseDefault* decisions below
        var options = configuration.GetSection($"{serviceKey}_{nameof(UnboundedPoolOptions)}").Get<UnboundedPoolOptions>()
            ?? configuration.GetSection(nameof(UnboundedPoolOptions)).Get<UnboundedPoolOptions>()
            ?? new UnboundedPoolOptions();

        configureOptions?.Invoke(options);

        // named, validated options pipeline keyed by serviceKey; the pool resolves it via IOptionsMonitor.
        // bind the general section first, then let the pool-specific section override it per-property.
        var optionsBuilder = services
            .AddOptions<UnboundedPoolOptions>(serviceKey)
            .Bind(configuration.GetSection(nameof(UnboundedPoolOptions)))
            .Bind(configuration.GetSection($"{serviceKey}_{nameof(UnboundedPoolOptions)}"));
        if (configureOptions is not null)
        {
            _ = optionsBuilder.Configure(configureOptions);
        }

        _ = optionsBuilder
            .ValidateDataAnnotations()
            .ValidateOnStart();

        _ = services
            .AddPoolFactory<TPoolItem>()
            // the keyed UnboundedPoolOptions stays resolvable, but now delegates to the validated
            // named-options pipeline above (single source of truth) instead of a raw, unvalidated instance
            .AddKeyedSingleton(serviceKey, static (serviceProvider, key) =>
                serviceProvider.GetRequiredService<IOptionsMonitor<UnboundedPoolOptions>>().Get((string)key!))
            .AddDefaultPreparationStrategy<TPoolItem>(options, serviceKey)
            .AddDefaultItemFactory<TPoolItem>(options, serviceKey)
            .AddDefaultUnboundedPoolMetrics<TPoolItem>(name)
            .AddKeyedSingleton<IPool<TPoolItem>>(serviceKey, (services, serviceKey) =>
            {
                var itemFactory =
                    services.GetKeyedService<IItemFactory<TPoolItem>>(serviceKey)
                    ?? services.GetRequiredService<IItemFactory<TPoolItem>>();

                var logger =
                    services.GetKeyedService<ILogger<UnboundedPool<TPoolItem>>>(serviceKey)
                    ?? services.GetRequiredService<ILogger<UnboundedPool<TPoolItem>>>();

                var metrics =
                    services.GetRequiredKeyedService<IPoolMetrics>(serviceKey);

                var preparationStrategy =
                    services.GetKeyedService<IPreparationStrategy<TPoolItem>>(serviceKey)
                    ?? services.GetService<IPreparationStrategy<TPoolItem>>();

                var options =
                    services.GetRequiredKeyedService<UnboundedPoolOptions>(serviceKey);

                return new UnboundedPool<TPoolItem>(itemFactory, logger, metrics, preparationStrategy, options);
            });

        return services;
    }

    /// <summary>
    /// Registers <see cref="IPoolMetrics"/> with the default implementation, named for the unbounded pool.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddDefaultUnboundedPoolMetrics<TPoolItem>(this IServiceCollection services)
        where TPoolItem : class
    {
        ArgumentNullException.ThrowIfNull(services);
        services
            .AddMetrics()
            .TryAddSingleton<IPoolMetrics>((services) =>
            new DefaultPoolMetrics(
                UnboundedPool<TPoolItem>.PoolName,
                services.GetRequiredService<IMeterFactory>(),
                services.GetRequiredService<ILogger<DefaultPoolMetrics>>()));
        return services;
    }

    /// <summary>
    /// Registers <see cref="IPoolMetrics"/> with the default implementation, named for the unbounded pool.
    /// </summary>
    /// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="name">The name of the pool.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddDefaultUnboundedPoolMetrics<TPoolItem>(
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
                $"{name}.{UnboundedPool<TPoolItem>.PoolName}",
                services.GetRequiredService<IMeterFactory>(),
                services.GetRequiredService<ILogger<DefaultPoolMetrics>>()));
        return services;
    }

    private static IServiceCollection AddDefaultPreparationStrategy<TPoolItem>(this IServiceCollection services, UnboundedPoolOptions options)
        where TPoolItem : class
    {
        if (options.UseDefaultPreparationStrategy)
        {
            services.TryAddSingleton<IPreparationStrategy<TPoolItem>, DefaultPreparationStrategy<TPoolItem>>();
        }

        return services;
    }

    private static IServiceCollection AddDefaultItemFactory<TPoolItem>(this IServiceCollection services, UnboundedPoolOptions options)
        where TPoolItem : class
    {
        if (options.UseDefaultFactory)
        {
            services.TryAddSingleton<IItemFactory<TPoolItem>, DefaultItemFactory<TPoolItem>>();
        }

        return services;
    }

    private static IServiceCollection AddDefaultPreparationStrategy<TPoolItem>(
        this IServiceCollection services,
        UnboundedPoolOptions options,
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
        UnboundedPoolOptions options,
        string serviceKey)
        where TPoolItem : class
    {
        if (options.UseDefaultFactory)
        {
            services.TryAddKeyedSingleton<IItemFactory<TPoolItem>, DefaultItemFactory<TPoolItem>>(serviceKey);
        }

        return services;
    }
}
