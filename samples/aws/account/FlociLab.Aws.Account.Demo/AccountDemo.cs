using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Account;
using Amazon.Account.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Account;

/// <summary>
/// Sets the account's SECURITY alternate contact, reads it back, overwrites it, shows a malformed
/// request being refused, tries to delete it and puts the account back as it was. Ordinary
/// AWSSDK.Account code — the only emulator-aware line in the sample is in
/// <see cref="AccountClientFactory"/>. The contact belongs to the account, not to the run, so the
/// run snapshots what was there first and restores it; floci has no <c>DeleteAlternateContact</c>,
/// so an account that started with none keeps this run's contact there (docs/BLAZOR-PLAN.md §14).
/// </summary>
public sealed class AccountDemo(AccountClientFactory factory) : IServiceDemo
{
    // Every contact name this sample writes starts with it, which is how a run tells a contact an
    // earlier run left behind from one the account really has.
    private const string NamePrefix = "flocilab-";

    // The SECURITY contact is one value for the whole account, and this demo is a singleton: two
    // overlapping runs would each snapshot the other's contact and restore it, leaving a fake one
    // behind. 1 while a run holds the account. Guards one process only; two hosts against the same
    // account can still overlap.
    private int running;

    public string Provider => CloudProvider.Aws;

    public string Slug => "account";

    public string DisplayName => "AWS Account";

    public string Category => "Identity and access";

    public string Route => "/aws/account";

    /// <summary>GetAlternateContact. An account with no contact answers ResourceNotFoundException, which still means the service is up.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonAccount client = factory.Create();
            AlternateContact? contact = await GetContactAsync(client, ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), contact is null ? "GetAlternateContact: the account has no SECURITY contact." : "GetAlternateContact: the account has a SECURITY contact.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref this.running, 1, 0) != 0)
        {
            yield return DemoStep.Failed("Another run is in progress", new InvalidOperationException("another run is changing this account's SECURITY contact; wait for it to finish."), string.Empty);
            yield break;
        }

