using MailKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pool;
using System.Diagnostics.CodeAnalysis;

namespace Smtp.Pool;

/// <summary>
/// Registration helpers that wire MailKit's SMTP client into the object pool.
/// </summary>
public static class SmtpClientPoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IPool{TPoolItem}"/> of <see cref="IMailTransport"/> backed by MailKit's
    /// SmtpClient, binding host, credential, client, and pool options from configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration containing the <c>SmtpHostOptions</c>, <c>SmtpClientCredentials</c>, <c>SmtpClientOptions</c>, and <c>PoolOptions</c> sections.</param>
    /// <param name="configureOptions">Optional override of the bound <see cref="PoolOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    [RequiresDynamicCode("dynamic binding of strongly typed options might require dynamic code")]
    [RequiresUnreferencedCode("dynamic binding of strongly typed options might require unreferenced code")]
    public static IServiceCollection AddSmtpClientPool(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<PoolOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        _ = services
            .AddOptions<SmtpHostOptions>()
            .Bind(configuration.GetSection(nameof(SmtpHostOptions)))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        _ = services
            .AddOptions<SmtpClientCredentials>()
            .Bind(configuration.GetSection(nameof(SmtpClientCredentials)))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        _ = services
            .AddOptions<SmtpClientOptions>()
            .Bind(configuration.GetSection(nameof(SmtpClientOptions)));

        return services
            .AddPoolItemFactory<IMailTransport, SmtpClientFactory>()
            .AddPreparationStrategy<IMailTransport, SmtpClientPreparationStrategy>()
            .AddPool<IMailTransport>(configuration, configureOptions);
    }
}
