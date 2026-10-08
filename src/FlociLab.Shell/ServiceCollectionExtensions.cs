using System.Reflection;
using FlociLab.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace FlociLab.Shell;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared chrome for a host: the sidebar brand, and the host's own assembly as a
    /// page assembly, so its <c>Home</c> is routed by both the endpoint table and the circuit's
    /// Router. The shell's own pages (<c>/coverage</c>, <c>/Error</c>, <c>/not-found</c>) come with
    /// <see cref="App"/>, whose assembly is the app assembly in both.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="hostAssembly">The host's own assembly, <c>typeof(Program).Assembly</c>.</param>
    /// <param name="title">The sidebar brand, e.g. <c>FlociLab AWS</c>.</param>
    /// <param name="subtitle">The line under the brand.</param>
    public static IServiceCollection AddFlociShell(this IServiceCollection services, Assembly hostAssembly, string title, string subtitle)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hostAssembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(subtitle);

        // The shell's assembly is already the app assembly; declaring it again as a page assembly
        // registers /coverage twice and fails routing at runtime rather than here.
        if (hostAssembly == typeof(App).Assembly)
        {
            throw new ArgumentException("Pass the host's own assembly, typeof(Program).Assembly, not the shell's.", nameof(hostAssembly));
        }

        // A second call would silently replace the brand; one host, one shell.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ShellOptions)))
        {
            throw new InvalidOperationException("AddFlociShell has already been called for this host.");
        }

        services.AddSingleton(new ShellOptions(hostAssembly, title, subtitle));
        return services.AddPageAssembly(hostAssembly);
    }

    /// <summary>
    /// Adds <typeparamref name="TComponent"/> to the shared sidebar, under the fixed links. For a
    /// page-only RCL to call from its own registration, alongside <c>AddPageAssembly</c>.
    /// </summary>
    public static IServiceCollection AddNavSection<TComponent>(this IServiceCollection services) where TComponent : IComponent
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new NavSection(typeof(TComponent)));
        return services;
    }
}
