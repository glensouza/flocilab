using System.Reflection;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Coverage;

namespace FlociLab.Comparison;

/// <summary>Where a comparison cell sends a reader who wants the call, or the file that made it.</summary>
public static class ComparisonLinks
{
    public const string SourceRepository = "https://github.com/glensouza/flocilab/blob/main/";

    public static string? SourceUrl(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : SourceRepository + path;

    /// <summary>
    /// The demo that lives in the same assembly as the capability. Comparison columns are one
    /// service per assembly, so the first demo in that assembly is the page that shows the wire.
    /// </summary>
    public static string? DemoRoute(IDemoCatalog catalog, ICloudCapability capability)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(capability);

        Assembly assembly = capability.GetType().Assembly;
        IServiceDemo? demo = catalog.Demos.FirstOrDefault(candidate => candidate.GetType().Assembly == assembly);
        return demo?.Route;
    }
}
