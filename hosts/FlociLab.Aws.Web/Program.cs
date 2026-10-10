using System.Reflection;
using FlociLab.Aws.AccessAnalyzer;
using FlociLab.Aws.Acm;
using FlociLab.Aws.Account;
using FlociLab.Aws.ApiGatewayRest;
using FlociLab.Aws.ApiGatewayV2;
using FlociLab.Aws.AppConfig;
using FlociLab.Aws.AppConfigData;
using FlociLab.Aws.AppSync;
using FlociLab.Aws.ApplicationAutoScaling;
using FlociLab.Aws.AutoScaling;
using FlociLab.Aws.CloudControlApi;
using FlociLab.Aws.CloudFormation;
using FlociLab.Aws.CloudFront;
using FlociLab.Aws.CloudMap;
using FlociLab.Aws.CloudWatchLogs;
using FlociLab.Aws.CloudWatchMetrics;
using FlociLab.Aws.CodeGuruReviewer;
using FlociLab.Aws.Cognito;
using FlociLab.Aws.DynamoDb;
using FlociLab.Aws.ElasticBeanstalk;
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
using FlociLab.Aws.S3Tables;
using FlociLab.Aws.SecretsManager;
using FlociLab.Aws.Sns;
using FlociLab.Aws.Sqs;
using FlociLab.Aws.Ssm;
using FlociLab.Aws.Sts;
using FlociLab.Aws.StepFunctions;
using FlociLab.Aws.Swf;
using FlociLab.Aws.VerifiedPermissions;
using FlociLab.Core;
using FlociLab.Core.Coverage;
using FlociLab.Shell;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Options binding, the four endpoint factories, the demo catalog and the coverage matrix, then
// one .Add<Service>Demo() per AWS sample this host carries — its page, route and nav entry all
// come with it.
builder.Services
    .AddFlociCore(builder.Configuration)
    .AddFlociShell(typeof(Program).Assembly, "FlociLab AWS", "AWS emulator samples")
    .AddAwsS3Demo()
    .AddAwsS3TablesDemo()
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
    .AddAwsCloudControlApiDemo()
    .AddAwsAppConfigDemo()
    .AddAwsAppConfigDataDemo()
    .AddAwsCloudFormationDemo()
    .AddAwsCloudFrontDemo()
    .AddAwsCloudMapDemo()
    .AddAwsElbV2Demo()
    .AddAwsElbClassicDemo()
    .AddAwsGlobalAcceleratorDemo()
    .AddAwsCodeGuruReviewerDemo()
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
    .AddAwsAutoScalingDemo()
    .AddAwsApplicationAutoScalingDemo()
    .AddAwsElasticBeanstalkDemo();

WebApplication app = builder.Build();

// Which assemblies own routable pages is a question only the registration above can answer, so
// ask the catalog rather than repeating it by hand. Routes.razor asks it again for the Router, and
// both get the same answer (docs/BLAZOR-PLAN.md §6).
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
