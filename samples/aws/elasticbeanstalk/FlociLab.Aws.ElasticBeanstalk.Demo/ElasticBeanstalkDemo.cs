using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.ElasticBeanstalk;
using Amazon.ElasticBeanstalk.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.ElasticBeanstalk;

/// <summary>
/// Creates an application, a version of it, and an environment on a solution stack, reads them
/// back as the resource tree they are, then asks for a configuration template, a second
/// application of the same name, and the deletion of an application that still has a running
/// environment, and deletes everything. Ordinary AWSSDK.ElasticBeanstalk code — the only
/// emulator-aware line is in <see cref="ElasticBeanstalkClientFactory"/>. floci launches nothing:
/// an environment is a record that reads Ready and Green the moment it is created, where real
/// Elastic Beanstalk spends minutes provisioning instances, so the page does not wait for one.
/// Every step reports what the engine actually answered rather than what the docs promise.
/// </summary>
public sealed class ElasticBeanstalkDemo(ElasticBeanstalkClientFactory factory) : IServiceDemo
{
    private const string VersionLabel = "1.0.0";

    public string Provider => CloudProvider.Aws;

    public string Slug => "elasticbeanstalk";

    public string DisplayName => "Elastic Beanstalk";

    public string Category => "Containers and compute";

    public string Route => "/aws/elasticbeanstalk";

