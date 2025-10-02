using Microsoft.Extensions.FileProviders;
using MudBlazor.Services;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Web.Components;

namespace SPTarkov.Server.Web;

public static class SPTWeb
{
    internal static IEnumerable<SptMod> SptWebMods = [];

    public static void InitializeSptBlazor(this WebApplicationBuilder builder, IReadOnlyList<SptMod> sptMods)
    {
        SptWebMods = sptMods.Where(mod => mod.ModMetadata is IModWebMetadata).ToList();

        builder.WebHost.UseStaticWebAssets();
        builder.Services.AddMudServices();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var mvcBuilder = builder.Services.AddControllers();

        foreach (var assembly in SptWebMods.SelectMany(mod => mod.Assemblies))
        {
            mvcBuilder.AddApplicationPart(assembly);
        }
    }

    public static void UseSptBlazor(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILogger<App>>();

        app.UseAntiforgery();
        app.UseStaticFiles();

        var razorBuilder = app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        foreach (var mod in SptWebMods)
        {
            foreach (var assembly in mod.Assemblies)
            {
                razorBuilder.AddAdditionalAssemblies(assembly);
            }

            var modAssembly = mod.ModMetadata.GetType().Assembly;

            var location = Path.GetDirectoryName(modAssembly.Location);

            if (!string.IsNullOrEmpty(location) && Directory.Exists(Path.Combine(location, "wwwroot")))
            {
                var modAssemblyName = modAssembly.GetName().Name;

                logger.LogDebug(
                    "Mod {modName} has a wwwroot, mapping to /{modAssemblyName}/",
                    mod.ModMetadata.Name,
                    modAssembly.GetName().Name
                );

                app.UseStaticFiles(
                    new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(Path.Combine(location, "wwwroot")),
                        RequestPath = $"/{modAssembly.GetName().Name}",
                    }
                );
            }
        }

        app.MapControllers();
    }
}
