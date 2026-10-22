using FlociLab.Core.Coverage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FlociLab.Shell;

public static class CoverageSnapshotEndpoint
{
    /// <summary>
    /// <c>GET /coverage.json</c> returns the last probe this process took. <c>?refresh=true</c>
    /// probes again, which is how a release diff can be taken without opening the page.
    /// </summary>
    public static IEndpointRouteBuilder MapCoverageSnapshot(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/coverage.json", async (IServiceProvider services, CancellationToken ct, bool refresh = false) =>
        {
            ICoverageSnapshotStore store = services.GetRequiredService<ICoverageSnapshotStore>();

            if (!refresh && store.Latest is { } cached)
            {
                return Results.Json(cached);
            }

            using IServiceScope scope = services.CreateScope();
            IDemoCatalog catalog = scope.ServiceProvider.GetRequiredService<IDemoCatalog>();
            IEmulatorHealthProbe healthProbe = scope.ServiceProvider.GetRequiredService<IEmulatorHealthProbe>();
            ICoverageMatrix matrix = scope.ServiceProvider.GetRequiredService<ICoverageMatrix>();

            Task<IReadOnlyList<EmulatorHealth>> healthTask = healthProbe.ProbeAsync(catalog.CoveredProviders, ct);
            Task<IReadOnlyList<DemoCoverage>> coverageTask = matrix.ProbeAllAsync(ct);
            await Task.WhenAll(healthTask, coverageTask).ConfigureAwait(false);

            CoverageSnapshot snapshot = store.Save(await healthTask.ConfigureAwait(false), await coverageTask.ConfigureAwait(false));
            return Results.Json(snapshot);
        });

        return endpoints;
    }
}
