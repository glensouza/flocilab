using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.AppSync;
using Amazon.AppSync.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.AppSync;

/// <summary>
/// AWS AppSync against floci. Ordinary AWSSDK.AppSync code for every management-plane call; the
/// query steps go over plain HTTP, which is how a production client reaches a GraphQL API too —
/// the data plane is never an SDK call. The only emulator-aware lines are in
/// <see cref="AppSyncClientFactory"/>.
/// </summary>
public sealed class AppSyncDemo(AppSyncClientFactory factory, IHttpClientFactory httpClientFactory) : IServiceDemo
{
    private const string Schema = "schema { query: Query }\ntype Query { echo(text: String!): String }";

    // 2018-05-29 is the template version floci's VTL engine documents; 2017-02-28 is the older one.
    private const string RequestTemplate = "{\"version\":\"2018-05-29\",\"payload\":$util.toJson($ctx.args.text)}";

    private const string ResponseTemplate = "$util.toJson($ctx.result)";

    private const string Query = "{ echo(text: \"hello from flocilab\") }";

    // StartSchemaCreation is asynchronous on real AppSync — the schema is PROCESSING until it
    // validates — so a resolver attached straight after can be refused. floci answers on the first
    // poll, so a green suite does not rule the wait out (the same shape as §14's read-back polls).
    internal static readonly TimeSpan SchemaPollBudget = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan SchemaPollDelay = TimeSpan.FromSeconds(1);

    public string Provider => CloudProvider.Aws;

    public string Slug => "appsync";

    public string DisplayName => "AppSync";

    public string Category => "API";

    public string Route => "/aws/appsync";

    /// <summary>ListGraphqlApis — one request, no state, and the cheapest call the service has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonAppSync client = factory.Create();
            ListGraphqlApisResponse response = await client.ListGraphqlApisAsync(new ListGraphqlApisRequest(), ct).ConfigureAwait(false);

