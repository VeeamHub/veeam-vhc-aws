using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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
        app.MapStaticAssets();

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
}
