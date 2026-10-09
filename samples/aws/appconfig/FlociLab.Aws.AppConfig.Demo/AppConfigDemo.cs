using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Amazon.AppConfig;
using Amazon.AppConfig.Model;
using Amazon.Runtime;
using FlociLab.Core;
using AppEnvironment = Amazon.AppConfig.Model.Environment;

namespace FlociLab.Aws.AppConfig;

/// <summary>
/// Creates an application, an environment and a hosted configuration profile, publishes two
/// versions of a JSON configuration, reads one back, lists the deployment strategies AppConfig
/// ships with, tries to deploy a version that was never created, deploys the first version, tags
/// the application, then tries what floci has not built: updating the application and deleting the
/// environment. Ordinary AWSSDK.AppConfig code — the only emulator wiring is in
/// <see cref="AppConfigClientFactory"/>; this class reads <c>UseEmulator</c> only to name floci's
/// gaps where it differs from AWS and to stop a deployment AWS is still baking. Every step reports
/// what the engine actually answered rather than what the docs promise.
/// </summary>
public sealed class AppConfigDemo(AppConfigClientFactory factory) : IServiceDemo
{
    /// <summary>A strategy AppConfig ships with, so the run creates none: floci cannot delete a strategy.</summary>
    private const string StrategyId = "AppConfig.AllAtOnce";

    private const string FlagsJson = """{"checkout":{"newFlow":false},"timeoutSeconds":30}""";

    private const string FlagsJsonV2 = """{"checkout":{"newFlow":true},"timeoutSeconds":30}""";

    private static readonly string[] Versions = [FlagsJson, FlagsJsonV2];

    public string Provider => CloudProvider.Aws;

    public string Slug => "appconfig";

    public string DisplayName => "AppConfig";

    public string Category => "Developer tools and delivery";

    public string Route => "/aws/appconfig";

