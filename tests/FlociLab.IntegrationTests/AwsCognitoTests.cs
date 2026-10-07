using System.Net;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using FlociLab.Aws.Cognito;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Testcontainers.Floci;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator the
/// AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class AwsCognitoTests : IAsyncLifetime
{
    // Same reasoning as AwsKmsTests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private CognitoClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new CognitoClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new CognitoDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    /// <summary>
    /// Every step green, the token resolving to the user, the wrong password refused, and — because
    /// the page can be pointed at real AWS — no token or password anywhere in what it renders.
    /// </summary>
    [Fact]
    public async Task RunAsync_Every_Step_Succeeds_And_Never_Renders_A_Secret()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new CognitoDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateUserPool", s.Title),
            s => Assert.Equal("CreateUserPoolClient", s.Title),
            s => Assert.Equal("AdminCreateUser", s.Title),
            s => Assert.Equal("AdminSetUserPassword", s.Title),
            s => Assert.Equal("InitiateAuth", s.Title),
            s => Assert.Equal("GetUser", s.Title),
            s => Assert.Equal("InitiateAuth with a wrong password", s.Title),
            s => Assert.Equal("SignUp", s.Title),
            s => Assert.Equal("AdminConfirmSignUp", s.Title),
            s => Assert.Equal("CreateGroup and AdminAddUserToGroup", s.Title),
            s => Assert.Equal("AdminListGroupsForUser", s.Title),
            s => Assert.Equal("ListUsers", s.Title),
            s => Assert.Equal("DeleteUserPool", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Username: alice", steps.Single(s => s.Title == "GetUser").Response);
        Assert.Contains("NotAuthorizedException", steps.Single(s => s.Title == "InitiateAuth with a wrong password").Response);

        // A JWT always starts "eyJ" (base64 of `{"`), and the generated password always starts
        // "Fl0ci!" — either appearing means a secret escaped the "(withheld)" markers.
        Assert.All(steps, s => Assert.DoesNotMatch("eyJ|Fl0ci!", $"{s.Request}\n{s.Response}"));
    }

    /// <summary>
    /// A second run has to behave like the first — no "already exists" — and the first run's pool
    /// must be gone, or every page click would leak one.
    /// </summary>
    [Fact]
    public async Task RunAsync_Is_Idempotent_And_Leaves_No_Pool_Behind()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        for (int i = 0; i < 2; i++)
        {
            List<DemoStep> steps = [];

            await foreach (DemoStep step in new CognitoDemo(this.factory).RunAsync(ct))
            {
                steps.Add(step);
            }

            Assert.Equal(13, steps.Count);
            Assert.All(steps, s => Assert.True(s.Succeeded, $"run {i}, {s.Title}: {s.Error}"));
        }

        using IAmazonCognitoIdentityProvider client = this.factory.Create();
        ListUserPoolsResponse pools = await client.ListUserPoolsAsync(new ListUserPoolsRequest { MaxResults = 60 }, ct);

        Assert.DoesNotContain(pools.UserPools, p => p.Name.StartsWith("flocilab-cognito-", StringComparison.Ordinal));
    }

    /// <summary>
    /// floci 2.2.0 enforces a client's <c>ExplicitAuthFlows</c>: <c>USER_PASSWORD_AUTH</c> on a
    /// client that never enabled it is refused with <c>InvalidParameterException</c>, as AWS does.
    /// 2.1.0 issued tokens anyway (docs/BLAZOR-PLAN.md §14). The demo already sets
    /// <c>ALLOW_USER_PASSWORD_AUTH</c>, so it is unaffected.
    /// </summary>
    [Fact]
    public async Task Client_Without_Explicit_Auth_Flows_Refuses_User_Password_Auth()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonCognitoIdentityProvider client = this.factory.Create();

        string poolId = (await client.CreateUserPoolAsync(new CreateUserPoolRequest { PoolName = $"flocilab-cognito-flows-{Guid.NewGuid():N}" }, ct)).UserPool.Id;

        try
        {
            // No ExplicitAuthFlows at all — on AWS this client cannot use USER_PASSWORD_AUTH.
            string clientId = (await client.CreateUserPoolClientAsync(new CreateUserPoolClientRequest { UserPoolId = poolId, ClientName = "no-flows" }, ct)).UserPoolClient.ClientId;

            await client.AdminCreateUserAsync(new AdminCreateUserRequest { UserPoolId = poolId, Username = "alice", MessageAction = MessageActionType.SUPPRESS }, ct);

            InvalidParameterException ex = await Assert.ThrowsAsync<InvalidParameterException>(
                () => client.InitiateAuthAsync(
                    new InitiateAuthRequest
                    {
                        ClientId = clientId,
                        AuthFlow = AuthFlowType.USER_PASSWORD_AUTH,
                        AuthParameters = new Dictionary<string, string> { ["USERNAME"] = "alice", ["PASSWORD"] = "abc" },
                    },
                    ct));

            Assert.Contains("USER_PASSWORD_AUTH flow not enabled", ex.Message);
        }
        finally
        {
            await client.DeleteUserPoolAsync(new DeleteUserPoolRequest { UserPoolId = poolId }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tripwire (docs/BLAZOR-PLAN.md §14): real Cognito answers <c>InvalidPasswordException</c> to a
    /// three-character password under the default policy. floci 2.1.0 accepted it, and 2.2.0 still
    /// does — observed 2026-10-06 against a fresh container, despite its release notes mentioning
    /// password-policy validation. When it fails, flip it to assert the rejection.
    /// </summary>
    [Fact]
    public async Task Floci_Ignores_The_Password_Policy()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonCognitoIdentityProvider client = this.factory.Create();

        string poolId = (await client.CreateUserPoolAsync(new CreateUserPoolRequest { PoolName = $"flocilab-cognito-lax-{Guid.NewGuid():N}" }, ct)).UserPool.Id;

        try
        {
            await client.AdminCreateUserAsync(new AdminCreateUserRequest { UserPoolId = poolId, Username = "alice", MessageAction = MessageActionType.SUPPRESS }, ct);

            AdminSetUserPasswordResponse response = await client.AdminSetUserPasswordAsync(
                new AdminSetUserPasswordRequest { UserPoolId = poolId, Username = "alice", Password = "abc", Permanent = true }, ct);

            Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
        }
        finally
        {
            await client.DeleteUserPoolAsync(new DeleteUserPoolRequest { UserPoolId = poolId }, CancellationToken.None);
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
        CognitoDemo demo = new(this.factory);
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
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        CognitoDemo demo = new(new CognitoClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
