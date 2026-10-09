using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.CodeGuruReviewer;
using Amazon.CodeGuruReviewer.Model;
using Amazon.Runtime;
using FlociLab.Core;
using ReviewType = Amazon.CodeGuruReviewer.Type;

namespace FlociLab.Aws.CodeGuruReviewer;

/// <summary>
/// Associates a CodeCommit repository with CodeGuru Reviewer, reads the association back by ARN and
/// in the list, tags it, asks for the association of an ARN that does not exist and for the list of
/// code reviews, then disassociates it. Ordinary AWSSDK.CodeGuruReviewer code — the only
/// emulator-aware line is in <see cref="CodeGuruReviewerClientFactory"/>. floci keeps the
/// association as a record: it never reads the repository, so the name need not exist, and it
/// builds no code reviews, where real CodeGuru Reviewer analyses the code. Every step reports what
/// the engine actually answered rather than what the docs promise.
/// </summary>
public sealed class CodeGuruReviewerDemo(CodeGuruReviewerClientFactory factory) : IServiceDemo
{
    public string Provider => CloudProvider.Aws;

    public string Slug => "codegurureviewer";

    public string DisplayName => "CodeGuru Reviewer";

    public string Category => "Developer tools and delivery";

    public string Route => "/aws/codegurureviewer";

