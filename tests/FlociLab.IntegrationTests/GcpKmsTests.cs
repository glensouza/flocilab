using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using FlociLab.Gcp.Kms;
using Google.Api.Gax.ResourceNames;
using Google.Cloud.Kms.V1;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-gcp per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class GcpKmsTests : IAsyncLifetime
{
    private const int FlociGcpPort = 4588;

    // A plain ContainerBuilder rather than the FlociBuilder the S3/SQS tests use, for the same
    // reason GcpStorageTests, GcpPubSubTests, GcpFirestoreTests and GcpSecretManagerTests do:
    // Testcontainers.Floci 4.16.0 hardcodes 4566, and floci-gcp listens on 4588 with its health
    // path namespaced as /_floci-gcp/health.
    private readonly IContainer flociGcp = new ContainerBuilder("floci/floci-gcp:latest")
        .WithPortBinding(FlociGcpPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci-gcp/health").ForPort(FlociGcpPort)))
        .Build();

    private KmsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociGcp.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new KmsClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociGcp.DisposeAsync();

    // Hostname rather than "localhost" deliberately — see GcpStorageTests for why: Testcontainers
    // hands back an address, and on a Windows host "localhost" resolves to ::1 first while the
    // published port is IPv4-only, costing a ~2 s dead IPv6 attempt on every first connection.
    private string Endpoint => $"http://{this.flociGcp.Hostname}:{this.flociGcp.GetMappedPublicPort(FlociGcpPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new KmsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new KmsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListKeyRings — before", s.Title),
            s => Assert.Equal("CreateKeyRing", s.Title),
            s => Assert.Equal("CreateCryptoKey", s.Title),
            s => Assert.Equal("Encrypt", s.Title),
            s => Assert.Equal("Decrypt", s.Title),
            s => Assert.Equal("DestroyCryptoKeyVersion — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "Decrypt").Response);
    }

    /// <summary>
    /// Re-runnable because every run schedules its own version's destruction, which is what makes
    /// the page safe to hammer during a recording. Unlike the other Phase 2 samples, this does not
    /// assert "leaves nothing behind" — a Cloud KMS key ring and its crypto keys can never be
    /// deleted (see <see cref="KmsDemo"/>'s remarks), so a second run finds the key ring already
    /// there (reused, not recreated) and adds one more permanently-listed crypto key. The tripwire
    /// here is that both runs still complete and each destroys its own version.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Runs_Twice_And_Reuses_The_Key_Ring()
    {
        KmsDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        KeyManagementServiceClient client = this.factory.Create();
        int keyRingCount = 0;

        await foreach (KeyRing keyRing in client.ListKeyRingsAsync(new LocationName(this.factory.ProjectId, this.factory.LocationId)).WithCancellation(ct))
        {
            _ = keyRing;
            keyRingCount++;
        }

        // Exactly one — the second run's CreateKeyRing hit AlreadyExists and reused it.
        Assert.Equal(1, keyRingCount);
    }

    /// <summary>The capability the key-management comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task KeyManagement_Capability_RoundTrips()
    {
        KmsKeyManagement keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        string keyId = await keyManagement.CreateKeyAsync(name, ct);

        try
        {
            Assert.Contains(keyId, (await keyManagement.ListKeysAsync(ct)).Select(k => k.Id));

            byte[] plaintext = "capability round-trip"u8.ToArray();
            byte[] ciphertext = await keyManagement.EncryptAsync(keyId, plaintext, ct);

            Assert.NotEqual(plaintext, ciphertext);
            Assert.Equal(plaintext, await keyManagement.DecryptAsync(keyId, ciphertext, ct));
        }
        finally
        {
            await keyManagement.DeleteKeyAsync(keyId, CancellationToken.None);
        }
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        KmsDemo demo = new(this.factory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);
            }
        });

        Assert.DoesNotContain(steps, s => !s.Succeeded);
    }

    /// <summary>
    /// The same guarantee, but for a token that trips *while* a call is in flight rather than
    /// before the run starts — the case that actually happens when a user navigates away mid-run.
    /// Same reasoning as <c>GcpSecretManagerTests.Run_Cancelled_Mid_Flight_Still_Throws_Rather_Than_Failing_Steps</c>.
    /// </summary>
    [Fact]
    public async Task Run_Cancelled_Mid_Flight_Still_Throws_Rather_Than_Failing_Steps()
    {
        KmsDemo demo = new(this.factory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);

                // Cancel once the run is genuinely under way, so the next call is cancelled in
                // flight rather than refused at the gate.
                await cts.CancelAsync();
            }
        });

        Assert.NotEmpty(steps);
        Assert.DoesNotContain(steps, s => !s.Succeeded);
    }

    /// <summary>
    /// The probe honours the same translation, which is what <c>CoverageMatrix</c> depends on to
    /// render a ProbeTimeout as "No response within 5s" / <c>Unreachable</c> rather than as a red
    /// <c>Error</c> naming a gRPC status the reader has no use for.
    /// </summary>
    [Fact]
    public async Task Cancelled_Probe_Throws_Rather_Than_Returning_An_Error_Result()
    {
        KmsDemo demo = new(this.factory);

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => demo.ProbeAsync(cts.Token));
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        KmsDemo demo = new(new KmsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// The emulator behaviour <c>KmsDemo</c>'s cleanup step rests on, pinned so it fails loudly if
    /// floci-gcp ever stops accepting a repeat destroy. Unlike Secret Manager's delete — which
    /// answers NOT_FOUND on a second call — destroying an already-scheduled crypto key version
    /// answers 200 again, which is why the cleanup step checks the returned state rather than
    /// treating the call succeeding at all as the postcondition. See docs/BLAZOR-PLAN.md §14.
    /// </summary>
    [Fact]
    public async Task Destroying_An_Already_Scheduled_Version_Answers_Ok_Not_NotFound()
    {
        KeyManagementServiceClient client = this.factory.Create();
        LocationName location = new(this.factory.ProjectId, this.factory.LocationId);
        string keyRingId = $"flocilab-pin-{Guid.NewGuid():N}";
        KeyRingName keyRingName = new(this.factory.ProjectId, this.factory.LocationId, keyRingId);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await client.CreateKeyRingAsync(location, keyRingId, new KeyRing(), ct);
        CryptoKey cryptoKey = await client.CreateCryptoKeyAsync(
            keyRingName, "pin", new CryptoKey { Purpose = CryptoKey.Types.CryptoKeyPurpose.EncryptDecrypt }, ct);
        CryptoKeyVersionName versionName = CryptoKeyVersionName.Parse(cryptoKey.Primary.Name);

        await client.DestroyCryptoKeyVersionAsync(versionName, ct);
        CryptoKeyVersion second = await client.DestroyCryptoKeyVersionAsync(versionName, ct);

        Assert.Equal(CryptoKeyVersion.Types.CryptoKeyVersionState.DestroyScheduled, second.State);
    }

    /// <summary>
    /// The tripwire behind <c>KmsDemo</c>'s Encrypt postcondition, and the counterpart to
    /// <c>AwsKmsTests.Encrypt_Does_Not_Actually_Encrypt_On_Floci</c>: floci-gcp really does encrypt,
    /// so this pins that rather than pinning a limitation. Verified by curl against floci-gcp 0.7.0
    /// on 2026-09-02 — 32 bytes of binary ciphertext, no <c>kms:v2:</c> envelope and no base64
    /// plaintext anywhere inside it. The day floci-gcp adopts the wrapping floci's AWS KMS ships,
    /// this fails rather than the demo quietly rendering five green steps over recoverable data.
    /// </summary>
    [Fact]
    public async Task Encrypt_Really_Encrypts_Rather_Than_Wrapping_The_Plaintext()
    {
        KmsKeyManagement keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        string keyId = await keyManagement.CreateKeyAsync($"flocilab-crypto-{Guid.NewGuid():N}", ct);
        byte[] plaintext = "Hello from FlociLab."u8.ToArray();
        byte[] ciphertext = await keyManagement.EncryptAsync(keyId, plaintext, ct);

        Assert.NotEmpty(ciphertext);
        Assert.NotEqual(plaintext, ciphertext);

        // The check the byte-equality assertion above cannot make: an envelope that merely wraps
        // the base64 plaintext differs byte-for-byte and is still not encryption.
        string asText = Encoding.UTF8.GetString(ciphertext);
        Assert.DoesNotContain(Convert.ToBase64String(plaintext), asText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello from FlociLab.", asText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A delete that destroyed nothing must not report success (plan §14's corollary for capability
    /// code). Cloud KMS cannot delete a crypto key at all, so a second delete finds every version
    /// already destroy-scheduled and has nothing left to do — which is a failure, not a no-op.
    /// </summary>
    [Fact]
    public async Task Deleting_A_Key_Whose_Versions_Are_Already_Destroyed_Fails()
    {
        KmsKeyManagement keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        string keyId = await keyManagement.CreateKeyAsync($"flocilab-twice-{Guid.NewGuid():N}", ct);
        await keyManagement.DeleteKeyAsync(keyId, ct);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => keyManagement.DeleteKeyAsync(keyId, ct));

        Assert.Contains("nothing was deleted", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ProbeStatus.Error, keyManagement.Classify(ex));
    }

    /// <summary>
    /// A crypto key name is permanently taken, even after every version is destroyed — real Cloud
    /// KMS behaviour rather than a floci quirk, which is why the capability names it instead of
    /// letting a bare AlreadyExists read as the emulator misbehaving.
    /// </summary>
    [Fact]
    public async Task Creating_A_Key_Whose_Name_Is_Already_Taken_Explains_Why()
    {
        KmsKeyManagement keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-taken-{Guid.NewGuid():N}";

        string keyId = await keyManagement.CreateKeyAsync(name, ct);
        await keyManagement.DeleteKeyAsync(keyId, ct);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => keyManagement.CreateKeyAsync(name, ct));

        Assert.Contains("can never be deleted", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The emulator behaviour <c>KmsDemo.DestroyUnconfirmedKeyAsync</c> rests on: a CreateCryptoKey
    /// whose response is lost still created the key, so the cleanup path asks the server rather than
    /// assuming. This pins both answers GetCryptoKey can give — NOT_FOUND for a name that was never
    /// created, and a key carrying an enabled primary for one that was.
    /// </summary>
    [Fact]
    public async Task GetCryptoKey_Distinguishes_A_Key_That_Was_Created_From_One_That_Was_Not()
    {
        KeyManagementServiceClient client = this.factory.Create();
        CancellationToken ct = TestContext.Current.CancellationToken;
        LocationName location = new(this.factory.ProjectId, this.factory.LocationId);
        string keyRingId = $"flocilab-get-{Guid.NewGuid():N}";
        KeyRingName keyRingName = new(this.factory.ProjectId, this.factory.LocationId, keyRingId);

        await client.CreateKeyRingAsync(location, keyRingId, new KeyRing(), ct);

        RpcException missing = await Assert.ThrowsAsync<RpcException>(
            () => client.GetCryptoKeyAsync(new CryptoKeyName(this.factory.ProjectId, this.factory.LocationId, keyRingId, "never-created"), ct));

        Assert.Equal(StatusCode.NotFound, missing.StatusCode);

        await client.CreateCryptoKeyAsync(
            keyRingName, "created", new CryptoKey { Purpose = CryptoKey.Types.CryptoKeyPurpose.EncryptDecrypt }, ct);
        CryptoKey found = await client.GetCryptoKeyAsync(
            new CryptoKeyName(this.factory.ProjectId, this.factory.LocationId, keyRingId, "created"), ct);

        Assert.Equal(CryptoKeyVersion.Types.CryptoKeyVersionState.Enabled, found.Primary.State);
    }

    private static GcpEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Gcp = new GcpEmulatorOptions { Endpoint = endpoint } }));
}
