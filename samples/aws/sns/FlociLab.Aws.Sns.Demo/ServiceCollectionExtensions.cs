using FlociLab.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlociLab.Aws.Sns;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The sample's entire public surface toward a host (docs/BLAZOR-PLAN.md §3, constraint 4):
    ///
    /// <code>
    /// builder.Services
    ///     .AddFlociCore(builder.Configuration)
    ///     .AddAwsSnsDemo();
    /// </code>
    ///
    /// The page, the route and the nav entry all come with it — a host adds a ProjectReference and
    /// this line, and nothing else.
    /// </summary>
    public static IServiceCollection AddAwsSnsDemo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<SnsClientFactory>();

        // Registered by concrete type as well as by interface, because SnsPage injects SnsDemo
        // directly — a page that owns one service has no use for the whole catalog, and the
        // resolved-by-interface registration below forwards to the same instance rather than
        // building a second one. No capability registration — SNS has no capability interface;
        // fan-out pub/sub has no genuine cross-cloud analog in this catalog.
        services.TryAddSingleton<SnsDemo>();

        // TryAddEnumerable, not TryAddSingleton: the catalog resolves IEnumerable<T>, so every
        // sample has to be additive. TryAddSingleton would see another sample's IServiceDemo
        // already registered and silently drop this one; plain AddSingleton would register SNS
        // twice if a host called this method twice. TryAddEnumerable de-duplicates on the
        // implementation type, which is both.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IServiceDemo, SnsDemo>(sp => sp.GetRequiredService<SnsDemo>()));

        return services;
    }
}
