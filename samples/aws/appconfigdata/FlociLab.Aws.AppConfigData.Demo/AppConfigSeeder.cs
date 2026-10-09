using System.Globalization;
using System.Net;
using System.Text;
using Amazon.AppConfig;
using Amazon.AppConfig.Model;
using Amazon.Runtime;

namespace FlociLab.Aws.AppConfigData;

/// <summary>
/// Writes the configuration this sample reads: an application, an environment, a hosted profile,
/// hosted versions, and a deployment. That is the AppConfig control plane, so it is
/// <c>AWSSDK.AppConfig</c>, the sample's second package (see the csproj and docs/BLAZOR-PLAN.md §14).
/// Everything the demo is *about* — the session and the polling — is AWSSDK.AppConfigData; this
/// class is scaffolding, and is only ever pointed at the emulator.
/// </summary>
internal sealed class AppConfigSeeder(IAmazonAppConfig client) : IDisposable
{
    /// <summary>The identifiers a session needs.</summary>
    internal sealed record Seeded(string ApplicationId, string EnvironmentId, string ProfileId);

    public void Dispose() => client.Dispose();

    /// <summary>Creates the application, the environment and the profile, named for this run.</summary>
    public async Task<Seeded> CreateAsync(string name, CancellationToken ct)
    {
        string application = (await client.CreateApplicationAsync(new CreateApplicationRequest { Name = name }, ct).ConfigureAwait(false)).Id;
        string environment = (await client.CreateEnvironmentAsync(new CreateEnvironmentRequest { ApplicationId = application, Name = "prod" }, ct).ConfigureAwait(false)).Id;
        string profile = (await client.CreateConfigurationProfileAsync(
            new CreateConfigurationProfileRequest { ApplicationId = application, Name = "checkout", LocationUri = "hosted" }, ct).ConfigureAwait(false)).Id;

        return new Seeded(application, environment, profile);
    }

    /// <summary>Publishes a hosted version and returns its number.</summary>
    public async Task<int> PublishAsync(Seeded target, string json, CancellationToken ct)
    {
        using MemoryStream content = new(Encoding.UTF8.GetBytes(json));
        CreateHostedConfigurationVersionResponse response = await client.CreateHostedConfigurationVersionAsync(
            new CreateHostedConfigurationVersionRequest
            {
                ApplicationId = target.ApplicationId,
                ConfigurationProfileId = target.ProfileId,
                Content = content,
                ContentType = "application/json",
            }, ct).ConfigureAwait(false);

        return response.VersionNumber.GetValueOrDefault();
    }

    /// <summary>Deploys a version with the built-in all-at-once strategy and returns the state floci reports.</summary>
    public async Task<string> DeployAsync(Seeded target, int version, CancellationToken ct)
    {
        StartDeploymentResponse response = await client.StartDeploymentAsync(
            new StartDeploymentRequest
            {
                ApplicationId = target.ApplicationId,
                EnvironmentId = target.EnvironmentId,
                ConfigurationProfileId = target.ProfileId,
                ConfigurationVersion = version.ToString(CultureInfo.InvariantCulture),
                DeploymentStrategyId = "AppConfig.AllAtOnce",
            }, ct).ConfigureAwait(false);

        return response.State?.Value ?? string.Empty;
    }

    /// <summary>The ids of the applications named <paramref name="name"/>, across every page; names are unique per run.</summary>
    public async Task<List<string>> FindAsync(string name, CancellationToken ct)
        => (await AllAsync(async next =>
            {
                ListApplicationsResponse page = await client.ListApplicationsAsync(new ListApplicationsRequest { NextToken = next }, ct).ConfigureAwait(false);

                return (page.Items, page.NextToken);
            }).ConfigureAwait(false))
            .Where(a => a.Name == name)
            .Select(a => a.Id)
            .ToList();

    /// <summary>
    /// Deletes an application inside out — hosted versions, profiles, environments, the
    /// application — and says what it removed. floci has no DeleteEnvironment (the AppConfig
    /// sample pins that), so an environment that cannot be deleted is counted, not thrown.
    /// </summary>
    public async Task<(int Versions, int Profiles, int Environments, int EnvironmentsKept)> DeleteAsync(string applicationId, CancellationToken ct)
    {
        int versions = 0;
        int profiles = 0;
        int environments = 0;
        int kept = 0;

        List<ConfigurationProfileSummary> profileList = await AllAsync(async next =>
        {
            ListConfigurationProfilesResponse page = await client.ListConfigurationProfilesAsync(
                new ListConfigurationProfilesRequest { ApplicationId = applicationId, NextToken = next }, ct).ConfigureAwait(false);

            return (page.Items, page.NextToken);
        }).ConfigureAwait(false);

        foreach (ConfigurationProfileSummary profile in profileList)
        {
            List<HostedConfigurationVersionSummary> versionList = await AllAsync(async next =>
            {
                ListHostedConfigurationVersionsResponse page = await client.ListHostedConfigurationVersionsAsync(
                    new ListHostedConfigurationVersionsRequest { ApplicationId = applicationId, ConfigurationProfileId = profile.Id, NextToken = next }, ct).ConfigureAwait(false);

                return (page.Items, page.NextToken);
            }).ConfigureAwait(false);

            foreach (HostedConfigurationVersionSummary version in versionList)
            {
                await client.DeleteHostedConfigurationVersionAsync(
                    new DeleteHostedConfigurationVersionRequest { ApplicationId = applicationId, ConfigurationProfileId = profile.Id, VersionNumber = version.VersionNumber }, ct).ConfigureAwait(false);
                versions++;
            }

            await client.DeleteConfigurationProfileAsync(
                new DeleteConfigurationProfileRequest { ApplicationId = applicationId, ConfigurationProfileId = profile.Id }, ct).ConfigureAwait(false);
            profiles++;
        }

        List<string> environmentIds = (await AllAsync(async next =>
        {
            ListEnvironmentsResponse page = await client.ListEnvironmentsAsync(
                new ListEnvironmentsRequest { ApplicationId = applicationId, NextToken = next }, ct).ConfigureAwait(false);

            return (page.Items, page.NextToken);
        }).ConfigureAwait(false)).Select(e => e.Id).ToList();

        foreach (string environmentId in environmentIds)
        {
            try
            {
                await client.DeleteEnvironmentAsync(new DeleteEnvironmentRequest { ApplicationId = applicationId, EnvironmentId = environmentId }, ct).ConfigureAwait(false);
                environments++;
            }
            // Only floci's not-implemented answer: a 404 ResourceNotFoundException is a real
            // failure, and counting it here would hide the day floci builds DeleteEnvironment.
            catch (AmazonAppConfigException ex) when (IsNotImplemented(ex))
            {
                kept++;
            }
        }

        await client.DeleteApplicationAsync(new DeleteApplicationRequest { ApplicationId = applicationId }, ct).ConfigureAwait(false);

        return (versions, profiles, environments, kept);
    }

    private static bool IsNotImplemented(AmazonServiceException ex)
        => ex.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed || ex.ErrorCode == "UnknownOperationException";

    /// <summary>Every item of a paged list: AppConfig's list calls page with <c>NextToken</c>.</summary>
    private static async Task<List<T>> AllAsync<T>(Func<string?, Task<(List<T>? Items, string? NextToken)>> page)
    {
        List<T> all = [];
        string? next = null;

        do
        {
            (List<T>? items, string? token) = await page(next).ConfigureAwait(false);

            all.AddRange(items ?? []);
            next = token;
        }
        while (!string.IsNullOrEmpty(next));

        return all;
    }
}
