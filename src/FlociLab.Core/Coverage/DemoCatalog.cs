using System.Reflection;
using FlociLab.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlociLab.Core.Coverage;

internal sealed class DemoCatalog : IDemoCatalog
{
    public DemoCatalog(
        IEnumerable<IServiceDemo> demos,
        IEnumerable<PageAssembly> pageAssemblies,
        IOptions<FlociOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Kind B demos start containers. They stay out of the nav, the coverage grid and the
        // comparison columns until the flag says otherwise. The route still exists; the attribute's
        // remarks say the page has to refuse the run itself.
        IEnumerable<IServiceDemo> visible = options.Value.EnableContainerBackedServices
            ? demos
            : demos.Where(demo => demo.GetType().GetCustomAttribute<ContainerBackedServiceAttribute>() is null);

        this.Demos = [.. visible
            .OrderBy(d => CloudProvider.Order(d.Provider))
            .ThenBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)];

        this.ByProvider = [.. CloudProvider.All
            .Select(p => new ProviderDemos(p, [.. this.Demos.Where(d => d.Provider == p)]))
            .Where(g => g.Demos.Count > 0)];

        // Nothing registered yet means "show me the emulators anyway" rather than an empty page:
        // that is the whole value of /coverage before the first sample exists.
        this.CoveredProviders = this.ByProvider.Count == 0
            ? CloudProvider.All
            : [.. this.ByProvider.Select(g => g.Provider)];

        // Demos first so the nav-bearing sample assemblies keep their existing order, then the
        // declared page-only assemblies. Distinct because an RCL may both own a demo and declare
        // itself, and because AddPageAssembly does not de-duplicate.
        this.PageAssemblies = [.. this.Demos
            .Select(d => d.GetType().Assembly)
            .Concat(pageAssemblies.Select(a => a.Assembly))
            .Distinct()];
    }

    public IReadOnlyList<IServiceDemo> Demos { get; }

    public IReadOnlyList<ProviderDemos> ByProvider { get; }

    public IReadOnlyList<string> CoveredProviders { get; }

    public IReadOnlyList<Assembly> PageAssemblies { get; }

    public IServiceDemo? Find(string provider, string slug)
        => this.Demos.FirstOrDefault(d =>
            string.Equals(d.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