            // AWSSDK v4 leaves an absent response collection null rather than empty (§14).
            int count = response.GraphqlApis?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListGraphqlApis returned {count} API(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonAppSync client = factory.Create();

        // Unique per run, so two runs never collide and a leftover API from a crashed run never
        // makes the next one fail.
        string apiName = $"flocilab-appsync-{Guid.NewGuid():N}";
        bool created = false;
        string? apiId = null;
        string? graphqlUri = null;
        string? apiKey = null;

        DemoStep? cleanup;

        try
        {
            // A do/while(false) so an early exit is a `break` and one real fault renders as one red
            // step, not six. A plain `break` (not `yield break`) so the finally still runs and the
            // cleanup step is still yielded wherever the sequence stops.
            do
            {
                yield return await RunStepAsync(
                    "CreateGraphqlApi",
                    $"POST {factory.ServiceUrl}/v1/apis\nappsync.CreateGraphqlApiAsync(new CreateGraphqlApiRequest {{ Name = \"{apiName}\", AuthenticationType = API_KEY }})",
                    async () =>
                    {
                        // Set before the call, not after: if the request lands but the response is
                        // lost, the API exists and cleanup has to know. Cleanup treats an absent
                        // API as a no-op, so claiming it early is free.
                        created = true;
                        CreateGraphqlApiResponse response = await client.CreateGraphqlApiAsync(
                            new CreateGraphqlApiRequest { Name = apiName, AuthenticationType = AuthenticationType.API_KEY }, ct).ConfigureAwait(false);
                        apiId = response.GraphqlApi.ApiId;
                        graphqlUri = response.GraphqlApi.Uris?.GetValueOrDefault("GRAPHQL");

                        return $"HTTP {(int)response.HttpStatusCode} — apiId: {apiId}, arn: {response.GraphqlApi.Arn}";
                    }).ConfigureAwait(false);

                if (apiId is null)
                {
                    break;
                }

                yield return await RunStepAsync(
                    "CreateApiKey",
                    $"POST {factory.ServiceUrl}/v1/apis/{apiId}/apikeys\nappsync.CreateApiKeyAsync(new CreateApiKeyRequest {{ ApiId = \"{apiId}\" }})",
                    async () =>
                    {
                        CreateApiKeyResponse response = await client.CreateApiKeyAsync(new CreateApiKeyRequest { ApiId = apiId }, ct).ConfigureAwait(false);
                        apiKey = response.ApiKey.Id;

                        // ApiKey.Id is the key value itself, as on AWS — there is no separate secret.
                        return $"HTTP {(int)response.HttpStatusCode} — key: {apiKey}, expires: {DateTimeOffset.FromUnixTimeSeconds(response.ApiKey.Expires ?? 0):u}";
                    }).ConfigureAwait(false);

                if (apiKey is null)
                {
                    break;
                }

                bool schemaLoaded = false;

                yield return await RunStepAsync(
                    "StartSchemaCreation",
                    $"POST {factory.ServiceUrl}/v1/apis/{apiId}/schemacreation\nappsync.StartSchemaCreationAsync(...) then GetSchemaCreationStatusAsync(...) until SUCCESS\n{Schema}",
                    async () =>
                    {
                        using MemoryStream definition = new(Encoding.UTF8.GetBytes(Schema));
                        StartSchemaCreationResponse started = await client.StartSchemaCreationAsync(
                            new StartSchemaCreationRequest { ApiId = apiId, Definition = definition }, ct).ConfigureAwait(false);

                        long deadline = Stopwatch.GetTimestamp() + (long)(SchemaPollBudget.TotalSeconds * Stopwatch.Frequency);
                        GetSchemaCreationStatusResponse status = await client.GetSchemaCreationStatusAsync(
                            new GetSchemaCreationStatusRequest { ApiId = apiId }, ct).ConfigureAwait(false);

                        while (status.Status == SchemaStatus.PROCESSING)
                        {
                            if (Stopwatch.GetTimestamp() >= deadline)
                            {
                                // floci 2.2.0 loads the schema in a GraphQL sidecar it starts through the Docker socket;
                                // with no socket it stays PROCESSING for 30-50 s while floci retries Docker, then
                                // goes FAILED — longer than this budget, so it reads as a hang here.
                                throw new InvalidOperationException($"Schema still PROCESSING after {SchemaPollBudget.TotalSeconds:0} s." + (factory.UseEmulator ? " floci starts its GraphQL engine as a sidecar container through the Docker socket — check floci has the socket and can pull floci/floci-sidecar-graphql." : string.Empty));
                            }

                            await Task.Delay(SchemaPollDelay, ct).ConfigureAwait(false);
                            status = await client.GetSchemaCreationStatusAsync(
                                new GetSchemaCreationStatusRequest { ApiId = apiId }, ct).ConfigureAwait(false);
                        }

                        // FAILED carries the reason in Details; anything but a loaded schema stops
                        // the run rather than letting the query step blame the wrong thing.
                        if (status.Status != SchemaStatus.SUCCESS && status.Status != SchemaStatus.ACTIVE)
                        {
                            throw new InvalidOperationException($"Schema ended {status.Status}: {status.Details}");
                        }

                        schemaLoaded = true;

                        return $"HTTP {(int)started.HttpStatusCode} — status: {started.Status}, then {status.Status}";
                    }).ConfigureAwait(false);

                if (!schemaLoaded)
                {
                    break;
                }

                bool sourced = false;

                yield return await RunStepAsync(
                    "CreateDataSource (NONE)",
                    $"POST {factory.ServiceUrl}/v1/apis/{apiId}/datasources\nappsync.CreateDataSourceAsync(new CreateDataSourceRequest {{ Name = \"local\", Type = NONE }})",
                    async () =>
                    {
                        CreateDataSourceResponse response = await client.CreateDataSourceAsync(
                            new CreateDataSourceRequest { ApiId = apiId, Name = "local", Type = DataSourceType.NONE }, ct).ConfigureAwait(false);
                        sourced = true;

                        return $"HTTP {(int)response.HttpStatusCode} — {response.DataSource.DataSourceArn}";
                    }).ConfigureAwait(false);

                if (!sourced)
                {
                    break;
                }

                bool resolved = false;

                yield return await RunStepAsync(
                    "CreateResolver (Query.echo)",
                    $"POST {factory.ServiceUrl}/v1/apis/{apiId}/types/Query/resolvers\nappsync.CreateResolverAsync(new CreateResolverRequest {{ TypeName = \"Query\", FieldName = \"echo\", DataSourceName = \"local\", RequestMappingTemplate = ..., ResponseMappingTemplate = ... }})\n{RequestTemplate}",
                    async () =>
                    {
                        CreateResolverResponse response = await client.CreateResolverAsync(
                            new CreateResolverRequest
                            {
                                ApiId = apiId,
                                TypeName = "Query",
                                FieldName = "echo",
                                DataSourceName = "local",
                                RequestMappingTemplate = RequestTemplate,
                                ResponseMappingTemplate = ResponseTemplate,
                            }, ct).ConfigureAwait(false);
                        resolved = true;

                        return $"HTTP {(int)response.HttpStatusCode} — {response.Resolver.ResolverArn}";
                    }).ConfigureAwait(false);

                if (!resolved)
                {
                    break;
                }

                // The query steps are plain HTTP, not SDK calls — production clients POST GraphQL
                // to the endpoint the API reports, never through AWSSDK.AppSync. This is what
                // proves the API answers, not just that it was configured.
                string url = factory.GraphqlUrl(apiId, graphqlUri);
                string body = JsonSerializer.Serialize(new { query = Query });

                yield return await RunStepAsync(
                    "Query with the API key",
                    $"POST {url}\nx-api-key: {apiKey}\n{body}",
                    async () =>
                    {
                        using HttpClient http = httpClientFactory.CreateClient();
                        using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                        request.Headers.Add("x-api-key", apiKey);
                        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
                        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                        if (!response.IsSuccessStatusCode)
                        {
                            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} — {text}");
                        }

                        return $"HTTP {(int)response.StatusCode} — {text}\n{Interpret(text)}";
                    }).ConfigureAwait(false);

                // The negative half: the same query without a key must be refused. It shows the
                // API key is what gates access, and that the step above was not simply open.
                yield return await RunStepAsync(
                    "Query without a key (expect 401)",
                    $"POST {url}\n{body}",
                    async () =>
                    {
                        using HttpClient http = httpClientFactory.CreateClient();
                        using HttpResponseMessage response = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
                        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                        if (response.StatusCode != HttpStatusCode.Unauthorized)
                        {
                            throw new InvalidOperationException($"Expected HTTP 401 but got {(int)response.StatusCode} — {text}");
                        }

                        return $"HTTP {(int)response.StatusCode} — {text}";
                    }).ConfigureAwait(false);
            }
            while (false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts clean. Yielded below — an iterator may not yield from a finally.
            cleanup = created ? await DeleteApiAsync(factory.ServiceUrl, client, apiId, apiName).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// The SDK reports both of the interesting failures inside an <see cref="AmazonServiceException"/>,
    /// so <see cref="ProbeResult.FromException"/> — which inspects only the outermost exception —
    /// cannot classify them alone. A 501 arrives as a status code on the exception; a refused
    /// connection arrives with no status code and a transport exception underneath.
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

                // A status code means the emulator answered: behaving badly, not absent.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Says what the query response proves. floci 2.2.0 executes resolvers in a GraphQL sidecar;
    /// 2.1.0 left every resolver field null. A null is still reported as that gap rather than as
    /// success, because a sidecar that is up but not executing would look identical, and a value
    /// is reported as the round trip it is.
    /// GraphQL reports validation and resolver faults inside an HTTP 200, so a non-empty
    /// <c>errors</c> array throws here — otherwise a template real AppSync rejects would read as Ok.
    /// </summary>
    internal static string Interpret(string responseBody)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The response body is not JSON.", ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The response body is not a GraphQL response object.");
            }

