using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Amazon.AppConfigData;
using Amazon.AppConfigData.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.AppConfigData;

/// <summary>
/// Reads a deployed configuration the way an application does: starts a session, fetches the
/// configuration, polls again and gets nothing because nothing changed, deploys a new version and
/// polls for it, then tries a spent token and a profile that does not exist. Ordinary
/// AWSSDK.AppConfigData code — the only emulator wiring is in <see cref="AppConfigDataClientFactory"/>.
/// The configuration it reads has to be written by something, and that is the AppConfig control
/// plane: <see cref="AppConfigSeeder"/> does it with AWSSDK.AppConfig, the sample's second package.
/// Every step reports what the engine actually answered rather than what the docs promise.
/// </summary>
public sealed class AppConfigDataDemo(AppConfigDataClientFactory factory) : IServiceDemo
{
    private const string FlagsJson = """{"checkout":{"newFlow":false},"timeoutSeconds":30}""";

    private const string FlagsJsonV2 = """{"checkout":{"newFlow":true},"timeoutSeconds":30}""";

    /// <summary>Asked of the session, so the step can show that the answer carries it back.</summary>
    private const int PollIntervalSeconds = 60;

    public string Provider => CloudProvider.Aws;

    public string Slug => "appconfigdata";

    public string DisplayName => "AppConfigData";

    public string Category => "Developer tools and delivery";

    public string Route => "/aws/appconfigdata";

    /// <summary>
    /// StartConfigurationSession for a profile that does not exist. It is the one data-plane call
    /// that needs nothing created, and a service that answers ResourceNotFoundException is a service
    /// that is there.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonAppConfigData client = factory.Create();