        try
        {
            await foreach (DemoStep step in this.RunExclusiveAsync(ct).ConfigureAwait(false))
            {
                yield return step;
            }
        }
        finally
        {
            Volatile.Write(ref this.running, 0);
        }
    }

    private async IAsyncEnumerable<DemoStep> RunExclusiveAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonAccount client = factory.Create();

        // Unique per run, and prefixed, so a contact left behind by an earlier run is recognisable as one.
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string name = $"{NamePrefix}{suffix}";
        string overwritten = $"{name}-overwritten";

        AlternateContact? previous = null;
        bool snapshotTaken = false;

        // Claimed before the call, not after: a request that lands while its response is lost (the
        // page's Dispose cancels mid-flight) still replaced the contact.
        bool putAttempted = false;
        bool removed = false;
        bool deleteUnsupported = false;

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep snapshotStep = await RunStepAsync(
                "GetAlternateContact — what is there already",
                this.Wire("/getAlternateContact", "client.GetAlternateContactAsync(new GetAlternateContactRequest { AlternateContactType = SECURITY })"),
                async () =>
                {
                    AlternateContact? found = await GetContactAsync(client, ct).ConfigureAwait(false);
                    snapshotTaken = true;

                    if (found is null)
                    {
                        return "ResourceNotFoundException — the account has no SECURITY contact, so at the end this run deletes the one it adds, where the service can.";
                    }

                    // A run that died before its finally (a crashed process) leaves its contact
                    // behind. Restoring that would make the fake contact permanent, so it is
                    // treated as no contact at all.
                    if (found.Name?.StartsWith(NamePrefix, StringComparison.Ordinal) == true)
                    {
                        return $"{found.Name} <{found.EmailAddress}> — left by an earlier FlociLab run, so it is not put back; this run deletes it with its own, where the service can.";
                    }

                    previous = found;

                    return $"{previous.Name} <{previous.EmailAddress}> — kept, and put back at the end of the run.";
                }).ConfigureAwait(false);

            yield return snapshotStep;

            // Without a snapshot there is nothing to restore, so nothing may be overwritten.
            if (!snapshotStep.Succeeded)
            {
                yield break;
            }

            DemoStep putStep = await RunStepAsync(
                "PutAlternateContact",
                this.Wire("/putAlternateContact", $"client.PutAlternateContactAsync(new PutAlternateContactRequest {{ AlternateContactType = SECURITY, Name = \"{name}\", Title = \"Security lead\", EmailAddress = \"{name}@example.com\", PhoneNumber = \"+15555550100\" }})"),
                async () =>
                {
                    putAttempted = true;
                    PutAlternateContactResponse response = await client.PutAlternateContactAsync(Request(name, "Security lead", $"{name}@example.com"), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return putStep;

            if (!putStep.Succeeded)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "GetAlternateContact — read back",
                this.Wire("/getAlternateContact", "client.GetAlternateContactAsync(new GetAlternateContactRequest { AlternateContactType = SECURITY })"),
                async () =>
                {
                    AlternateContact contact = await GetContactAsync(client, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("PutAlternateContact returned, but GetAlternateContact finds no SECURITY contact.");

                    if (contact.Name != name || contact.EmailAddress != $"{name}@example.com" || contact.Title != "Security lead")
                    {
                        throw new InvalidOperationException($"the contact reads back as {contact.Name} <{contact.EmailAddress}>, {contact.Title}, not what was put.");
                    }

                    return $"{contact.AlternateContactType} — {contact.Name} <{contact.EmailAddress}>, {contact.Title}, {contact.PhoneNumber}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutAlternateContact — overwrite",
                this.Wire("/putAlternateContact", $"client.PutAlternateContactAsync(new PutAlternateContactRequest {{ AlternateContactType = SECURITY, Name = \"{overwritten}\", Title = \"Deputy\", ... }})"),
                async () =>
                {
                    // One contact per type: a second put replaces the first rather than adding to it.
                    PutAlternateContactResponse response = await client.PutAlternateContactAsync(Request(overwritten, "Deputy", $"{name}@example.com"), ct).ConfigureAwait(false);
                    AlternateContact contact = await GetContactAsync(client, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("the contact is gone after the second PutAlternateContact.");

                    if (contact.Name != overwritten || contact.Title != "Deputy")
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode}, but the contact still reads back as {contact.Name}, {contact.Title}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — the contact reads back as {contact.Name}, {contact.Title}; one contact per type, so the first was replaced";
                }).ConfigureAwait(false);

            // Emulator-only: AWS documents ValidationException for an empty email, but that has not
            // been exercised against a real account, and if it were accepted the account's security
            // contact would have no address until the restore. A run against a real account sends
            // nothing it has not been shown to refuse.
            if (factory.UseEmulator)
            {
                yield return await RunStepAsync(
                    "PutAlternateContact — refused without an email",
                    this.Wire("/putAlternateContact", "client.PutAlternateContactAsync(new PutAlternateContactRequest { AlternateContactType = SECURITY, EmailAddress = \"\", ... })"),
                    async () =>
                    {
                        PutAlternateContactRequest bad = Request(overwritten, "Deputy", $"{name}@example.com");
                        bad.EmailAddress = string.Empty;

                        try
                        {
                            await client.PutAlternateContactAsync(bad, ct).ConfigureAwait(false);
                        }
                        // The expected answer, shown as a successful step.
                        catch (ValidationException ex)
                        {
                            return $"{ex.GetType().Name}: {ex.Message}";
                        }

                        throw new InvalidOperationException("a contact with no email address was accepted.");
                    }).ConfigureAwait(false);
            }

            // Only for a contact the run owns: deleting a contact the account already had would
            // leave it with none if the restore then failed.
            if (previous is null)
            {
                yield return await RunStepAsync(
                    "DeleteAlternateContact",
                    this.Wire("/deleteAlternateContact", "client.DeleteAlternateContactAsync(new DeleteAlternateContactRequest { AlternateContactType = SECURITY })"),
                    async () =>
                    {
                        try
                        {
                            DeleteAlternateContactResponse response = await client.DeleteAlternateContactAsync(new DeleteAlternateContactRequest { AlternateContactType = AlternateContactType.SECURITY }, ct).ConfigureAwait(false);
                            removed = true;

                            return $"HTTP {(int)response.HttpStatusCode}";
                        }
                        // floci answers a 404 UnknownOperationException, not a 501. Reported as a NOTE
                        // rather than a failure: it is the documented outcome, and the next step puts
                        // the account back regardless.
                        catch (AmazonAccountException ex) when (factory.UseEmulator && ex is { StatusCode: HttpStatusCode.NotFound, ErrorCode: "UnknownOperationException" })
                        {
                            deleteUnsupported = true;

                            return $"{ex.ErrorCode}: {ex.Message}\nNOTE: floci has no DeleteAlternateContact, so the contact cannot be removed here.";
                        }
                    }).ConfigureAwait(false);
            }
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so the account is put back even from a run that stopped halfway. The step it
            // produces is yielded below — an iterator may not yield from inside a finally.
            if (putAttempted && snapshotTaken)
            {
                cleanup.Add(await this.RestoreAsync(client, previous, removed, deleteUnsupported, ct).ConfigureAwait(false));
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
    /// the emulator does something real Account Management would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still restores the contact.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static PutAlternateContactRequest Request(string name, string title, string email) => new()
    {
        AlternateContactType = AlternateContactType.SECURITY,
        Name = name,
        Title = title,
        EmailAddress = email,
        PhoneNumber = "+15555550100",
    };

    /// <summary>Null when the account has no SECURITY contact — the service's way of saying so is an exception, not an empty response.</summary>
    private static async Task<AlternateContact?> GetContactAsync(IAmazonAccount client, CancellationToken ct)
    {
        try
        {
            GetAlternateContactResponse response = await client.GetAlternateContactAsync(new GetAlternateContactRequest { AlternateContactType = AlternateContactType.SECURITY }, ct).ConfigureAwait(false);

            return response.AlternateContact;
        }
        catch (ResourceNotFoundException)
        {
            // No contact of that type is a legitimate answer, reported to the caller as null.
            return null;
        }
    }

    /// <summary>The wire-level request shown beside an SDK call: Account Management is REST-JSON, one path per operation.</summary>
    private string Wire(string path, string call) => $"POST {factory.ServiceUrl}{path}\n{call}";

    /// <summary>
    /// Cleanup uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a
    /// contact to put back. A contact that existed before is written back; one that did not is
    /// deleted, which floci cannot do, so there the run's contact stays and the step says so.
    /// </summary>
    private async Task<DemoStep> RestoreAsync(IAmazonAccount client, AlternateContact? previous, bool removed, bool deleteUnsupported, CancellationToken ct)
    {
        string request = previous is null
            ? this.Wire("/deleteAlternateContact", "client.DeleteAlternateContactAsync(new DeleteAlternateContactRequest { AlternateContactType = SECURITY })")
            : this.Wire("/putAlternateContact", "client.PutAlternateContactAsync(new PutAlternateContactRequest { AlternateContactType = SECURITY, ... = the contact from the first step })");

        return await RunStepAsync("Put the account back — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;

            if (previous is not null)
            {
                await client.PutAlternateContactAsync(
                    new PutAlternateContactRequest
                    {
                        AlternateContactType = AlternateContactType.SECURITY,
                        Name = previous.Name,
                        Title = previous.Title,
                        EmailAddress = previous.EmailAddress,
                        PhoneNumber = previous.PhoneNumber,
                    }, CancellationToken.None).ConfigureAwait(false);

                return $"Restored {previous.Name} <{previous.EmailAddress}>.{cancelled}";
            }

            if (removed)
            {
                return $"The run's contact was already deleted — nothing to restore.{cancelled}";
            }

            if (deleteUnsupported)
            {
                return $"The account had no SECURITY contact of its own before this run and floci cannot delete one, so the run's contact stays. The next run recognises it as a leftover and does not put it back.{cancelled}";
            }

            try
            {
                await client.DeleteAlternateContactAsync(new DeleteAlternateContactRequest { AlternateContactType = AlternateContactType.SECURITY }, CancellationToken.None).ConfigureAwait(false);
            }
            // A run cancelled before its own delete step never learned floci has none.
            catch (AmazonAccountException ex) when (factory.UseEmulator && ex is { StatusCode: HttpStatusCode.NotFound, ErrorCode: "UnknownOperationException" })
            {
                return $"floci cannot delete a contact, so the run's SECURITY contact stays.{cancelled}";
            }

            return $"Deleted the run's SECURITY contact.{cancelled}";
        }).ConfigureAwait(false);
    }
}
