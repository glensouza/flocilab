using Azure;
using Azure.Security.KeyVault.Keys;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Azure.KeyVaultKeys;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-az per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
///
/// Key Vault Keys was ⊘ from 2026-09-01 to 2026-10-06. Through floci-az 0.12.0 every <c>/keys</c>
/// route answered a plain 404. 0.13.0 routed them but misrouted the SDK's trailing-slash list and
/// sent unset <c>attributes.nbf</c>/<c>exp</c> as JSON <c>null</c>, which the SDK cannot parse.
/// floci-az 0.14.0 fixed both, through floci-az PR #349 (issue #348) from this project (§14). These
/// tests used to pin the failures; they now pin the round trip.
/// </summary>
[Collection(nameof(AzureKeyVaultCollection))]
public sealed class AzureKeyVaultKeysTests : IAsyncLifetime
{
    private const int FlociAzPort = 4577;

    private readonly IContainer flociAz = new ContainerBuilder("floci/floci-az:latest")
        .WithPortBinding(FlociAzPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci/health").ForPort(FlociAzPort)))
        .Build();

    private KeyVaultKeysClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociAz.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new KeyVaultKeysClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociAz.DisposeAsync();

    private string Endpoint => $"http://{this.flociAz.Hostname}:{this.flociAz.GetMappedPublicPort(FlociAzPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new KeyVaultKeysDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        KeyVaultKeysDemo demo = new(new KeyVaultKeysClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new KeyVaultKeysDemo(this.factory));

        Assert.Collection(
            steps,
            s => Assert.Equal("ListKeys — before", s.Title),
            s => Assert.Equal("CreateKey", s.Title),
            s => Assert.Equal("Encrypt", s.Title),
            s => Assert.Equal("Decrypt", s.Title),
            s => Assert.Equal("DeleteKey — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "Decrypt").Response);
    }

    /// <summary>
    /// Unique per-run key names plus delete-and-purge cleanup make re-runs idempotent; a second run
    /// against the same container is how that is proved rather than asserted. Purge matters: a
    /// soft-deleted key keeps its name reserved, so a cleanup that stopped at delete would leave the
    /// deleted-keys list growing by one per run.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Runs_Twice_And_Leaves_No_Key_Behind()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        KeyClient client = this.factory.Create();
        List<string> before = await ListKeyNamesAsync(client.GetPropertiesOfKeysAsync(ct));
        List<string> deletedBefore = await ListDeletedKeyNamesAsync(client, ct);

        foreach (int run in Enumerable.Range(0, 2))
        {
            List<DemoStep> steps = await RunAsync(new KeyVaultKeysDemo(this.factory));

            Assert.All(steps, s => Assert.True(s.Succeeded, $"run {run}, {s.Title}: {s.Error}"));
        }

        Assert.Equal(before.Order(), (await ListKeyNamesAsync(client.GetPropertiesOfKeysAsync(ct))).Order());
        Assert.Equal(deletedBefore.Order(), (await ListDeletedKeyNamesAsync(client, ct)).Order());
    }

    /// <summary>The capability the key-management comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task KeyManagement_Capability_RoundTrips()
    {
        KeyVaultKeyManagement keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        string keyId = await keyManagement.CreateKeyAsync(name, ct);

        try
        {
            // Key Vault's create returns a versioned id and its list returns unversioned ones, so
            // the listing is matched by name — the same rule the comparison page uses.
            Assert.Contains(name, (await keyManagement.ListKeysAsync(ct)).Select(k => k.Name));

            byte[] plaintext = "capability round-trip"u8.ToArray();
            byte[] ciphertext = await keyManagement.EncryptAsync(keyId, plaintext, ct);

            Assert.NotEqual(plaintext, ciphertext);
            Assert.Equal(plaintext, await keyManagement.DecryptAsync(keyId, ciphertext, ct));
        }
        finally
        {
            await keyManagement.DeleteKeyAsync(keyId, CancellationToken.None);
        }

        await this.AssertKeyGoneAsync(name);
    }

    /// <summary>
    /// The protection added while 0.13.0's create landed server-side and then failed to parse: a
    /// create that fails after it may have landed is undone by name, so the comparison page cannot
    /// leak a key. Still worth pinning on a working emulator — a name that is already taken fails the
    /// create with an answered 409, and that must not purge the key the earlier call made.
    /// </summary>
    [Fact]
    public async Task Capability_CreateKey_Conflict_Leaves_The_Existing_Key_Alone()
    {
        KeyVaultKeyManagement keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-dup-{Guid.NewGuid():N}";

        string keyId = await keyManagement.CreateKeyAsync(name, ct);

        try
        {
            KeyClient client = this.factory.Create();
            await client.StartDeleteKeyAsync(name, ct);

            // A soft-deleted name is reserved until purged, so a second create answers 409.
            await Assert.ThrowsAnyAsync<Exception>(async () => await keyManagement.CreateKeyAsync(name, ct));

            DeletedKey stillThere = await client.GetDeletedKeyAsync(name, ct);
            Assert.Equal(name, stillThere.Name);
        }
        finally
        {
            KeyClient client = this.factory.Create();
            await client.PurgeDeletedKeyAsync(name, CancellationToken.None);
        }

        await this.AssertKeyGoneAsync(name);
    }

    // Neither live nor soft-deleted: GetKey and GetDeletedKey both answer 404.
    private async Task AssertKeyGoneAsync(string name)
    {
        KeyClient client = this.factory.Create();
        CancellationToken ct = TestContext.Current.CancellationToken;

        RequestFailedException live = await Assert.ThrowsAsync<RequestFailedException>(async () => await client.GetKeyAsync(name, cancellationToken: ct));
        Assert.Equal(404, live.Status);

        RequestFailedException deleted = await Assert.ThrowsAsync<RequestFailedException>(async () => await client.GetDeletedKeyAsync(name, ct));
        Assert.Equal(404, deleted.Status);
    }

    private static async Task<List<DemoStep>> RunAsync(KeyVaultKeysDemo demo)
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in demo.RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        return steps;
    }

    private static async Task<List<string>> ListKeyNamesAsync(AsyncPageable<KeyProperties> keys)
    {
        List<string> names = [];

        await foreach (KeyProperties key in keys)
        {
            names.Add(key.Name);
        }

        return names;
    }

    private static async Task<List<string>> ListDeletedKeyNamesAsync(KeyClient client, CancellationToken ct)
    {
        List<string> names = [];

        await foreach (DeletedKey key in client.GetDeletedKeysAsync(ct))
        {
            names.Add(key.Name);
        }

        return names;
    }

    private static AzureEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Azure = new AzureEmulatorOptions { Endpoint = endpoint } }));
}