            try
            {
                await client.StartConfigurationSessionAsync(UnknownSession(), ct).ConfigureAwait(false);

                return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), "StartConfigurationSession: accepted a profile that does not exist.");
            }
            catch (ResourceNotFoundException ex)
            {
                return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"StartConfigurationSession: {ex.ErrorCode} for an unknown profile, as expected.");
            }
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonAppConfigData client = factory.Create();

        // The seeding writes to floci only: a page that created and deployed cloud resources
        // unasked, on every run, is not what this sample is for.
        using AppConfigSeeder? seeder = factory.UseEmulator ? new AppConfigSeeder(factory.CreateAppConfig()) : null;

        string name = $"flocilab-{Guid.NewGuid().ToString("N")[..12]}";

        // Claimed before the create: a call that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created the application, so the finally looks it up by
        // name rather than trusting that a response arrived.
        bool claimed = false;
        AppConfigSeeder.Seeded? target = null;
        string? firstToken = null;
        string? nextToken = null;
        string? spentToken = null;

        List<DemoStep> cleanup = [];

        try
        {
            if (seeder is null)
            {
                yield return new DemoStep(
                    "Seeding — skipped on real AWS",
                    "(none)",
                    "skipped: reading a configuration needs one deployed first, and this page does not create and deploy AppConfig resources on a real account; point the lab at floci to run the whole walk");
            }
            else
            {
                DemoStep seeded = await RunStepAsync(
                    "Seeding with AWSSDK.AppConfig — an application, a profile and version 1 deployed",
                    this.Call("POST", "/applications ... /deployments", "appConfig.CreateApplicationAsync, CreateEnvironmentAsync, CreateConfigurationProfileAsync, CreateHostedConfigurationVersionAsync, StartDeploymentAsync\n(AWSSDK.AppConfig, the sample's second package: the control plane writes what AppConfigData reads)"),
                    async () =>
                    {
                        claimed = true;
                        target = await seeder.CreateAsync(name, ct).ConfigureAwait(false);

                        int version = await seeder.PublishAsync(target, FlagsJson, ct).ConfigureAwait(false);
                        string state = await seeder.DeployAsync(target, version, ct).ConfigureAwait(false);

                        return $"{name}: application {target.ApplicationId}, environment {target.EnvironmentId}, profile {target.ProfileId}\nversion {version} published and deployed: {state}";
                    }).ConfigureAwait(false);

                yield return seeded;

                if (seeded.Succeeded && target is not null)
                {
                    DemoStep session = await RunStepAsync(
                        "StartConfigurationSession — opening a session",
                        this.Call("POST", "/configurationsessions", $"client.StartConfigurationSessionAsync(new StartConfigurationSessionRequest {{ ApplicationIdentifier = app, EnvironmentIdentifier = env, ConfigurationProfileIdentifier = profile, RequiredMinimumPollIntervalInSeconds = {PollIntervalSeconds} }})"),
                        async () =>
                        {
                            StartConfigurationSessionResponse response = await client.StartConfigurationSessionAsync(
                                new StartConfigurationSessionRequest
                                {
                                    ApplicationIdentifier = target.ApplicationId,
                                    EnvironmentIdentifier = target.EnvironmentId,
                                    ConfigurationProfileIdentifier = target.ProfileId,
                                    RequiredMinimumPollIntervalInSeconds = PollIntervalSeconds,
                                }, ct).ConfigureAwait(false);

                            firstToken = response.InitialConfigurationToken;

                            return $"session open, initial token of {firstToken.Length} characters";
                        }).ConfigureAwait(false);

                    yield return session;

                    if (session.Succeeded && firstToken is not null)
                    {
                        yield return await RunStepAsync(
                            "GetLatestConfiguration — the first poll",
                            this.Call("GET", "/configuration?configuration_token={token}", "client.GetLatestConfigurationAsync(new GetLatestConfigurationRequest { ConfigurationToken = token })"),
                            async () =>
                            {
                                GetLatestConfigurationResponse response = await client.GetLatestConfigurationAsync(
                                    new GetLatestConfigurationRequest { ConfigurationToken = firstToken }, ct).ConfigureAwait(false);

                                nextToken = response.NextPollConfigurationToken;

                                string body = await ReadAsync(response, ct).ConfigureAwait(false);

                                if (body != FlagsJson)
                                {
                                    throw new InvalidOperationException($"version 1 was deployed but the first poll reads {body}.");
                                }

                                return $"version {response.VersionLabel} ({response.ContentType}): {body}\nnext poll in {response.NextPollIntervalInSeconds} s, with a new token";
                            }).ConfigureAwait(false);

                        if (nextToken is not null)
                        {
                            string tokenToSpend = nextToken;

                            yield return await RunStepAsync(
                                "GetLatestConfiguration — polling again, nothing has changed",
                                this.Call("GET", "/configuration?configuration_token={next token}", "client.GetLatestConfigurationAsync(new GetLatestConfigurationRequest { ConfigurationToken = response.NextPollConfigurationToken })"),
                                async () =>
                                {
                                    // Cleared first: if this poll fails, the token it sent may be spent, and
                                    // the version-2 step must not run on it.
                                    nextToken = null;

                                    // At once, not after the interval the session asked for: a demo cannot
                                    // wait a minute per step. floci does not hold a session to its interval;
                                    // production code waits NextPollIntervalInSeconds between polls.
                                    GetLatestConfigurationResponse response = await client.GetLatestConfigurationAsync(
                                        new GetLatestConfigurationRequest { ConfigurationToken = tokenToSpend }, ct).ConfigureAwait(false);

                                    nextToken = response.NextPollConfigurationToken;
                                    spentToken = tokenToSpend;

                                    string body = await ReadAsync(response, ct).ConfigureAwait(false);

                                    return $"{Encoding.UTF8.GetByteCount(body)} bytes, version label '{response.VersionLabel}'\nan empty body is the answer when the deployed version is the one you already have\npolled at once: the session asked for {PollIntervalSeconds} s between polls, and floci does not hold it to that";
                                }).ConfigureAwait(false);

                            if (nextToken is not null)
                            {
                                string latestToken = nextToken;

                                yield return await RunStepAsync(
                                    "GetLatestConfiguration — after deploying version 2",
                                    this.Call("GET", "/configuration?configuration_token={next token}", "(seed: publish and deploy version 2)\nclient.GetLatestConfigurationAsync(new GetLatestConfigurationRequest { ConfigurationToken = response.NextPollConfigurationToken })"),
                                    async () =>
                                    {
                                        int version = await seeder.PublishAsync(target, FlagsJsonV2, ct).ConfigureAwait(false);
                                        string state = await seeder.DeployAsync(target, version, ct).ConfigureAwait(false);

                                        GetLatestConfigurationResponse response = await client.GetLatestConfigurationAsync(
                                            new GetLatestConfigurationRequest { ConfigurationToken = latestToken }, ct).ConfigureAwait(false);

                                        string body = await ReadAsync(response, ct).ConfigureAwait(false);

                                        if (body != FlagsJsonV2)
                                        {
                                            throw new InvalidOperationException($"version {version} was deployed ({state}) but the poll reads {body}.");
                                        }

                                        return $"version {version} deployed: {state}\nversion {response.VersionLabel}: {body}";
                                    }).ConfigureAwait(false);
                            }

                            if (spentToken is not null)
                            {
                                string reused = spentToken;

                                yield return await RunStepAsync(
                                    "GetLatestConfiguration — a token that was already used",
                                    this.Call("GET", "/configuration?configuration_token={spent token}", "client.GetLatestConfigurationAsync(new GetLatestConfigurationRequest { ConfigurationToken = <the token the second poll used> })"),
                                    async () =>
                                    {
                                        try
                                        {
                                            await client.GetLatestConfigurationAsync(new GetLatestConfigurationRequest { ConfigurationToken = reused }, ct).ConfigureAwait(false);

                                            return "accepted\nfloci rejects a token a poll already spent; this run it did not";
                                        }
                                        // What floci answers: the expected outcome, shown as a successful step.
                                        catch (BadRequestException ex)
                                        {
                                            return $"{ex.ErrorCode}: {ex.Message}\neach poll hands back the token for the next one; keep only the newest";
                                        }
                                    }).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }

            yield return await RunStepAsync(
                "StartConfigurationSession — a profile that does not exist",
                this.Call("POST", "/configurationsessions", "client.StartConfigurationSessionAsync(new StartConfigurationSessionRequest { ApplicationIdentifier = \"no-such-app\", EnvironmentIdentifier = \"no-such-env\", ConfigurationProfileIdentifier = \"no-such-profile\" })"),
                async () =>
                {
                    try
                    {
                        await client.StartConfigurationSessionAsync(UnknownSession(), ct).ConfigureAwait(false);

                        return "accepted\nreal AppConfigData answers ResourceNotFoundException for an application that does not exist; floci has started to accept it";
                    }
                    // What real AppConfigData answers: the expected outcome, shown as a successful step.
                    catch (ResourceNotFoundException ex)
                    {
                        return $"{ex.ErrorCode}: {ex.Message}";
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // An iterator may not yield from a finally, so the step is yielded below.
            if (claimed && seeder is not null)
            {
                cleanup.Add(await this.DeleteAsync(seeder, name, ct).ConfigureAwait(false));
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

    private static StartConfigurationSessionRequest UnknownSession()
        => new()
        {
            ApplicationIdentifier = "no-such-app",
            EnvironmentIdentifier = "no-such-env",
            ConfigurationProfileIdentifier = "no-such-profile",
        };

    private static async Task<string> ReadAsync(GetLatestConfigurationResponse response, CancellationToken ct)
    {
        // An empty stream when the poll returned no new configuration; null is guarded too, since the SDK does not promise a stream.
        if (response.Configuration is null)
        {
            return string.Empty;
        }

        using StreamReader reader = new(response.Configuration, Encoding.UTF8);

        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

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

    /// <summary>The wire-level request shown beside an SDK call: REST JSON, one method and path per operation.</summary>
    private string Call(string method, string path, string call) => $"{method} {factory.ServiceUrl}{path}\n{call}";

    /// <summary>
    /// Deletes whatever this run created, looking the application up by its unique name so one whose
    /// create response was lost is still found, and one that was never created reads as a truthful
    /// "nothing to remove". Uses <see cref="CancellationToken.None"/>: a cancelled run still has an
    /// application to remove.
    /// </summary>
    private async Task<DemoStep> DeleteAsync(AppConfigSeeder seeder, string name, CancellationToken ct)
    {
        string request = this.Call("DELETE", "/applications/{id}", "appConfig.DeleteHostedConfigurationVersionAsync, DeleteConfigurationProfileAsync, DeleteEnvironmentAsync, DeleteApplicationAsync");

        return await RunStepAsync("DeleteApplication — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<string> found = await seeder.FindAsync(name, none).ConfigureAwait(false);

            if (found.Count == 0)
            {
                return $"No application for this run exists — nothing to remove.{cancelled}";
            }

            int versions = 0;
            int profiles = 0;
            int environments = 0;
            int kept = 0;

            foreach (string id in found)
            {
                (int v, int p, int e, int k) = await seeder.DeleteAsync(id, none).ConfigureAwait(false);

                versions += v;
                profiles += p;
                environments += e;
                kept += k;
            }

            int remaining = (await seeder.FindAsync(name, none).ConfigureAwait(false)).Count;

            if (remaining != 0)
            {
                throw new InvalidOperationException($"{remaining} application(s) not removed.{cancelled}");
            }

            string result = string.Create(
                CultureInfo.InvariantCulture,
                $"Deleted {found.Count} application(s) of {name} with {versions} hosted version(s), {profiles} profile(s) and {environments} environment(s), now absent from ListApplications.");

            return kept == 0
                ? result + cancelled
                : $"{result}\nfloci cannot delete an environment ({kept} left behind), and DeleteApplication leaves it listed under the deleted id{cancelled}";
        }).ConfigureAwait(false);
    }
}
