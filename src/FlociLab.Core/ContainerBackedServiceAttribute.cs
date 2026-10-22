namespace FlociLab.Core;

/// <summary>
/// Marks a Kind B demo: Lambda, RDS, EKS, Cloud Run, Functions, OKE and the rest, which start
/// real containers through the Docker socket. The catalog omits a demo carrying this attribute
/// unless <see cref="Configuration.FlociOptions.EnableContainerBackedServices"/> is set, so an
/// ordinary <c>F5</c> does not run one. The demo's page should also refuse to run while the flag
/// is off — hiding the nav entry is not the same as the route being unreachable.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ContainerBackedServiceAttribute : Attribute;
