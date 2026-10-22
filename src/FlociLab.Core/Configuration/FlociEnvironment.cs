namespace FlociLab.Core.Configuration;

/// <summary>
/// Applies the same <c>Floci__*</c> environment variables the hosts bind from configuration.
/// A file-based <c>lab.cs</c> has no <c>appsettings.json</c> and calls this from
/// <c>AddFlociCore</c>. Unset variables leave the defaults alone.
/// </summary>
public static class FlociEnvironment
{
    public static void Apply(FlociOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Assign(options.Aws, "Floci__Aws__Endpoint");
        Assign(options.Azure, "Floci__Azure__Endpoint");
        Assign(options.Gcp, "Floci__Gcp__Endpoint");
        Assign(options.Oci, "Floci__Oci__Endpoint");
        AssignString("Floci__Aws__Region", value => options.Aws.Region = value);
        AssignString("Floci__Azure__AccountName", value => options.Azure.AccountName = value);
        AssignString("Floci__Gcp__ProjectId", value => options.Gcp.ProjectId = value);
        AssignString("Floci__Oci__Region", value => options.Oci.Region = value);
        AssignString("Floci__Oci__VaultId", value => options.Oci.VaultId = value);
        AssignString("Floci__Oci__KeyId", value => options.Oci.KeyId = value);
        AssignBool("Floci__EnableContainerBackedServices", value => options.EnableContainerBackedServices = value);
    }

    private static void Assign(EmulatorOptions options, string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        if (!string.IsNullOrWhiteSpace(value))
        {
            options.Endpoint = value;
        }
    }

    private static void AssignString(string name, Action<string> apply)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value);
        }
    }

    private static void AssignBool(string name, Action<bool> apply)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        if (bool.TryParse(value, out bool parsed))
        {
            apply(parsed);
        }
    }
}
