using System.Reflection;
using FlociLab.Oci.ObjectStorage;
using FlociLab.Oci.Queue;
using FlociLab.Oci.Secrets;
using FlociLab.Oci.Vault;
using FlociLab.Core;
using FlociLab.Core.Coverage;
using FlociLab.Shell;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Options binding, the four endpoint factories, the demo catalog and the coverage matrix, then
// one .Add<Service>Demo() per OCI sample this host carries — each brings its own page, route and
// nav entry with it.
builder.Services
    .AddFlociCore(builder.Configuration)
    .AddFlociShell(typeof(Program).Assembly, "FlociLab OCI", "OCI emulator samples")
    .AddOciObjectStorageDemo()
    .AddOciQueueDemo()
    .AddOciVaultDemo()
    .AddOciSecretsDemo();

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
