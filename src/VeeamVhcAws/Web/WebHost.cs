using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using VeeamVhcAws.Web.Components;
using VeeamVhcAws.Web.Services;
using SerilogILogger = Serilog.ILogger;

namespace VeeamVhcAws.Web;

public record WebHostOptions(string ConfigPath, string BindAddress, int Port);

public static class WebHost
{
    private static readonly SerilogILogger Logger = Log.ForContext(typeof(WebHost));

    public static async Task RunAsync(WebHostOptions options, AuthOptions auth, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls($"http://{options.BindAddress}:{options.Port}");
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(auth);
        builder.Services.AddScoped<StateService>();
        builder.Services.AddScoped<ConfigService>();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var app = builder.Build();

        app.UseMiddleware<AuthMiddleware>();
        app.UseAntiforgery();
        app.UseStaticFiles();
        // StaticAssetDevelopmentRuntimeHandler (used by MapStaticAssets) hardcodes
        // webRoot+AssetFile for ALL assets, crashing on NuGet-sourced _framework/*
        // files. Workaround: read the runtime manifest content roots and register
        // a PhysicalFileProvider for _framework directly, then use UseStaticFiles.
        ServeBlazorFrameworkFiles(app);

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/state", (StateService s) => Results.Ok(s.Load()));
        app.MapGet("/api/config", (ConfigService c) => Results.Ok(c.Load()));
        app.MapPost("/api/alerts/{key}/suppress", (string key, StateService s) =>
            s.Suppress(key) ? Results.Ok() : Results.NotFound());
        app.MapDelete("/api/alerts/{key}/suppress", (string key, StateService s) =>
            s.Unsuppress(key) ? Results.Ok() : Results.NotFound());

        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        Logger.Information("Web UI listening on http://{Bind}:{Port}", options.BindAddress, options.Port);
        await app.RunAsync(cancellationToken);
    }

    // MapStaticAssets() development handler incorrectly resolves NuGet-sourced
    // _framework assets to wwwroot. Read the runtime manifest to find the real
    // content root (NuGet package directory) and serve from it directly.
    private static void ServeBlazorFrameworkFiles(WebApplication app)
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory,
            "veeam-vhc-aws.staticwebassets.runtime.json");
        if (!File.Exists(manifestPath)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var roots = doc.RootElement.GetProperty("ContentRoots")
            .EnumerateArray()
            .Select(r => r.GetString()!)
            .ToArray();

        // Find the content root that contains blazor.web.js
        var frameworkRoot = roots.FirstOrDefault(r =>
            File.Exists(Path.Combine(r.TrimEnd('/'), "blazor.web.js")));
        if (frameworkRoot is null) return;

        var provider = new PhysicalFileProvider(frameworkRoot.TrimEnd('/'));
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".js"] = "text/javascript";
        contentTypes.Mappings[".gz"] = "application/gzip";

        app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = "/_framework",
            FileProvider = provider,
            ContentTypeProvider = contentTypes,
        });
    }
}