            if (root.TryGetProperty("errors", out JsonElement errors) && errors is { ValueKind: JsonValueKind.Array } && errors.GetArrayLength() != 0)
            {
                throw new InvalidOperationException($"The query returned GraphQL errors: {errors.GetRawText()}");
            }

            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("echo", out JsonElement echo))
            {
                throw new InvalidOperationException("The response carried no data.echo field.");
            }

            return echo.ValueKind == JsonValueKind.String
                ? "The resolver executed: data.echo is the argument echoed back through the mapping templates."
                : "The query executed against the schema, but the resolver returned null — floci does not execute resolvers in this build.";
        }
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text.
    /// </summary>
    internal static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed; RunAsync's finally still
        // removes the API.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    internal static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Deletes by the id captured from CreateGraphqlApi when there is one, and otherwise finds the
    /// API by this run's unique name — a request that landed with a lost response still left an API
    /// behind. Uses <see cref="CancellationToken.None"/>: a cancelled run still has an API to
    /// remove. Deleting the API takes its key, schema, data source and resolver with it.
    /// </summary>
    internal static async Task<DemoStep> DeleteApiAsync(string serviceUrl, IAmazonAppSync client, string? apiId, string apiName)
    {
        string request = apiId is not null
            ? $"DELETE {serviceUrl}/v1/apis/{apiId}\nappsync.DeleteGraphqlApiAsync(new DeleteGraphqlApiRequest {{ ApiId = \"{apiId}\" }})"
            : $"GET {serviceUrl}/v1/apis\nappsync.ListGraphqlApisAsync() — find \"{apiName}\"\nDELETE {serviceUrl}/v1/apis/{{id}}\nappsync.DeleteGraphqlApiAsync(...)";

        return await RunStepAsync("DeleteGraphqlApi — cleanup", request, async () =>
        {
            string? id = apiId ?? await FindApiIdAsync(client, apiName).ConfigureAwait(false);

            if (id is null)
            {
                return $"Nothing to remove — no API named \"{apiName}\" exists.";
            }

            DeleteGraphqlApiResponse response = await client.DeleteGraphqlApiAsync(
                new DeleteGraphqlApiRequest { ApiId = id }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — removed the API";
        }).ConfigureAwait(false);
    }

    private static async Task<string?> FindApiIdAsync(IAmazonAppSync client, string apiName)
    {
        string? token = null;

        do
        {
            ListGraphqlApisResponse page = await client.ListGraphqlApisAsync(
                new ListGraphqlApisRequest { NextToken = token, MaxResults = 25 }, CancellationToken.None).ConfigureAwait(false);

            // GraphqlApis is null rather than empty on an account with none (AWSSDK v4, §14).
            GraphqlApi? match = page.GraphqlApis?.FirstOrDefault(api => api.Name == apiName);

            if (match is not null)
            {
                return match.ApiId;
            }

            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return null;
    }
}