    /// <summary>ListApplications — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonAppConfig client = factory.Create();
            ListApplicationsResponse response = await client.ListApplicationsAsync(new ListApplicationsRequest(), ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListApplications: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonAppConfig client = factory.Create();

        string name = $"flocilab-{Guid.NewGuid().ToString("N")[..12]}";

        // Claimed before the create: a call that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created the application, so the finally looks it up by
        // name rather than trusting that a response arrived.
        bool claimed = false;
        string? applicationId = null;
        string? environmentId = null;
        string? profileId = null;
        string? firstVersion = null;

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep created = await RunStepAsync(
                "CreateApplication, GetApplication — an application",
                this.Call("POST", "/applications", $"client.CreateApplicationAsync(new CreateApplicationRequest {{ Name = \"{name}\", Description = \"FlociLab AppConfig demo\" }})\nclient.GetApplicationAsync(new GetApplicationRequest {{ ApplicationId = id }})"),
                async () =>
                {
                    claimed = true;
                    CreateApplicationResponse response = await client.CreateApplicationAsync(
                        new CreateApplicationRequest { Name = name, Description = "FlociLab AppConfig demo" }, ct).ConfigureAwait(false);

                    applicationId = response.Id;

                    GetApplicationResponse found = await client.GetApplicationAsync(new GetApplicationRequest { ApplicationId = applicationId }, ct).ConfigureAwait(false);

                    return $"{found.Name} ({found.Id})";
                }).ConfigureAwait(false);

            yield return created;

            // A failed create means every later step would look for an application that is not there.
            // Skipped rather than `yield break`, which would also skip yielding the cleanup step below.
            if (created.Succeeded)
            {
                DemoStep environment = await RunStepAsync(
                    "CreateEnvironment, ListEnvironments — an environment",
                    this.Call("POST", $"/applications/{applicationId}/environments", "client.CreateEnvironmentAsync(new CreateEnvironmentRequest { ApplicationId = id, Name = \"prod\" })\nclient.ListEnvironmentsAsync(new ListEnvironmentsRequest { ApplicationId = id })"),
                    async () =>
                    {
                        CreateEnvironmentResponse response = await client.CreateEnvironmentAsync(
                            new CreateEnvironmentRequest { ApplicationId = applicationId, Name = "prod" }, ct).ConfigureAwait(false);

                        environmentId = response.Id;

                        List<AppEnvironment> found = (await client.ListEnvironmentsAsync(new ListEnvironmentsRequest { ApplicationId = applicationId }, ct).ConfigureAwait(false)).Items ?? [];

                        if (!found.Any(e => e.Id == environmentId))
                        {
                            throw new InvalidOperationException($"the environment was accepted but ListEnvironments lists {found.Count} environment(s) without it.");
                        }

                        return string.Join("\n", found.Select(e => $"{e.Name} ({e.Id}): {e.State?.Value}"));
                    }).ConfigureAwait(false);

                yield return environment;

                DemoStep profile = await RunStepAsync(
                    "CreateConfigurationProfile — a hosted profile",
                    this.Call("POST", $"/applications/{applicationId}/configurationprofiles", "client.CreateConfigurationProfileAsync(new CreateConfigurationProfileRequest { ApplicationId = id, Name = \"checkout\", LocationUri = \"hosted\" })"),
                    async () =>
                    {
                        CreateConfigurationProfileResponse response = await client.CreateConfigurationProfileAsync(
                            new CreateConfigurationProfileRequest { ApplicationId = applicationId, Name = "checkout", LocationUri = "hosted" }, ct).ConfigureAwait(false);

                        profileId = response.Id;

                        return $"{response.Name} ({response.Id}): {response.LocationUri}";
                    }).ConfigureAwait(false);

                yield return profile;

                if (profile.Succeeded)
                {
                    DemoStep hosted = await RunStepAsync(
                        "CreateHostedConfigurationVersion, GetHostedConfigurationVersion — two versions of a configuration",
                        this.Call("POST", $"/applications/{applicationId}/configurationprofiles/{profileId}/hostedconfigurationversions", "client.CreateHostedConfigurationVersionAsync(new CreateHostedConfigurationVersionRequest { ApplicationId = id, ConfigurationProfileId = profile, Content = json, ContentType = \"application/json\" }) — twice\nclient.GetHostedConfigurationVersionAsync(new GetHostedConfigurationVersionRequest { ..., VersionNumber = 2 })"),
                        async () =>
                        {
                            List<int> numbers = [];

                            foreach (string json in Versions)
                            {
                                using MemoryStream content = new(Encoding.UTF8.GetBytes(json));
                                CreateHostedConfigurationVersionResponse response = await client.CreateHostedConfigurationVersionAsync(
                                    new CreateHostedConfigurationVersionRequest
                                    {
                                        ApplicationId = applicationId,
                                        ConfigurationProfileId = profileId,
                                        Content = content,
                                        ContentType = "application/json",
                                    }, ct).ConfigureAwait(false);

                                numbers.Add(response.VersionNumber.GetValueOrDefault());
                            }

                            GetHostedConfigurationVersionResponse read = await client.GetHostedConfigurationVersionAsync(
                                new GetHostedConfigurationVersionRequest { ApplicationId = applicationId, ConfigurationProfileId = profileId, VersionNumber = numbers[1] }, ct).ConfigureAwait(false);

                            using StreamReader reader = new(read.Content, Encoding.UTF8);
                            string body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                            if (body != FlagsJsonV2)
                            {
                                throw new InvalidOperationException($"version {numbers[1]} was accepted but reads back as {body}.");
                            }

                            List<HostedConfigurationVersionSummary> listed = (await client.ListHostedConfigurationVersionsAsync(
                                new ListHostedConfigurationVersionsRequest { ApplicationId = applicationId, ConfigurationProfileId = profileId }, ct).ConfigureAwait(false)).Items ?? [];

                            firstVersion = numbers[0].ToString(CultureInfo.InvariantCulture);

                            return $"versions {string.Join(", ", numbers)}; ListHostedConfigurationVersions lists {listed.Count}\nversion {numbers[1]} ({read.ContentType}): {body}";
                        }).ConfigureAwait(false);

                    yield return hosted;

                    yield return await RunStepAsync(
                        "ListDeploymentStrategies, GetDeploymentStrategy — the strategies AppConfig ships with",
                        this.Call("GET", "/deploymentstrategies", $"client.ListDeploymentStrategiesAsync(new ListDeploymentStrategiesRequest())\nclient.GetDeploymentStrategyAsync(new GetDeploymentStrategyRequest {{ DeploymentStrategyId = \"{StrategyId}\" }})"),
                        async () =>
                        {
                            List<DeploymentStrategy> strategies = (await client.ListDeploymentStrategiesAsync(new ListDeploymentStrategiesRequest(), ct).ConfigureAwait(false)).Items ?? [];
                            GetDeploymentStrategyResponse chosen = await client.GetDeploymentStrategyAsync(new GetDeploymentStrategyRequest { DeploymentStrategyId = StrategyId }, ct).ConfigureAwait(false);

                            return $"{string.Join("\n", strategies.Select(s => $"{s.Id}: {s.DeploymentDurationInMinutes} min, {s.FinalBakeTimeInMinutes} min bake, {s.GrowthFactor}% {s.GrowthType?.Value}"))}\nusing {chosen.Id}";
                        }).ConfigureAwait(false);

                    // Before the real deployment, not after it: real AppConfig allows one active deployment
                    // per environment, and AllAtOnce bakes for ten minutes, so a later attempt would be
                    // refused for that rather than for the missing version.
                    if (environment.Succeeded)
                    {
                        yield return await RunStepAsync(
                            "StartDeployment — a version that was never created",
                            this.Call("POST", $"/applications/{applicationId}/environments/{environmentId}/deployments", $"client.StartDeploymentAsync(new StartDeploymentRequest {{ ..., ConfigurationVersion = \"9\", DeploymentStrategyId = \"{StrategyId}\" }})"),
                            async () =>
                            {
                                try
                                {
                                    StartDeploymentResponse response = await client.StartDeploymentAsync(
                                        new StartDeploymentRequest
                                        {
                                            ApplicationId = applicationId,
                                            EnvironmentId = environmentId,
                                            ConfigurationProfileId = profileId,
                                            ConfigurationVersion = "9",
                                            DeploymentStrategyId = StrategyId,
                                        }, ct).ConfigureAwait(false);

                                    return $"deployment {response.DeploymentNumber}: version {response.ConfigurationVersion}, {response.State?.Value}\nreal AppConfig refuses a version the profile does not have; floci deploys it and reports it complete";
                                }
                                // What real AppConfig answers: the expected outcome, shown as a successful step.
                                catch (AmazonAppConfigException ex) when (ex.StatusCode != 0)
                                {
                                    return $"{ex.ErrorCode}: {ex.Message}";
                                }
                            }).ConfigureAwait(false);
                    }

                    if (environment.Succeeded && hosted.Succeeded)
                    {
                        yield return await RunStepAsync(
                            "StartDeployment, GetDeployment — version 1 into the environment",
                            this.Call("POST", $"/applications/{applicationId}/environments/{environmentId}/deployments", $"client.StartDeploymentAsync(new StartDeploymentRequest {{ ApplicationId = id, EnvironmentId = env, ConfigurationProfileId = profile, ConfigurationVersion = \"{firstVersion}\", DeploymentStrategyId = \"{StrategyId}\" }})\nclient.GetDeploymentAsync(new GetDeploymentRequest {{ ..., DeploymentNumber = n }})"),
                            async () =>
                            {
                                StartDeploymentResponse started = await client.StartDeploymentAsync(
                                    new StartDeploymentRequest
                                    {
                                        ApplicationId = applicationId,
                                        EnvironmentId = environmentId,
                                        ConfigurationProfileId = profileId,
                                        ConfigurationVersion = firstVersion,
                                        DeploymentStrategyId = StrategyId,
                                    }, ct).ConfigureAwait(false);

                                GetDeploymentResponse found = await client.GetDeploymentAsync(
                                    new GetDeploymentRequest { ApplicationId = applicationId, EnvironmentId = environmentId, DeploymentNumber = started.DeploymentNumber.GetValueOrDefault() }, ct).ConfigureAwait(false);

                                string line = $"deployment {found.DeploymentNumber}: version {found.ConfigurationVersion}, {found.State?.Value}";

                                return factory.UseEmulator
                                    ? $"{line}\nfloci completes the deployment at once, with no rollout and no bake time; real AppConfig reads Deploying and then Baking for the strategy's bake time"
                                    : line;
                            }).ConfigureAwait(false);
                    }
                }

                yield return await RunStepAsync(
                    "TagResource, ListTagsForResource — labelling the application",
                    this.Call("POST", "/tags/{arn}", "client.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = new Dictionary<string, string> { [\"env\"] = \"lab\" } })\nclient.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })"),
                    async () =>
                    {
                        // The ARN carries the account id. floci's is the all-zero one; real AWS's would
                        // take a second package (STS) to learn, which the one-package rule forbids.
                        if (!factory.UseEmulator)
                        {
                            return "skipped: tagging addresses an ARN with the account id, which this sample could only learn through a second package (STS)";
                        }

                        string arn = $"arn:aws:appconfig:{factory.Region}:000000000000:application/{applicationId}";

                        await client.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = new Dictionary<string, string> { ["env"] = "lab" } }, ct).ConfigureAwait(false);

                        Dictionary<string, string> tags = (await client.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn }, ct).ConfigureAwait(false)).Tags ?? [];

