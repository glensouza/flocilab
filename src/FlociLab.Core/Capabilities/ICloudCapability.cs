namespace FlociLab.Core.Capabilities;

/// <summary>
/// Base of the five capability interfaces. A sample implements one of them only where a genuine
/// cross-cloud analog exists (docs/BLAZOR-PLAN.md §6) — services with no analog simply don't
/// appear in the comparison pages. the FlociLab.Comparison RCL consumes these and never
/// references a provider SDK.
/// </summary>
public interface ICloudCapability
{
    /// <summary>"aws" | "azure" | "gcp" | "oci" — the comparison column this fills.</summary>
    string Provider { get; }

    /// <summary>The real service behind the column, e.g. "Amazon S3", "Azure Blob Storage".</summary>
    string ServiceName { get; }

    /// <summary>
    /// Repo-relative path of the file that implements this column, so the comparison cell can
    /// link to it. Null when a column has not recorded one.
    /// </summary>
    string? SourcePath => null;

    /// <summary>
    /// Classifies an exception thrown by this capability's own SDK into one of the four
    /// <see cref="ProbeStatus"/> outcomes.
    ///
    /// <para>
    /// It lives on the capability because only the sample knows its SDK's exception types:
    /// <c>ProbeResult.FromException</c> deliberately handles transport-level cases only, and a 501
    /// arrives as <c>AmazonServiceException.StatusCode</c>, <c>RequestFailedException.Status</c>,
    /// <c>GoogleApiException.HttpStatusCode</c> or <c>OciException.StatusCode</c> depending on who
    /// threw it. FlociLab.Comparison consumes these interfaces and references no SDK, so without
    /// this it has no way to tell a documented 501 from a broken sample and would paint both red.
    /// </para>
    ///
    /// <para>
    /// Every implementation delegates to the same classifier its <see cref="IServiceDemo"/> uses,
    /// so /coverage and the comparison pages cannot disagree about the same operation — which is
    /// the point. A 501 is a documented outcome to record, never to work around
    /// (docs/BLAZOR-PLAN.md §10).
    /// </para>
    /// </summary>
    ProbeStatus Classify(Exception ex);
}
