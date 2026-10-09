using FlociLab.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlociLab.Aws.Organizations;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The sample's entire public surface toward a host (docs/BLAZOR-PLAN.md §3, constraint 4):
    ///
    /// <code>
    /// builder.Services
    ///     .AddFlociCore(builder.Configuration)
    ///     .AddAwsOrganizationsDemo();
    /// </code>
    ///
    /// The page, the route and the nav entry all come with it — a host adds a ProjectReference and
    /// this line, and nothing else. There is no capability registration — AWS Organizations has no
    /// capability interface, so it appears only in its own provider's nav, not on a comparison
    /// page.
    /// </summary>
    public static IServiceCollection AddAwsOrganizationsDemo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<OrganizationsClientFactory>();

        // Registered by concrete type as well as by interface, because OrganizationsPage injects OrganizationsDemo
        // directly — a page that owns one service has no use for the whole catalog, and the
        // interface registration below forwards to the same instance rather than building a
        // second one.
        services.TryAddSingleton<OrganizationsDemo>();

        // TryAddEnumerable, not TryAddSingleton: the catalog resolves IEnumerable<IServiceDemo>,
        // so every sample has to be additive. TryAddSingleton would see another sample's
        // IServiceDemo already registered and silently drop this one; plain AddSingleton would
        // register AWS Organizations twice if a host called this method twice. TryAddEnumerable de-duplicates
        // on the implementation type, which is both.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IServiceDemo, OrganizationsDemo>(sp => sp.GetRequiredService<OrganizationsDemo>()));

        return services;
    }
}
