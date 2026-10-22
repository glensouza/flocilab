using System.Reflection;
using FlociLab.Core.Configuration;
using FlociLab.Core.Coverage;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FlociLab.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Options binding, the four endpoint factories, the demo catalog and the coverage matrix.
    /// Every host calls this once, then chains one <c>Add*Demo()</c> per sample:
    ///
    /// <code>
    /// builder.Services
    ///     .AddFlociCore(builder.Configuration)
    ///     .AddAzureBlobDemo()
    ///     .AddAzureServiceBusDemo();
    /// </code>
    /// </summary>
    public static IServiceCollection AddFlociCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<FlociOptions>(configuration.GetSection(FlociOptions.SectionName));

        return services.AddFlociCoreServices();
    }

    /// <summary>
    /// Same, for a standalone sample host with no configuration file — the defaults in
    /// <see cref="FlociOptions"/> are the README's host-side ports.
    /// </summary>
    public static IServiceCollection AddFlociCore(
        this IServiceCollection services,
        Action<FlociOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }

        return services.AddFlociCoreServices();
    }

    /// <summary>
    /// Declares that <paramref name="assembly"/> owns routable pages. Only needed by an RCL that
    /// registers no <see cref="IServiceDemo"/> — see <see cref="PageAssembly"/>.
    ///
    /// <para>
    /// Plain <c>AddSingleton</c> rather than <c>TryAddEnumerable</c>: the latter de-duplicates on
    /// the implementation type, which is <see cref="PageAssembly"/> for every registration, so it
    /// would silently keep the first assembly and drop every one after it. The catalog distincts
    /// the assemblies instead, which also makes calling this twice harmless.
    /// </para>
    /// </summary>
    public static IServiceCollection AddPageAssembly(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        services.AddSingleton(new PageAssembly(assembly));

        return services;
    }

    private static IServiceCollection AddFlociCoreServices(this IServiceCollection services)
    {
        services.AddOptions<FlociOptions>();

        services.AddHttpClient(HttpEmulatorHealthProbe.HttpClientName, (provider, client) =>
        {
            client.Timeout = provider.GetRequiredService<IOptions<FlociOptions>>().Value.ProbeTimeout;
        });

        services.TryAddSingleton<AwsEndpoints>();
        services.TryAddSingleton<AzureEndpoints>();
        services.TryAddSingleton<GcpEndpoints>();
        services.TryAddSingleton<OciEndpoints>();

        services.TryAddSingleton<IEmulatorHealthProbe, HttpEmulatorHealthProbe>();
        services.TryAddSingleton<ICoverageSnapshotStore, CoverageSnapshotStore>();
        services.TryAddScoped<IDemoCatalog, DemoCatalog>();
        services.TryAddScoped<ICoverageMatrix, CoverageMatrix>();

        return services;
    }
}
