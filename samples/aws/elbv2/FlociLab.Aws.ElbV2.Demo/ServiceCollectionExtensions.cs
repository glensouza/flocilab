using FlociLab.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlociLab.Aws.ElbV2;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The sample's entire public surface toward a host (docs/BLAZOR-PLAN.md §3, constraint 4):
    ///
    /// <code>
    /// builder.Services
    ///     .AddFlociCore(builder.Configuration)
    ///     .AddAwsElbV2Demo();
    /// </code>
    ///
    /// The page, the route and the nav entry all come with it — a host adds a ProjectReference and
    /// this line, and nothing else. There is no capability registration — ELB v2 has no capability
    /// interface, so it appears only in its own provider's nav, not on a comparison page.
    /// </summary>
    public static IServiceCollection AddAwsElbV2Demo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ElbV2ClientFactory>();

        // Registered by concrete type as well as by interface, because ElbV2Page injects ElbV2Demo
        // directly — a page that owns one service has no use for the whole catalog, and the
        // interface registration below forwards to the same instance rather than building a
        // second one.
        services.TryAddSingleton<ElbV2Demo>();

        // TryAddEnumerable, not TryAddSingleton: the catalog resolves IEnumerable<IServiceDemo>,
        // so every sample has to be additive. TryAddSingleton would see another sample's
        // IServiceDemo already registered and silently drop this one; plain AddSingleton would
        // register ELB v2 twice if a host called this method twice. TryAddEnumerable de-duplicates
        // on the implementation type, which is both.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IServiceDemo, ElbV2Demo>(sp => sp.GetRequiredService<ElbV2Demo>()));

        return services;
    }
}
