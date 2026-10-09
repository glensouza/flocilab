using FlociLab.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlociLab.Aws.AppSync;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The sample's entire public surface toward a host (docs/BLAZOR-PLAN.md §3, constraint 4):
    ///
    /// <code>
    /// builder.Services
    ///     .AddFlociCore(builder.Configuration)
    ///     .AddAwsAppSyncDemo();
    /// </code>
    ///
    /// There is no capability registration — AppSync has no capability interface.
    /// </summary>
    public static IServiceCollection AddAwsAppSyncDemo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The query steps are plain HTTP against the GraphQL endpoint, not SDK calls; AddHttpClient
        // is idempotent alongside Core's own call.
        services.AddHttpClient();

        services.TryAddSingleton<AppSyncClientFactory>();

        // By concrete type as well as by interface: the page injects AppSyncDemo directly, and the
        // interface registration forwards to the same instance.
        services.TryAddSingleton<AppSyncDemo>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IServiceDemo, AppSyncDemo>(sp => sp.GetRequiredService<AppSyncDemo>()));

        return services;
    }
}
