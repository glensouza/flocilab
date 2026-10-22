namespace FlociLab.Core.Configuration;

/// <summary>
/// Bound from the "Floci" configuration section (docs/BLAZOR-PLAN.md §7). Defaults are the
/// host-side ports from the Compose stack in the README, so the app works with no configuration
/// at all; the AppHost overrides them with the live container endpoints, and running inside the
/// Compose network is a matter of setting Floci__Aws__Endpoint=http://floci:4566 and friends.
/// </summary>
public sealed class FlociOptions
{
    public const string SectionName = "Floci";

    public AwsEmulatorOptions Aws { get; set; } = new();

    public AzureEmulatorOptions Azure { get; set; } = new();

    public GcpEmulatorOptions Gcp { get; set; } = new();

    public OciEmulatorOptions Oci { get; set; } = new();

    /// <summary>How long a single probe is given before it counts as unreachable.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Kind B services start real containers and are slow, so they stay out of an ordinary run.
    /// A demo opts in with <see cref="ContainerBackedServiceAttribute"/>; the catalog hides it
    /// while this is false. The AppHost reads the same flag to decide which hostname advertised
    /// URLs use: <c>127.0.0.1</c> for the host, or the container's network alias when this is true.
    /// </summary>
    public bool EnableContainerBackedServices { get; set; }

    /// <summary>
    /// Slugs reach here from <see cref="IServiceDemo.Provider"/> and from route values, and the
    /// rest of the codebase matches them case-insensitively (<c>DemoCatalog.Find</c>), so this
    /// does too — a case mismatch should not be a hard throw.
    /// </summary>
    public EmulatorOptions For(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return provider.ToLowerInvariant() switch
        {
            CloudProvider.Aws => this.Aws,
            CloudProvider.Azure => this.Azure,
            CloudProvider.Gcp => this.Gcp,
            CloudProvider.Oci => this.Oci,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider slug."),
        };
    }
}