    /// <summary>ListRepositoryAssociations — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonCodeGuruReviewer client = factory.Create();
            ListRepositoryAssociationsResponse response = await client.ListRepositoryAssociationsAsync(new ListRepositoryAssociationsRequest(), ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListRepositoryAssociations: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonCodeGuruReviewer client = factory.Create();

        string repository = $"flocilab-{Guid.NewGuid().ToString("N")[..12]}";

        // Claimed before the create: a call that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created the association, so the finally looks it up by
        // name rather than trusting that a response arrived.
        bool claimed = false;
        string? arn = null;

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep associated = await RunStepAsync(
                "AssociateRepository, DescribeRepositoryAssociation — a CodeCommit repository",
                this.Call("POST", "/associations", $"client.AssociateRepositoryAsync(new AssociateRepositoryRequest {{ Repository = new Repository {{ CodeCommit = new CodeCommitRepository {{ Name = \"{repository}\" }} }} }})\nclient.DescribeRepositoryAssociationAsync(new DescribeRepositoryAssociationRequest {{ AssociationArn = arn }})"),
                async () =>
                {
                    claimed = true;
                    AssociateRepositoryResponse created = await client.AssociateRepositoryAsync(
                        new AssociateRepositoryRequest
                        {
                            Repository = new Repository { CodeCommit = new CodeCommitRepository { Name = repository } },
                            ClientRequestToken = Guid.NewGuid().ToString(),
                        }, ct).ConfigureAwait(false);

                    arn = created.RepositoryAssociation.AssociationArn;

                    RepositoryAssociation found = (await client.DescribeRepositoryAssociationAsync(new DescribeRepositoryAssociationRequest { AssociationArn = arn }, ct).ConfigureAwait(false)).RepositoryAssociation;

                    return factory.UseEmulator
                        ? $"{DescribeAssociation(found)}\n{found.AssociationArn}\nfloci never reads the repository: the association reads {found.State?.Value} at once, real CodeGuru Reviewer reads Associating while it checks the repository exists"
                        : $"{DescribeAssociation(found)}\n{found.AssociationArn}";
                }).ConfigureAwait(false);

            yield return associated;

            // A failed associate means every later step would look for an association that is not there.
            // Skipped rather than `yield break`, which would also skip yielding the cleanup step below.
            if (associated.Succeeded)
            {
                yield return await RunStepAsync(
                    "ListRepositoryAssociations — finding it by name",
                    this.Call("GET", $"/associations?Name={repository}", $"client.ListRepositoryAssociationsAsync(new ListRepositoryAssociationsRequest {{ Names = [\"{repository}\"] }})"),
                    async () =>
                    {
                        List<RepositoryAssociationSummary> found = (await client.ListRepositoryAssociationsAsync(new ListRepositoryAssociationsRequest { Names = [repository] }, ct).ConfigureAwait(false)).RepositoryAssociationSummaries ?? [];

                        if (!found.Any(a => a.AssociationArn == arn))
                        {
                            throw new InvalidOperationException($"the association was accepted but ListRepositoryAssociations lists {found.Count} association(s) without it.");
                        }

                        return string.Join("\n", found.Select(a => $"{a.Name}: {a.State?.Value}, {a.ProviderType?.Value}, owner {a.Owner}"));
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "TagResource, ListTagsForResource — labelling the association",
                    this.Call("POST", $"/tags/{arn}", "client.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = new Dictionary<string, string> { [\"env\"] = \"lab\" } })\nclient.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })"),
                    async () =>
                    {
                        await client.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = new Dictionary<string, string> { ["env"] = "lab" } }, ct).ConfigureAwait(false);

                        Dictionary<string, string> tags = (await client.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn }, ct).ConfigureAwait(false)).Tags ?? [];

                        if (!tags.TryGetValue("env", out string? value) || value != "lab")
                        {
                            throw new InvalidOperationException($"the tag was accepted but ListTagsForResource answers {tags.Count} tag(s) without env=lab.");
                        }

                        return string.Join("\n", tags.Select(t => $"{t.Key}={t.Value}"));
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "DescribeRepositoryAssociation — an ARN that does not exist",
                    this.Call("GET", "/associations/{arn}", "client.DescribeRepositoryAssociationAsync(new DescribeRepositoryAssociationRequest { AssociationArn = <unknown arn> })"),
                    async () =>
                    {
                        string unknown = arn![..arn!.LastIndexOf(':')] + ":" + Guid.NewGuid();

                        try
                        {
                            await client.DescribeRepositoryAssociationAsync(new DescribeRepositoryAssociationRequest { AssociationArn = unknown }, ct).ConfigureAwait(false);

                            return "accepted\nreal CodeGuru Reviewer answers NotFoundException for an association that does not exist; floci has started to accept it";
                        }
                        // What real CodeGuru Reviewer answers: the expected outcome, shown as a successful step.
                        catch (NotFoundException ex)
                        {
                            return $"{ex.ErrorCode}: {ex.Message}";
                        }
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "ListCodeReviews — the analyses CodeGuru Reviewer has run",
                    this.Call("GET", "/codereviews?Type=RepositoryAnalysis", "client.ListCodeReviewsAsync(new ListCodeReviewsRequest { Type = ReviewType.RepositoryAnalysis })"),
                    async () =>
                    {
                        try
                        {
                            List<CodeReviewSummary> reviews = (await client.ListCodeReviewsAsync(new ListCodeReviewsRequest { Type = ReviewType.RepositoryAnalysis }, ct).ConfigureAwait(false)).CodeReviewSummaries ?? [];

                            return factory.UseEmulator
                                ? $"{reviews.Count} code review(s)\nfloci now implements code reviews: delete this step's tripwire and add CreateCodeReview"
                                : $"{reviews.Count} code review(s)";
                        }
                        // Not a 501: floci answers an operation it has not built with HTTP 404 and this
                        // code. Real CodeGuru Reviewer lists the reviews.
                        catch (AmazonCodeGuruReviewerException ex) when (ex.ErrorCode == "UnknownOperationException")
                        {
                            return $"{ex.ErrorCode}: {ex.Message}\nreal CodeGuru Reviewer lists code reviews; floci builds the association half of the service and no reviews";
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
                cleanup.Add(await this.DisassociateAsync(client, repository, ct).ConfigureAwait(false));
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
    /// the error code <c>UnknownOperationException</c>, which is the not-implemented outcome here.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                case AmazonServiceException { ErrorCode: "UnknownOperationException" }:
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

    private static string DescribeAssociation(RepositoryAssociation association)
        => $"{association.Name}: {association.State?.Value}, {association.ProviderType?.Value}, owner {association.Owner}";

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
        // stops the run at the step it reached, and RunAsync's finally still disassociates.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>The associations of that name that are still associated; a disassociated one is history, not a resource to clean up.</summary>
    private static async Task<List<RepositoryAssociationSummary>> FindAsync(IAmazonCodeGuruReviewer client, string name, CancellationToken ct)
        => (await client.ListRepositoryAssociationsAsync(new ListRepositoryAssociationsRequest { Names = [name] }, ct).ConfigureAwait(false)).RepositoryAssociationSummaries?
            .Where(a => a.Name == name && a.State != RepositoryAssociationState.Disassociated)
            .ToList() ?? [];

    /// <summary>
    /// Real AWS only: a run takes seconds, real CodeGuru Reviewer reads Associating for longer while
    /// it checks the repository, and it can refuse a disassociate until that settles. Polls for up to
    /// two minutes; floci reads Associated at once, so it is never called there.
    /// </summary>
    private static async Task WaitWhileAssociatingAsync(IAmazonCodeGuruReviewer client, string arn, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);

        while (DateTime.UtcNow < deadline)
        {
            RepositoryAssociation association = (await client.DescribeRepositoryAssociationAsync(new DescribeRepositoryAssociationRequest { AssociationArn = arn }, ct).ConfigureAwait(false)).RepositoryAssociation;

            if (association.State != RepositoryAssociationState.Associating)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The wire-level request shown beside an SDK call: REST JSON, one method and path per operation.</summary>
    private string Call(string method, string path, string call) => $"{method} {factory.ServiceUrl}{path}\n{call}";

    /// <summary>
    /// Disassociates whatever this run created, looking the association up by its unique name so one
    /// whose create response was lost is still found, and one that was never created reads as a
    /// truthful "nothing to remove". Uses <see cref="CancellationToken.None"/>: a cancelled run still
    /// has an association to remove.
    /// </summary>
    private async Task<DemoStep> DisassociateAsync(IAmazonCodeGuruReviewer client, string name, CancellationToken ct)
    {
        string request = this.Call("DELETE", "/associations/{arn}", "client.DisassociateRepositoryAsync(new DisassociateRepositoryRequest { AssociationArn = arn })");

        return await RunStepAsync("DisassociateRepository — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<RepositoryAssociationSummary> found = await FindAsync(client, name, none).ConfigureAwait(false);

            if (found.Count == 0)
            {
                return $"No association for this run exists — nothing to remove.{cancelled}";
            }

            List<string> failures = [];

            foreach (RepositoryAssociationSummary association in found)
            {
                try
                {
                    if (!factory.UseEmulator)
                    {
                        await WaitWhileAssociatingAsync(client, association.AssociationArn, none).ConfigureAwait(false);
                    }

                    await client.DisassociateRepositoryAsync(new DisassociateRepositoryRequest { AssociationArn = association.AssociationArn }, none).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add($"{association.AssociationArn}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Real CodeGuru Reviewer reads Disassociating for a while before the list drops it: that is
            // the disassociate taking effect, not an association left behind. floci drops it at once.
            int remaining = (await FindAsync(client, name, none).ConfigureAwait(false)).Count(a => a.State != RepositoryAssociationState.Disassociating);

            if (failures.Count != 0 || remaining != 0)
            {
                throw new InvalidOperationException($"{Math.Max(failures.Count, remaining)} association(s) not removed:\n{string.Join("\n", failures)}{cancelled}");
            }

            return $"Disassociated {found.Count} association(s) of {name}, now absent from ListRepositoryAssociations.{cancelled}";
        }).ConfigureAwait(false);
    }
}
