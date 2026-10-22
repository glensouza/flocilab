#:sdk Microsoft.NET.Sdk.Web
#:project FlociLab.Aws.StepFunctions.Demo/FlociLab.Aws.StepFunctions.Demo.csproj

// Runs this sample's own round-trip: dotnet run lab.cs
// from this directory. The defaults are the README's host ports. Floci__* environment
// variables override them, the same names the Blazor hosts bind.

using FlociLab.Aws.StepFunctions;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Coverage;
using Microsoft.Extensions.DependencyInjection;

ServiceCollection services = new ServiceCollection();
services.AddFlociCore(FlociEnvironment.Apply);
services.AddAwsStepFunctionsDemo();

// The Web SDK is only here so the sample RCL's shared framework resolves. This file is a
// one-shot console, so building the provider is the program rather than a second copy of a host.
#pragma warning disable ASP0000
await using ServiceProvider provider = services.BuildServiceProvider();
#pragma warning restore ASP0000
await using AsyncServiceScope scope = provider.CreateAsyncScope();
IDemoCatalog catalog = scope.ServiceProvider.GetRequiredService<IDemoCatalog>();

int failed = 0;

foreach (IServiceDemo demo in catalog.Demos)
{
    Console.WriteLine(demo.DisplayName);
    Console.WriteLine();

    await foreach (DemoStep step in demo.RunAsync(CancellationToken.None))
    {
        Console.WriteLine(step.Succeeded ? $"ok  {step.Title}" : $"failed  {step.Title}");

        if (step.Request is not null)
        {
            Console.WriteLine(step.Request);
        }

        if (step.Response is not null)
        {
            Console.WriteLine(step.Response);
        }

        if (step.Error is not null)
        {
            Console.WriteLine(step.Error);
            failed++;
        }

        Console.WriteLine();
    }
}

Environment.ExitCode = failed == 0 ? 0 : 1;
