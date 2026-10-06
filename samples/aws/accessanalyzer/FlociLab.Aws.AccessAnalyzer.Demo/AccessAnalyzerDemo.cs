using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.AccessAnalyzer;
using Amazon.AccessAnalyzer.Model;
using Amazon.Runtime;
using FlociLab.Core;
using AnalyzerType = Amazon.AccessAnalyzer.Type;

namespace FlociLab.Aws.AccessAnalyzer;

/// <summary>
/// An account-zone analyzer created with a tag, refused as a duplicate, read back through
/// <c>ListAnalyzers</c>, then deleted and refused a second delete, against floci. Ordinary
/// AWSSDK.AccessAnalyzer code — the only emulator-aware line in the sample is in
/// <see cref="AccessAnalyzerClientFactory"/>. Findings, archive rules and policy validation are
/// left out because floci answers <c>UnknownOperationException</c> for them (docs/BLAZOR-PLAN.md §14).
/// </summary>
public sealed class AccessAnalyzerDemo(AccessAnalyzerClientFactory factory) : IServiceDemo
{
    private static readonly TimeSpan ActiveTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(2);

    public string Provider => CloudProvider.Aws;

    public string Slug => "accessanalyzer";

    public string DisplayName => "IAM Access Analyzer";

    public string Category => "Identity and access";

    public string Route => "/aws/accessanalyzer";

    /// <summary>
    /// ListAnalyzers. An empty account is still an Ok probe — the service answered — and, unlike
    /// most List calls, an empty page here does not trip AWSSDK's collection handling (probed on a
    /// fresh container, <c>{"analyzers":[]}</c>).
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonAccessAnalyzer client = factory.Create();
            ListAnalyzersResponse response = await client.ListAnalyzersAsync(new ListAnalyzersRequest(), ct).ConfigureAwait(false);
            int count = response.Analyzers?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListAnalyzers returned {count} analyzer(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonAccessAnalyzer client = factory.Create();

        // Unique per run, so two runs never collide. Unlike most AWS resources the analyzer is
        // addressed by this client-chosen name, so it is also all cleanup needs — no ARN to lose.
        string name = $"flocilab-aa-{Guid.NewGuid().ToString("N")[..12]}";
        string url = factory.ServiceUrl;

        bool createAttempted = false;
        bool deleted = false;

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "ListAnalyzers",
                $"GET {url}/analyzer?type=ACCOUNT\nclient.ListAnalyzersAsync(new ListAnalyzersRequest {{ Type = ACCOUNT }})",
                async () =>
                {
                    ListAnalyzersResponse response = await client.ListAnalyzersAsync(new ListAnalyzersRequest { Type = AnalyzerType.ACCOUNT }, ct).ConfigureAwait(false);
                    List<AnalyzerSummary> analyzers = response.Analyzers ?? [];

                    return $"HTTP {(int)response.HttpStatusCode} — {analyzers.Count} account analyzer(s)";
                }).ConfigureAwait(false);

