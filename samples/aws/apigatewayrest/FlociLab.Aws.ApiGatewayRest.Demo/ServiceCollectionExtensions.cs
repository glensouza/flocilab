using FlociLab.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlociLab.Aws.ApiGatewayRest;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The sample's entire public surface toward a host (docs/BLAZOR-PLAN.md §3, constraint 4):
    ///
    /// <code>
    /// builder.Services
    ///     .AddFlociCore(builder.Configuration)
    ///     .AddAwsApiGatewayRestDemo();
    /// </code>
    ///
    /// The page, the route and the nav entry all come with it — a host adds a ProjectReference and
    /// this line, and nothing else. There is no capability registration — API Gateway REST has no
    /// capability interface, so it appears only in its own provider's nav, not on a comparison
    /// page.
    /// </summary>
    public static IServiceCollection AddAwsApiGatewayRestDemo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Not relying on AddFlociCore already registering IHttpClientFactory — the demo's own
        // invoke step needs it (plain HTTP against the deployed stage's execute-api URL, not the
        // SDK), and AddHttpClient() is idempotent so this is safe alongside Core's own call.
        services.AddHttpClient();

        services.TryAddSingleton<ApiGatewayRestClientFactory>();

        // Registered by concrete type as well as by interface, because ApiGatewayRestPage injects
        // ApiGatewayRestDemo directly — a page that owns one service has no use for the whole
        // catalog, and the interface registration below forwards to the same instance rather than
        // building a second one.
        services.TryAddSingleton<ApiGatewayRestDemo>();

        // TryAddEnumerable, not TryAddSingleton: the catalog resolves IEnumerable<IServiceDemo>,
        // so every sample has to be additive. TryAddSingleton would see another sample's
        // IServiceDemo already registered and silently drop this one; plain AddSingleton would
        // register API Gateway REST twice if a host called this method twice. TryAddEnumerable
        // de-duplicates on the implementation type, which is both.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IServiceDemo, ApiGatewayRestDemo>(sp => sp.GetRequiredService<ApiGatewayRestDemo>()));

        return services;
    }
}