    /// <summary>DescribeApplications — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonElasticBeanstalk client = factory.Create();
            DescribeApplicationsResponse response = await client.DescribeApplicationsAsync(new DescribeApplicationsRequest(), ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"DescribeApplications: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonElasticBeanstalk client = factory.Create();

        string suffix = Guid.NewGuid().ToString("N")[..12];
        Names names = new($"flocilab-{suffix}", $"fl-{suffix}", $"flocilab-{suffix}-template");

        // Claimed before each create: a call that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created the resource, so the finally looks each name
        // up rather than trusting that a response arrived.
        Claimed claimed = new();

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep application = await RunStepAsync(
                "CreateApplication, DescribeApplications — a home for versions and environments",
                this.Call("CreateApplication", $"client.CreateApplicationAsync(new CreateApplicationRequest {{ ApplicationName = \"{names.Application}\", Description = \"FlociLab demo\" }})\nclient.DescribeApplicationsAsync(new DescribeApplicationsRequest {{ ApplicationNames = [\"{names.Application}\"] }})"),
                async () =>
                {
                    claimed.Application = true;
                    CreateApplicationResponse created = await client.CreateApplicationAsync(new CreateApplicationRequest { ApplicationName = names.Application, Description = "FlociLab demo" }, ct).ConfigureAwait(false);

                    ApplicationDescription found = await DescribeApplicationAsync(client, names.Application, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("CreateApplication was accepted but DescribeApplications does not list it.");

                    return $"{found.ApplicationName}: {found.Description}\n{created.Application.ApplicationArn}";
                }).ConfigureAwait(false);

            yield return application;

            // A failed create means every later step would look for an application that is not there.
            // Skipped rather than `yield break`, which would also skip yielding the cleanup step below.
            if (application.Succeeded)
            {
                yield return await RunStepAsync(
                    $"CreateApplicationVersion, DescribeApplicationVersions — version {VersionLabel}, no source bundle",
                    this.Call("CreateApplicationVersion", $"client.CreateApplicationVersionAsync(new CreateApplicationVersionRequest {{ ApplicationName = \"{names.Application}\", VersionLabel = \"{VersionLabel}\" }})\nclient.DescribeApplicationVersionsAsync(new DescribeApplicationVersionsRequest {{ ApplicationName = \"{names.Application}\" }})"),
                    async () =>
                    {
                        await client.CreateApplicationVersionAsync(new CreateApplicationVersionRequest { ApplicationName = names.Application, VersionLabel = VersionLabel }, ct).ConfigureAwait(false);

                        List<ApplicationVersionDescription> versions = (await client.DescribeApplicationVersionsAsync(new DescribeApplicationVersionsRequest { ApplicationName = names.Application }, ct).ConfigureAwait(false)).ApplicationVersions ?? [];

                        if (!versions.Any(v => v.VersionLabel == VersionLabel))
                        {
                            throw new InvalidOperationException($"the version was accepted but DescribeApplicationVersions lists {versions.Count} version(s) without it.");
                        }

                        return string.Join("\n", versions.Select(v => $"{v.VersionLabel}: {v.Status?.Value}"));
                    }).ConfigureAwait(false);

                string? stack = null;

                yield return await RunStepAsync(
                    "ListAvailableSolutionStacks — what an environment can run",
                    this.Call("ListAvailableSolutionStacks", "client.ListAvailableSolutionStacksAsync(new ListAvailableSolutionStacksRequest())"),
                    async () =>
                    {
                        List<string> stacks = (await client.ListAvailableSolutionStacksAsync(new ListAvailableSolutionStacksRequest(), ct).ConfigureAwait(false)).SolutionStacks ?? [];

                        stack = stacks.FirstOrDefault(s => s.Contains("Node.js", StringComparison.Ordinal)) ?? stacks.FirstOrDefault()
                            ?? throw new InvalidOperationException("ListAvailableSolutionStacks answered an empty list, so there is nothing to launch an environment on.");

                        return $"{stacks.Count} stack(s):\n{string.Join("\n", stacks)}\nusing: {stack}";
                    }).ConfigureAwait(false);

                // Gates the DeleteApplication step: a refusal only means something if the environment exists.
                bool launched = false;

                if (stack is not null)
                {
                    DemoStep environment = await RunStepAsync(
                        $"CreateEnvironment — {VersionLabel} on {stack}",
                        this.Call("CreateEnvironment", $"client.CreateEnvironmentAsync(new CreateEnvironmentRequest {{ ApplicationName = \"{names.Application}\", EnvironmentName = \"{names.Environment}\", VersionLabel = \"{VersionLabel}\", SolutionStackName = \"{stack}\" }})"),
                        async () =>
                        {
                            claimed.Environment = true;
                            CreateEnvironmentResponse created = await client.CreateEnvironmentAsync(
                                new CreateEnvironmentRequest
                                {
                                    ApplicationName = names.Application,
                                    EnvironmentName = names.Environment,
                                    VersionLabel = VersionLabel,
                                    SolutionStackName = stack,
                                }, ct).ConfigureAwait(false);

                            EnvironmentDescription found = await this.WaitForEnvironmentAsync(client, names.Environment, e => e?.Status != EnvironmentStatus.Launching, ct).ConfigureAwait(false)
                                ?? throw new InvalidOperationException("CreateEnvironment was accepted but DescribeEnvironments does not list it.");

                            return factory.UseEmulator
                                ? $"{DescribeEnvironment(found)}\n{created.EnvironmentArn}\nfloci launches nothing: the environment reads {found.Status?.Value} at once, real Elastic Beanstalk reads Launching for minutes"
                                : $"{DescribeEnvironment(found)}\n{created.EnvironmentArn}";
                        }).ConfigureAwait(false);

                    launched = environment.Succeeded;

                    yield return environment;

                    yield return await RunStepAsync(
                        "DescribeApplications, DescribeEnvironments — the resource tree",
                        this.Call("DescribeApplications", $"client.DescribeApplicationsAsync(new DescribeApplicationsRequest {{ ApplicationNames = [\"{names.Application}\"] }})\nclient.DescribeEnvironmentsAsync(new DescribeEnvironmentsRequest {{ ApplicationName = \"{names.Application}\" }})"),
                        async () =>
                        {
                            ApplicationDescription app = await DescribeApplicationAsync(client, names.Application, ct).ConfigureAwait(false)
                                ?? throw new InvalidOperationException("DescribeApplications does not list the application.");

                            List<EnvironmentDescription> environments = (await client.DescribeEnvironmentsAsync(new DescribeEnvironmentsRequest { ApplicationName = names.Application }, ct).ConfigureAwait(false)).Environments ?? [];

                            return DescribeTree(app, environments);
                        }).ConfigureAwait(false);

                    // Inside the stack check: a template with no solution stack is a bad request on
                    // real Elastic Beanstalk, which floci's UnsupportedOperation would hide.
                    yield return await RunStepAsync(
                        "CreateConfigurationTemplate — saving an environment's settings",
                        this.Call("CreateConfigurationTemplate", $"client.CreateConfigurationTemplateAsync(new CreateConfigurationTemplateRequest {{ ApplicationName = \"{names.Application}\", TemplateName = \"{names.Template}\", SolutionStackName = \"{stack}\" }})"),
                        async () =>
                        {
                            try
                            {
                                // A template dies with its application, so cleanup needs no delete of its own.
                                await client.CreateConfigurationTemplateAsync(
                                    new CreateConfigurationTemplateRequest
                                    {
                                        ApplicationName = names.Application,
                                        TemplateName = names.Template,
                                        SolutionStackName = stack,
                                    }, ct).ConfigureAwait(false);

                                return factory.UseEmulator
                                    ? "accepted\nfloci now implements configuration templates: delete this step's tripwire and add the template to the resource tree"
                                    : "accepted";
                            }
                            // Not a 501: floci answers an operation it recognises but has not built with
                            // HTTP 400 and this code. Real Elastic Beanstalk accepts the call.
                            catch (AmazonElasticBeanstalkException ex) when (ex.ErrorCode == "UnsupportedOperation")
                            {
                                return $"{ex.ErrorCode}: {ex.Message}\nreal Elastic Beanstalk accepts a configuration template; floci has not built it";
                            }
                        }).ConfigureAwait(false);
                }

                yield return await RunStepAsync(
                    "CreateApplication — a name that is taken",
                    this.Call("CreateApplication", $"client.CreateApplicationAsync(new CreateApplicationRequest {{ ApplicationName = \"{names.Application}\" }})"),
                    async () =>
                    {
                        try
                        {
                            await client.CreateApplicationAsync(new CreateApplicationRequest { ApplicationName = names.Application }, ct).ConfigureAwait(false);

                            return "accepted\nreal Elastic Beanstalk refuses a second application of the same name with InvalidParameterValue; floci has started to accept it";
                        }
                        // What real Elastic Beanstalk answers: the expected outcome, shown as a successful step.
                        catch (AmazonElasticBeanstalkException ex) when (ex.ErrorCode == "InvalidParameterValue" && ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                        {
                            return $"{ex.ErrorCode}: {ex.Message}";
                        }
                    }).ConfigureAwait(false);

                if (launched)
                {
                    yield return await RunStepAsync(
                        "DeleteApplication — while it still has an environment",
                        this.Call("DeleteApplication", $"client.DeleteApplicationAsync(new DeleteApplicationRequest {{ ApplicationName = \"{names.Application}\" }})"),
                        async () =>
                        {
                            try
                            {
                                await client.DeleteApplicationAsync(new DeleteApplicationRequest { ApplicationName = names.Application }, ct).ConfigureAwait(false);

                                return "accepted\nreal Elastic Beanstalk refuses to delete an application that has an environment; floci has started to accept it";
                            }
                            // What the service answers with an environment still running: the expected
                            // outcome. Only a refusal about the environment counts, not any bad request.
                            catch (AmazonElasticBeanstalkException ex) when (ex.ErrorCode == "InvalidParameterValue" && ex.Message.Contains("environment", StringComparison.OrdinalIgnoreCase))
                            {
                                return $"{ex.ErrorCode}: {ex.Message}";
                            }
                        }).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // An iterator may not yield from a finally, so the step is yielded below.
            if (claimed.Any)
            {
                cleanup.Add(await this.DeleteEverythingAsync(client, claimed, names, ct).ConfigureAwait(false));
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
    /// cannot classify it on its own. floci answers an operation it recognises but has not built
    /// with HTTP 400 and the error code <c>UnsupportedOperation</c>, which is the not-implemented
    /// outcome here.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                case AmazonServiceException { ErrorCode: "UnsupportedOperation" }:
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

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    private static string DescribeEnvironment(EnvironmentDescription environment)
        => $"{environment.EnvironmentName}: {environment.Status?.Value}, {environment.Health?.Value}, {environment.CNAME}";

    private static string DescribeTree(ApplicationDescription application, List<EnvironmentDescription> environments)
    {
        List<string> branches =
        [
            .. (application.Versions ?? []).Select(v => $"version {v}"),
            .. environments.Select(e => $"environment {DescribeEnvironment(e)} on {e.SolutionStackName} @ {e.VersionLabel}"),
        ];

        List<string> lines = [$"application {application.ApplicationName}"];

        for (int i = 0; i < branches.Count; i++)
        {
            lines.Add($"{(i == branches.Count - 1 ? "└─" : "├─")} {branches[i]}");
        }

        return string.Join("\n", lines);
    }

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
        // stops the run at the step it reached, and RunAsync's finally still deletes everything.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static async Task<ApplicationDescription?> DescribeApplicationAsync(IAmazonElasticBeanstalk client, string name, CancellationToken ct)
        => (await client.DescribeApplicationsAsync(new DescribeApplicationsRequest { ApplicationNames = [name] }, ct).ConfigureAwait(false)).Applications?.FirstOrDefault(a => a.ApplicationName == name);

    /// <summary>The live environment of that name; a terminated one is history, not a resource to clean up.</summary>
    private static async Task<EnvironmentDescription?> DescribeEnvironmentAsync(IAmazonElasticBeanstalk client, string name, CancellationToken ct)
        => (await client.DescribeEnvironmentsAsync(new DescribeEnvironmentsRequest { EnvironmentNames = [name] }, ct).ConfigureAwait(false)).Environments?.FirstOrDefault(e => e.EnvironmentName == name && e.Status != EnvironmentStatus.Terminated);

    /// <summary>
    /// Reads the environment until <paramref name="settled"/> holds. Against floci it reads once, so
    /// an environment that reads Ready at once stays a tripwire rather than something this waits out;
    /// real Elastic Beanstalk spends minutes launching or terminating, so there it polls for up to
    /// twenty minutes and returns whatever it last read.
    /// </summary>
    private async Task<EnvironmentDescription?> WaitForEnvironmentAsync(IAmazonElasticBeanstalk client, string name, Func<EnvironmentDescription?, bool> settled, CancellationToken ct)
    {
        TimeSpan limit = factory.UseEmulator ? TimeSpan.Zero : TimeSpan.FromMinutes(20);
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            EnvironmentDescription? environment = await DescribeEnvironmentAsync(client, name, ct).ConfigureAwait(false);

            if (settled(environment) || Stopwatch.GetElapsedTime(started) >= limit)
            {
                return environment;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The wire-level request shown beside an SDK call: the AWS Query protocol, one form-encoded POST / per operation named by Action.</summary>
    private string Call(string operation, string call) => $"POST {factory.ServiceUrl}/\nAction={operation}&Version=2010-12-01\n{call}";

    /// <summary>
    /// Deletes whatever this run created, looking each one up by its unique name so a resource whose
    /// create response was lost is still found, and one that was never created reads as a truthful
    /// "nothing to remove". The environment goes first, then the application, which takes its
    /// versions and templates with it. Every delete is attempted before any failure is reported, so
    /// one stuck delete does not leak the rest. Uses <see cref="CancellationToken.None"/>: a
    /// cancelled run still has resources to delete.
    /// </summary>
    private async Task<DemoStep> DeleteEverythingAsync(IAmazonElasticBeanstalk client, Claimed claimed, Names names, CancellationToken ct)
    {
        string request = this.Call("TerminateEnvironment", "client.TerminateEnvironmentAsync, DeleteApplicationAsync — each resource this run created");

        return await RunStepAsync("Delete everything — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<string> removed = [];
            List<string> failures = [];

            if (claimed.Environment)
            {
                try
                {
                    if (await DescribeEnvironmentAsync(client, names.Environment, none).ConfigureAwait(false) is not null)
                    {
                        await client.TerminateEnvironmentAsync(new TerminateEnvironmentRequest { EnvironmentName = names.Environment }, none).ConfigureAwait(false);

                        // Real Elastic Beanstalk reads Terminating for minutes, and refuses DeleteApplication until it is done.
                        if (await this.WaitForEnvironmentAsync(client, names.Environment, e => e is null, none).ConfigureAwait(false) is { } left)
                        {
                            failures.Add($"Terminate returned, but environment {names.Environment} still reads {left.Status?.Value}.");
                        }
                        else
                        {
                            removed.Add($"environment {names.Environment}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"environment {names.Environment}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (claimed.Application)
            {
                try
                {
                    if (await DescribeApplicationAsync(client, names.Application, none).ConfigureAwait(false) is not null)
                    {
                        await client.DeleteApplicationAsync(new DeleteApplicationRequest { ApplicationName = names.Application }, none).ConfigureAwait(false);

                        if (await DescribeApplicationAsync(client, names.Application, none).ConfigureAwait(false) is not null)
                        {
                            failures.Add($"Delete returned, but application {names.Application} can still be read.");
                        }
                        else
                        {
                            removed.Add($"application {names.Application} (and its versions)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"application {names.Application}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (failures.Count != 0)
            {
                throw new InvalidOperationException($"{failures.Count} resource(s) not deleted:\n{string.Join("\n", failures)}{cancelled}");
            }

            return removed.Count == 0
                ? $"No resource for this run exists — nothing to remove.{cancelled}"
                : $"Deleted {removed.Count} resource(s), each now absent from its Describe call:\n{string.Join("\n", removed)}{cancelled}";
        }).ConfigureAwait(false);
    }

    /// <summary>The unique names of everything one run creates or asks about.</summary>
    private sealed record Names(string Application, string Environment, string Template);

    /// <summary>Which of this run's resources have had a create call started.</summary>
    private sealed class Claimed
    {
        public bool Application { get; set; }

        public bool Environment { get; set; }

        public bool Any => this.Application || this.Environment;
    }
}