            DemoStep createStep = await RunStepAsync(
                "CreateAnalyzer",
                $"PUT {url}/analyzer\nclient.CreateAnalyzerAsync(new CreateAnalyzerRequest {{ AnalyzerName = \"{name}\", Type = ACCOUNT, Tags = [lab=flocilab] }})",
                async () =>
                {
                    // Claimed before the call, not after: a request that lands while its response is
                    // lost (the page's Dispose cancels mid-flight) still created an analyzer.
                    createAttempted = true;

                    CreateAnalyzerResponse response = await client.CreateAnalyzerAsync(
                        new CreateAnalyzerRequest
                        {
                            AnalyzerName = name,
                            Type = AnalyzerType.ACCOUNT,
                            Tags = new Dictionary<string, string> { ["lab"] = "flocilab" },
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Arn}";
                }).ConfigureAwait(false);

            yield return createStep;

            // With no analyzer, the duplicate check would pass for the wrong reason and every later
            // step would look for a resource that does not exist. The finally still cleans up by name.
            if (!createStep.Succeeded)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "CreateAnalyzer — refused as a duplicate",
                $"PUT {url}/analyzer\nclient.CreateAnalyzerAsync(new CreateAnalyzerRequest {{ AnalyzerName = \"{name}\", Type = ACCOUNT }})   // expect ConflictException",
                async () =>
                {
                    CreateAnalyzerResponse duplicate;

                    try
                    {
                        duplicate = await client.CreateAnalyzerAsync(new CreateAnalyzerRequest { AnalyzerName = name, Type = AnalyzerType.ACCOUNT }, ct).ConfigureAwait(false);
                    }
                    // The refusal is the point of the step, not a failure of it.
                    catch (ConflictException ex)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException($"a second analyzer named {name} was accepted ({duplicate.Arn}).");
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListAnalyzers — the new analyzer reads back ACTIVE",
                $"GET {url}/analyzer?type=ACCOUNT\nclient.ListAnalyzersAsync(new ListAnalyzersRequest {{ Type = ACCOUNT }})   // until {name} is ACTIVE",
                async () =>
                {
                    AnalyzerSummary analyzer = await WaitForActiveAsync(client, name, ct).ConfigureAwait(false);
                    string tags = string.Join(", ", (analyzer.Tags ?? []).Select(t => $"{t.Key}={t.Value}"));

                    if (analyzer.Type != AnalyzerType.ACCOUNT || analyzer.Tags?.GetValueOrDefault("lab") != "flocilab")
                    {
                        throw new InvalidOperationException($"expected an ACCOUNT analyzer tagged lab=flocilab but got {analyzer.Type}, tags: {tags}.");
                    }

                    return $"{analyzer.Arn} — {analyzer.Type}, {analyzer.Status}, {tags}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteAnalyzer",
                $"DELETE {url}/analyzer/{name}\nclient.DeleteAnalyzerAsync(new DeleteAnalyzerRequest {{ AnalyzerName = \"{name}\" }})",
                async () =>
                {
                    DeleteAnalyzerResponse response = await client.DeleteAnalyzerAsync(new DeleteAnalyzerRequest { AnalyzerName = name }, ct).ConfigureAwait(false);

                    deleted = true;

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteAnalyzer — refused once deleted",
                $"DELETE {url}/analyzer/{name}\nclient.DeleteAnalyzerAsync(new DeleteAnalyzerRequest {{ AnalyzerName = \"{name}\" }})   // expect ResourceNotFoundException",
                async () =>
                {
                    try
                    {
                        await client.DeleteAnalyzerAsync(new DeleteAnalyzerRequest { AnalyzerName = name }, ct).ConfigureAwait(false);
                    }
                    catch (ResourceNotFoundException ex)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException("the analyzer can be deleted twice.");
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            if (createAttempted && !deleted)
            {
                cleanup.Add(await this.DeleteAnalyzerByNameAsync(client, name, ct).ConfigureAwait(false));
            }
        }

        foreach (DemoStep step in cleanup)
        {
            yield return step;
        }
    }

    /// <summary>
    /// The AWS SDK reports both of the interesting failures inside an
    /// <see cref="AmazonServiceException"/>, so <see cref="ProbeResult.FromException"/> — which
    /// inspects only the outermost exception — cannot classify them on its own. A 501 arrives as
    /// a status code on the exception; a refused connection arrives with no status code at all
    /// and a transport exception underneath.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
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

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Access Analyzer would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes what exists.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// A new analyzer starts <c>CREATING</c> on real AWS and becomes <c>ACTIVE</c> shortly after;
    /// floci answers <c>ACTIVE</c> at once, so the loop is a single iteration there. There is no
    /// <c>GetAnalyzer</c> to poll on floci (plan §14), so this lists and picks the run's analyzer
    /// out by its unique name.
    /// </summary>
    private static async Task<AnalyzerSummary> WaitForActiveAsync(IAmazonAccessAnalyzer client, string name, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            AnalyzerSummary? analyzer = await FindAnalyzerAsync(client, name, ct).ConfigureAwait(false);

            if (analyzer is null)
            {
                throw new InvalidOperationException($"analyzer {name} is not in ListAnalyzers after it was created.");
            }

            if (analyzer.Status == AnalyzerStatus.ACTIVE)
            {
                return analyzer;
            }

            if (analyzer.Status == AnalyzerStatus.FAILED)
            {
                throw new InvalidOperationException($"analyzer {name} failed: {analyzer.StatusReason?.Code}");
            }

            if (Stopwatch.GetElapsedTime(started) > ActiveTimeout)
            {
                throw new TimeoutException($"analyzer {name} was still {analyzer.Status} after {ActiveTimeout.TotalMinutes:0} minutes.");
            }

            await Task.Delay(ActivePollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Pages <c>ListAnalyzers</c>, since a real account's analyzer can be on any page.</summary>
    private static async Task<AnalyzerSummary?> FindAnalyzerAsync(IAmazonAccessAnalyzer client, string name, CancellationToken ct)
    {
        string? token = null;

        do
        {
            ListAnalyzersResponse page = await client.ListAnalyzersAsync(new ListAnalyzersRequest { Type = AnalyzerType.ACCOUNT, NextToken = token }, ct).ConfigureAwait(false);
            AnalyzerSummary? match = page.Analyzers?.Find(a => a.Name == name);

            if (match is not null)
            {
                return match;
            }

            token = page.NextToken;
        }
        while (token is not null);

        return null;
    }

    /// <summary>
    /// Cleanup uses <see cref="CancellationToken.None"/> — a run that was cancelled still has an
    /// analyzer to remove. Deleting by name is safe to attempt blind: a create that never landed
    /// answers <c>ResourceNotFoundException</c>, which is a truthful "nothing to remove".
    /// </summary>
    private async Task<DemoStep> DeleteAnalyzerByNameAsync(IAmazonAccessAnalyzer client, string name, CancellationToken ct)
    {
        string request = $"DELETE {factory.ServiceUrl}/analyzer/{name}\nclient.DeleteAnalyzerAsync(new DeleteAnalyzerRequest {{ AnalyzerName = \"{name}\" }})";

        return await RunStepAsync("DeleteAnalyzer — cleanup", request, async () =>
        {
            try
            {
                DeleteAnalyzerResponse response = await client.DeleteAnalyzerAsync(new DeleteAnalyzerRequest { AnalyzerName = name }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — deleted {name}"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            // Not a failure: the create never landed, and the server says so.
            catch (ResourceNotFoundException)
            {
                return $"No analyzer named {name} exists — nothing to remove.";
            }
        }).ConfigureAwait(false);
    }
}