                        if (!tags.TryGetValue("env", out string? value) || value != "lab")
                        {
                            throw new InvalidOperationException($"the tag was accepted but ListTagsForResource answers {tags.Count} tag(s) without env=lab.");
                        }

                        return string.Join("\n", tags.Select(t => $"{t.Key}={t.Value}"));
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "GetApplication — an id that does not exist",
                    this.Call("GET", "/applications/{id}", "client.GetApplicationAsync(new GetApplicationRequest { ApplicationId = <unknown id> })"),
                    async () =>
                    {
                        try
                        {
                            await client.GetApplicationAsync(new GetApplicationRequest { ApplicationId = "zzzzzzz" }, ct).ConfigureAwait(false);

                            return "accepted\nreal AppConfig answers ResourceNotFoundException for an application that does not exist; floci has started to accept it";
                        }
                        // What real AppConfig answers: the expected outcome, shown as a successful step.
                        catch (ResourceNotFoundException ex)
                        {
                            return $"{ex.ErrorCode}: {ex.Message}";
                        }
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "UpdateApplication — changing its description",
                    this.Call("PATCH", $"/applications/{applicationId}", "client.UpdateApplicationAsync(new UpdateApplicationRequest { ApplicationId = id, Description = \"renamed\" })"),
                    async () =>
                    {
                        try
                        {
                            await client.UpdateApplicationAsync(new UpdateApplicationRequest { ApplicationId = applicationId, Description = "renamed" }, ct).ConfigureAwait(false);

                            return factory.UseEmulator
                                ? "updated\nfloci now implements the update operations: delete this step's tripwire and add UpdateEnvironment, UpdateConfigurationProfile and UpdateDeploymentStrategy"
                                : "updated";
                        }
                        // Not a 501: floci answers an update with HTTP 405 and no body. Real AppConfig updates it.
                        catch (AmazonAppConfigException ex) when (IsNotImplemented(ex))
                        {
                            return $"HTTP {(int)ex.StatusCode} {ex.StatusCode}: {ex.Message}\nreal AppConfig updates an application, an environment, a profile and a strategy in place; floci builds the create, get, list and delete half";
                        }
                    }).ConfigureAwait(false);
            }
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // An iterator may not yield from a finally, so the step is yielded below.
            if (claimed)
            {
                cleanup.Add(await this.DeleteAsync(client, name, ct).ConfigureAwait(false));
            }
        }

        foreach (DemoStep step in cleanup)
        {
            yield return step;
        }
    }

    /// <summary>
    /// The AWS SDK reports a not-implemented operation inside an <see cref="AmazonServiceException"/>,
    /// so <see cref="ProbeResult.FromException"/> — which inspects only the outermost exception —
    /// cannot classify it on its own. floci answers an operation it has not built with HTTP 404 and
    /// the error code <c>UnknownOperationException</c>, or with HTTP 405 where the path exists for
    /// another verb: both are the not-implemented outcome here.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                case AmazonServiceException { ErrorCode: "UnknownOperationException" }:
                case AmazonServiceException { StatusCode: HttpStatusCode.MethodNotAllowed }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    private static bool IsNotImplemented(AmazonServiceException ex)
        => ex.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed || ex.ErrorCode == "UnknownOperationException";

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still deletes the application.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>The applications of that name; names are unique per run, so more than one means a lost create was retried.</summary>
    private static async Task<List<Application>> FindAsync(IAmazonAppConfig client, string name, CancellationToken ct)
    {
        List<Application> found = [];
        string? next = null;

        do
        {
            ListApplicationsResponse page = await client.ListApplicationsAsync(new ListApplicationsRequest { NextToken = next }, ct).ConfigureAwait(false);

            found.AddRange((page.Items ?? []).Where(a => a.Name == name));
            next = page.NextToken;
        }
        while (!string.IsNullOrEmpty(next));

        return found;
    }

    /// <summary>
    /// Real AWS only: a deployment that is still rolling out or baking blocks the environment's
    /// delete. Stops it, which rolls it back, and waits up to two minutes for that to settle. floci
    /// completes a deployment at once, so it is never called there.
    /// </summary>
    private static async Task StopDeploymentsAsync(IAmazonAppConfig client, string applicationId, string environmentId, CancellationToken ct)
    {
        List<DeploymentSummary> deployments = (await client.ListDeploymentsAsync(
            new ListDeploymentsRequest { ApplicationId = applicationId, EnvironmentId = environmentId }, ct).ConfigureAwait(false)).Items ?? [];

        foreach (DeploymentSummary deployment in deployments.Where(d => d.State == DeploymentState.DEPLOYING || d.State == DeploymentState.BAKING))
        {
            // AllowRevert: without it StopDeployment works only on a DEPLOYING deployment, and an
            // AllAtOnce one is already BAKING.
            await client.StopDeploymentAsync(
                new StopDeploymentRequest { ApplicationId = applicationId, EnvironmentId = environmentId, DeploymentNumber = deployment.DeploymentNumber.GetValueOrDefault(), AllowRevert = true }, ct).ConfigureAwait(false);
        }

        DateTime deadline = DateTime.UtcNow.AddMinutes(2);

        while (DateTime.UtcNow < deadline)
        {
            deployments = (await client.ListDeploymentsAsync(
                new ListDeploymentsRequest { ApplicationId = applicationId, EnvironmentId = environmentId }, ct).ConfigureAwait(false)).Items ?? [];

            // ROLLING_BACK too: the stop is accepted at once, and the rollback after it still holds the environment.
            if (!deployments.Any(d => d.State == DeploymentState.DEPLOYING || d.State == DeploymentState.BAKING || d.State == DeploymentState.ROLLING_BACK))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The wire-level request shown beside an SDK call: REST JSON, one method and path per operation.</summary>
    private string Call(string method, string path, string call) => $"{method} {factory.ServiceUrl}{path}\n{call}";

    /// <summary>
    /// Deletes whatever this run created, looking the application up by its unique name so one whose
    /// create response was lost is still found, and one that was never created reads as a truthful
    /// "nothing to remove". Works inside out — hosted versions, profiles, environments, the
    /// application — because real AppConfig refuses to delete a parent that still has children.
    /// Uses <see cref="CancellationToken.None"/>: a cancelled run still has an application to remove.
    /// </summary>
    private async Task<DemoStep> DeleteAsync(IAmazonAppConfig client, string name, CancellationToken ct)
    {
        string request = this.Call("DELETE", "/applications/{id}", "client.DeleteHostedConfigurationVersionAsync, DeleteConfigurationProfileAsync, DeleteEnvironmentAsync, DeleteApplicationAsync");

        return await RunStepAsync("DeleteApplication — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<Application> found = await FindAsync(client, name, none).ConfigureAwait(false);

            if (found.Count == 0)
            {
                return $"No application for this run exists — nothing to remove.{cancelled}";
            }

            List<string> failures = [];
            int versions = 0;
            int profiles = 0;
            int environments = 0;
            string? environmentGap = null;

            foreach (Application application in found)
            {
                try
                {
                    List<AppEnvironment> envs = (await client.ListEnvironmentsAsync(new ListEnvironmentsRequest { ApplicationId = application.Id }, none).ConfigureAwait(false)).Items ?? [];

                    foreach (AppEnvironment env in envs)
                    {
                        if (!factory.UseEmulator)
                        {
                            await StopDeploymentsAsync(client, application.Id, env.Id, none).ConfigureAwait(false);
                        }
                    }

                    List<ConfigurationProfileSummary> listedProfiles = (await client.ListConfigurationProfilesAsync(new ListConfigurationProfilesRequest { ApplicationId = application.Id }, none).ConfigureAwait(false)).Items ?? [];

                    foreach (ConfigurationProfileSummary profile in listedProfiles)
                    {
                        List<HostedConfigurationVersionSummary> hosted = (await client.ListHostedConfigurationVersionsAsync(
                            new ListHostedConfigurationVersionsRequest { ApplicationId = application.Id, ConfigurationProfileId = profile.Id }, none).ConfigureAwait(false)).Items ?? [];

                        foreach (HostedConfigurationVersionSummary version in hosted)
                        {
                            await client.DeleteHostedConfigurationVersionAsync(
                                new DeleteHostedConfigurationVersionRequest { ApplicationId = application.Id, ConfigurationProfileId = profile.Id, VersionNumber = version.VersionNumber.GetValueOrDefault() }, none).ConfigureAwait(false);
                            versions++;
                        }

                        await client.DeleteConfigurationProfileAsync(new DeleteConfigurationProfileRequest { ApplicationId = application.Id, ConfigurationProfileId = profile.Id }, none).ConfigureAwait(false);
                        profiles++;
                    }

                    foreach (AppEnvironment env in envs)
                    {
                        try
                        {
                            await client.DeleteEnvironmentAsync(new DeleteEnvironmentRequest { ApplicationId = application.Id, EnvironmentId = env.Id }, none).ConfigureAwait(false);
                            environments++;
                        }
                        // floci has no DeleteEnvironment. Real AppConfig refuses to delete an application
                        // that still has environments; floci does not, so the run goes on and says so.
                        catch (AmazonAppConfigException ex) when (factory.UseEmulator && IsNotImplemented(ex))
                        {
                            environmentGap = $"{ex.ErrorCode ?? ex.StatusCode.ToString()}: floci cannot delete an environment, and DeleteApplication leaves it listed under the deleted id";
                        }
                    }

                    await client.DeleteApplicationAsync(new DeleteApplicationRequest { ApplicationId = application.Id }, none).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add($"{application.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            int remaining = (await FindAsync(client, name, none).ConfigureAwait(false)).Count;

            if (failures.Count != 0 || remaining != 0)
            {
                throw new InvalidOperationException($"{Math.Max(failures.Count, remaining)} application(s) not removed:\n{string.Join("\n", failures)}{cancelled}");
            }

            string result = $"Deleted {found.Count} application(s) of {name} with {versions} hosted version(s), {profiles} profile(s) and {environments} environment(s), now absent from ListApplications.";

            return environmentGap is null ? result + cancelled : $"{result}\n{environmentGap}{cancelled}";
        }).ConfigureAwait(false);
    }
}
