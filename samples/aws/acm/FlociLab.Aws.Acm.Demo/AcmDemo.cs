using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using Amazon.CertificateManager;
using Amazon.CertificateManager.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Acm;

/// <summary>
/// Requests a DNS-validated certificate, reads it back, fetches and parses its PEM, tags it, lists
/// it, asks for two requests real ACM would refuse and deletes everything it made. Ordinary
/// AWSSDK.CertificateManager code — the only emulator-aware line is in <see cref="AcmClientFactory"/>.
/// What differs from real ACM is when the certificate is usable: real ACM leaves it
/// <c>PENDING_VALIDATION</c> until the validation CNAME is in DNS, floci issues it in the request
/// call, and every step reports what the engine actually answered rather than what the docs promise.
/// </summary>
public sealed class AcmDemo(AcmClientFactory factory) : IServiceDemo
{
    public string Provider => CloudProvider.Aws;

    public string Slug => "acm";

    public string DisplayName => "ACM";

    public string Category => "Identity and access";

    public string Route => "/aws/acm";

    /// <summary>ListCertificates — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonCertificateManager client = factory.Create();
            ListCertificatesResponse response = await client.ListCertificatesAsync(new ListCertificatesRequest { MaxItems = 1 }, ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListCertificates: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonCertificateManager client = factory.Create();

        string suffix = Guid.NewGuid().ToString("N")[..12];
        string domain = $"flocilab-{suffix}.example.com";
        string alternative = $"www.{domain}";
        string notADomain = $"not a domain {suffix}!";

        // Claimed before each call: a request that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still issued a certificate, so the finally looks them up by
        // their unique domain names as well as deleting the ARNs whose responses did arrive. Real
        // ACM's list is eventually consistent, so neither source is enough on its own.
        HashSet<string> requestedDomains = [];
        HashSet<string> knownArns = [];
        string arn = string.Empty;

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep request = await RunStepAsync(
                "RequestCertificate — DNS validation",
                this.Call("RequestCertificate", $"client.RequestCertificateAsync(new RequestCertificateRequest {{ DomainName = \"{domain}\", SubjectAlternativeNames = [\"{alternative}\"], ValidationMethod = DNS }})"),
                async () =>
                {
                    requestedDomains.Add(domain);
                    RequestCertificateResponse response = await client.RequestCertificateAsync(
                        new RequestCertificateRequest
                        {
                            DomainName = domain,
                            SubjectAlternativeNames = [alternative],
                            ValidationMethod = ValidationMethod.DNS,
                        }, ct).ConfigureAwait(false);

                    arn = response.CertificateArn;
                    knownArns.Add(arn);

                    return response.CertificateArn;
                }).ConfigureAwait(false);

            yield return request;

            if (!request.Succeeded)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "DescribeCertificate — the status straight after the request",
                this.Call("DescribeCertificate", "client.DescribeCertificateAsync(new DescribeCertificateRequest { CertificateArn = ... })"),
                async () =>
                {
                    DescribeCertificateResponse response = await client.DescribeCertificateAsync(new DescribeCertificateRequest { CertificateArn = arn }, ct).ConfigureAwait(false);
                    CertificateDetail detail = response.Certificate;

                    if (detail.DomainName != domain)
                    {
                        throw new InvalidOperationException($"asked for {domain}, the certificate is for {detail.DomainName}.");
                    }

                    string validation = string.Join("\n", (detail.DomainValidationOptions ?? []).Select(o => $"{o.DomainName}: {o.ValidationStatus?.Value}, {o.ResourceRecord?.Type?.Value} {o.ResourceRecord?.Name}"));
                    string verdict = detail.Status == CertificateStatus.ISSUED
                        ? "ISSUED already; real ACM would still be PENDING_VALIDATION until that CNAME is in DNS"
                        : $"{detail.Status?.Value}, as real ACM would be until that CNAME is in DNS";

                    return $"{verdict}\nissuer {detail.Issuer}, {detail.KeyAlgorithm?.Value}, {detail.Type?.Value}\n{validation}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetCertificate — the PEM",
                this.Call("GetCertificate", "client.GetCertificateAsync(new GetCertificateRequest { CertificateArn = ... })"),
                async () =>
                {
                    try
                    {
                        GetCertificateResponse response = await client.GetCertificateAsync(new GetCertificateRequest { CertificateArn = arn }, ct).ConfigureAwait(false);
                        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(response.Certificate);

                        if (certificate.GetNameInfo(X509NameType.SimpleName, false) != domain)
                        {
                            throw new InvalidOperationException($"asked for {domain}, the PEM is for {certificate.Subject}.");
                        }

                        bool chain = !string.IsNullOrEmpty(response.CertificateChain);

                        return $"{certificate.Subject}, issued by {certificate.Issuer}\nvalid {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}, chain {(chain ? "returned" : "empty")}";
                    }
                    // What real ACM answers while the certificate is still pending — an outcome, not a failure.
                    catch (RequestInProgressException ex)
                    {
                        return $"{ex.GetType().Name}: {ex.Message}";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AddTagsToCertificate, ListTagsForCertificate",
                this.Call("AddTagsToCertificate", "client.AddTagsToCertificateAsync(... Tags = [run = " + suffix + "])"),
                async () =>
                {
                    await client.AddTagsToCertificateAsync(
                        new AddTagsToCertificateRequest
                        {
                            CertificateArn = arn,
                            Tags = [new Tag { Key = "run", Value = suffix }],
                        }, ct).ConfigureAwait(false);

                    ListTagsForCertificateResponse tags = await client.ListTagsForCertificateAsync(new ListTagsForCertificateRequest { CertificateArn = arn }, ct).ConfigureAwait(false);

                    if (!(tags.Tags ?? []).Exists(t => t.Key == "run" && t.Value == suffix))
                    {
                        throw new InvalidOperationException("the tag was accepted but ListTagsForCertificate does not return it.");
                    }

                    return string.Join("\n", tags.Tags!.Select(t => $"{t.Key} = {t.Value}"));
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListCertificates",
                this.Call("ListCertificates", "client.ListCertificatesAsync(new ListCertificatesRequest())"),
                async () =>
                {
                    List<CertificateSummary> all = await ListAllAsync(client, ct).ConfigureAwait(false);
                    CertificateSummary? ours = all.Find(c => c.CertificateArn == arn);

                    if (ours is null)
                    {
                        throw new InvalidOperationException($"{arn} was requested but is not listed.");
                    }

                    return $"{ours.DomainName} — {ours.Status?.Value}, {ours.Type?.Value}, in use: {ours.InUse}\n{all.Count} certificate(s) in the account";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "RequestCertificate — a name that is not a domain",
                this.Call("RequestCertificate", $"client.RequestCertificateAsync(new RequestCertificateRequest {{ DomainName = \"{notADomain}\" }})"),
                async () =>
                {
                    try
                    {
                        requestedDomains.Add(notADomain);
                        RequestCertificateResponse response = await client.RequestCertificateAsync(new RequestCertificateRequest { DomainName = notADomain }, ct).ConfigureAwait(false);
                        knownArns.Add(response.CertificateArn);

                        return $"accepted: {response.CertificateArn}\nreal ACM refuses a name that is not a valid domain; floci does not check";
                    }
                    // What real ACM answers: the expected outcome, shown as a successful step.
                    catch (AmazonCertificateManagerException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
                    {
                        return $"{ex.GetType().Name}: {ex.Message}";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "RequestCertificate — refused when the validation method is unknown",
                this.Call("RequestCertificate", $"client.RequestCertificateAsync(new RequestCertificateRequest {{ DomainName = \"{domain}\", ValidationMethod = \"BOGUS\" }})"),
                async () =>
                {
                    try
                    {
                        requestedDomains.Add(domain);
                        await client.RequestCertificateAsync(
                            new RequestCertificateRequest { DomainName = domain, ValidationMethod = new ValidationMethod("BOGUS") }, ct).ConfigureAwait(false);
                    }
                    // The expected answer, shown as a successful step.
                    catch (AmazonCertificateManagerException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
                    {
                        return $"{ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException("a validation method that is neither DNS nor EMAIL was accepted.");
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // An iterator may not yield from a finally, so the step is yielded below.
            if (requestedDomains.Count != 0)
            {
                cleanup.Add(await this.DeleteCertificatesAsync(client, requestedDomains, knownArns, ct).ConfigureAwait(false));
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
    /// inspects only the outermost exception — cannot classify them on its own.
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
    /// failure becomes a step carrying the error text.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still deletes the certificates.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static async Task<List<CertificateSummary>> ListAllAsync(IAmazonCertificateManager client, CancellationToken ct)
    {
        List<CertificateSummary> all = [];
        string? token = null;

        do
        {
            ListCertificatesResponse page = await client.ListCertificatesAsync(new ListCertificatesRequest { NextToken = token }, ct).ConfigureAwait(false);
            all.AddRange(page.CertificateSummaryList ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    /// <summary>The wire-level request shown beside an SDK call: AWS JSON 1.1, one POST / per operation, the operation in the target header.</summary>
    private string Call(string operation, string call) => $"POST {factory.ServiceUrl}/\nX-Amz-Target: CertificateManager.{operation}\n{call}";

    /// <summary>
    /// Deletes the ARNs whose request responses arrived, plus any certificate listed under one of
    /// this run's unique domain names, so one whose response was lost is still found, and a request
    /// the server refused finds nothing — a truthful "nothing to remove". Every certificate is
    /// attempted before any failure is reported, so one stuck delete does not leak the rest. Uses
    /// <see cref="CancellationToken.None"/>: a cancelled run still has certificates to delete.
    /// </summary>
    private async Task<DemoStep> DeleteCertificatesAsync(IAmazonCertificateManager client, HashSet<string> domains, HashSet<string> knownArns, CancellationToken ct)
    {
        string request = this.Call("DeleteCertificate", "client.DeleteCertificateAsync(new DeleteCertificateRequest { CertificateArn = <each certificate this run requested> })");

        return await RunStepAsync("DeleteCertificate — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            HashSet<string> found = [.. knownArns];
            found.UnionWith((await ListAllAsync(client, CancellationToken.None).ConfigureAwait(false))
                .Where(c => domains.Contains(c.DomainName))
                .Select(c => c.CertificateArn));

            if (found.Count == 0)
            {
                return $"No certificate for this run exists — nothing to remove.{cancelled}";
            }

            List<string> failures = [];

            foreach (string arn in found)
            {
                try
                {
                    await client.DeleteCertificateAsync(new DeleteCertificateRequest { CertificateArn = arn }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ResourceNotFoundException)
                {
                    // Already gone, which is what this step wants; the check below confirms it.
                }
                catch (AmazonCertificateManagerException ex)
                {
                    failures.Add($"{arn}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                // The certificate must be gone, not merely the call accepted.
                try
                {
                    await client.DescribeCertificateAsync(new DescribeCertificateRequest { CertificateArn = arn }, CancellationToken.None).ConfigureAwait(false);
                    failures.Add($"DeleteCertificate returned, but {arn} can still be read.");
                }
                catch (ResourceNotFoundException)
                {
                    // The answer this check is waiting for: the certificate is gone.
                }
            }

            if (failures.Count != 0)
            {
                throw new InvalidOperationException($"{failures.Count} of {found.Count} certificate(s) not deleted:\n{string.Join("\n", failures)}{cancelled}");
            }

            return $"Deleted {found.Count} certificate(s); DescribeCertificate now answers ResourceNotFoundException.{cancelled}";
        }).ConfigureAwait(false);
    }
}
