using System.Reflection;
using FlociLab.All.Web.Components;
using FlociLab.Aws.AccessAnalyzer;
using FlociLab.Aws.Acm;
using FlociLab.Aws.Account;
using FlociLab.Aws.ApiGatewayRest;
using FlociLab.Aws.ApiGatewayV2;
using FlociLab.Aws.AppSync;
using FlociLab.Aws.CloudFront;
using FlociLab.Aws.CloudMap;
using FlociLab.Aws.CloudWatchLogs;
using FlociLab.Aws.CloudWatchMetrics;
using FlociLab.Aws.Cognito;
using FlociLab.Aws.DynamoDb;
using FlociLab.Aws.ElbClassic;
using FlociLab.Aws.ElbV2;
using FlociLab.Aws.EventBridge;
using FlociLab.Aws.EventBridgePipes;
using FlociLab.Aws.EventBridgeScheduler;
using FlociLab.Aws.GlobalAccelerator;
using FlociLab.Aws.Iam;
using FlociLab.Aws.IdentityCenter;
using FlociLab.Aws.Kms;
using FlociLab.Aws.Lightsail;
using FlociLab.Aws.Organizations;
using FlociLab.Aws.Ram;
using FlociLab.Aws.Route53;
using FlociLab.Aws.Route53Resolver;
using FlociLab.Aws.S3;
using FlociLab.Aws.SecretsManager;
using FlociLab.Aws.Sns;
using FlociLab.Aws.Sqs;
using FlociLab.Aws.Ssm;
using FlociLab.Aws.Sts;
using FlociLab.Aws.StepFunctions;
using FlociLab.Aws.Swf;
using FlociLab.Aws.VerifiedPermissions;
using FlociLab.Azure;
using FlociLab.Azure.Blob;
using FlociLab.Azure.CosmosDb;
using FlociLab.Azure.KeyVaultKeys;
using FlociLab.Azure.KeyVaultSecrets;
using FlociLab.Azure.Queue;
using FlociLab.Azure.ServiceBus;
using FlociLab.Comparison;
using FlociLab.Core;
using FlociLab.Core.Coverage;
using FlociLab.Gcp.Firestore;
using FlociLab.Gcp.Kms;
using FlociLab.Gcp.PubSub;
using FlociLab.Gcp.SecretManager;
using FlociLab.Gcp.Storage;
using FlociLab.Oci.ObjectStorage;
using FlociLab.Oci.Queue;
using FlociLab.Oci.Secrets;
using FlociLab.Oci.Vault;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Options binding, the four endpoint factories, the demo catalog and the coverage matrix, then
// one .Add<Service>Demo() per sample RCL — each brings its own page, route and nav entry with it.
// AddComparisonPages() last, and it is the odd one out: it registers no demo, only the fact that
// FlociLab.Comparison owns routable pages, which nothing else could tell the catalog.
builder.Services
    .AddFlociCore(builder.Configuration)
    .AddAwsS3Demo()
    .AddAwsSqsDemo()
    .AddAwsDynamoDbDemo()
    .AddAwsEventBridgeDemo()
    .AddAwsEventBridgePipesDemo()
    .AddAwsEventBridgeSchedulerDemo()
    .AddAwsIamDemo()
    .AddAwsKmsDemo()
    .AddAwsSecretsManagerDemo()
    .AddAwsSnsDemo()
    .AddAwsSsmDemo()
    .AddAwsStepFunctionsDemo()
    .AddAwsSwfDemo()
    .AddAwsCloudWatchLogsDemo()
    .AddAwsCloudWatchMetricsDemo()
    .AddAwsApiGatewayRestDemo()
    .AddAwsApiGatewayV2Demo()
    .AddAwsAppSyncDemo()
    .AddAwsRoute53Demo()
    .AddAwsRoute53ResolverDemo()
    .AddAwsCloudFrontDemo()
    .AddAwsCloudMapDemo()
    .AddAwsElbV2Demo()
    .AddAwsElbClassicDemo()
    .AddAwsGlobalAcceleratorDemo()
    .AddAwsCognitoDemo()
    .AddAwsStsDemo()
    .AddAwsIdentityCenterDemo()
    .AddAwsAccessAnalyzerDemo()
    .AddAwsAccountDemo()
    .AddAwsOrganizationsDemo()
    .AddAwsRamDemo()
    .AddAwsVerifiedPermissionsDemo()
    .AddAwsAcmDemo()
    .AddAwsLightsailDemo()
    .AddAzureBlobDemo()
    .AddAzureQueueDemo()
    .AddAzureServiceBusDemo()
    .AddAzureCosmosDbDemo()
    .AddAzureKeyVaultSecretsDemo()
    .AddAzureKeyVaultKeysDemo()
    .AddGcpStorageDemo()
    .AddGcpPubSubDemo()
    .AddGcpFirestoreDemo()
    .AddGcpSecretManagerDemo()
    .AddGcpKmsDemo()
    .AddOciObjectStorageDemo()
    .AddOciQueueDemo()
    .AddOciVaultDemo()
    .AddOciSecretsDemo()
    .AddComparisonPages()
    .AddFlociAzureCredentialWarmup();

WebApplication app = builder.Build();

// Which assemblies own routable pages is a question only the registrations above can answer, so
// ask the catalog rather than repeating the list. Routes.razor asks it again for the Router, and
// both get the same answer — including FlociLab.Comparison, which AddComparisonPages() declared
// because its pages consume capabilities rather than registering an IServiceDemo.
Assembly[] pageAssemblies;

using (IServiceScope scope = app.Services.CreateScope())
{
    pageAssemblies = [.. scope.ServiceProvider.GetRequiredService<IDemoCatalog>().PageAssemblies];
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddAdditionalAssemblies(pageAssemblies)
    .AddInteractiveServerRenderMode();

app.Run();
