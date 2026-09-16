using Eota.Server.Infrastructure;
using Eota.Server.Transport.WebSocket;
using Eota.Transport.Contracts;

namespace Eota.Server.Host;

public static class Program
{
    public static async Task Main(string[] args)
    {
        await using var app = await BuildAsync(args).ConfigureAwait(false);
        if (app.Configuration["managed"] == "true")
        {
            _ = Task.Run(async () =>
            {
                // EOF also covers a crashed launcher. No unrelated process is inspected or terminated.
                await Console.In.ReadLineAsync().ConfigureAwait(false);
                await app.StopAsync().ConfigureAwait(false);
            });
        }
        await app.RunAsync().ConfigureAwait(false);
    }

    public static async Task<WebApplication> BuildAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) { builder.WebHost.UseUrls("http://127.0.0.1:5075"); }
        if (builder.Configuration["room"] is { } roomPath) { return RoomHost.Build(builder, roomPath); }
        if (builder.Configuration["fixture"] is null) { throw new ArgumentException("Use --room <configuration> or an explicit --fixture for development.", nameof(args)); }
        var fixture = builder.Configuration["fixture"] ?? Path.Combine(Directory.GetCurrentDirectory(), "tests", "Fixtures", "P4Match");
        var matches = new InMemoryMatchDirectory();
        var seedText = builder.Configuration["seed"] ?? "123456789";
        if (!ulong.TryParse(seedText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seed))
        { throw new ArgumentException("Seed must be an unsigned integer.", nameof(args)); }
        var matchId = builder.Configuration["match-id"] ?? "p4-demo";
        await matches.CreateAsync(matchId, FileMatchLoader.Load(fixture, seed, builder.Configuration["cards"])).ConfigureAwait(false);
        builder.Services.AddSingleton(_ => matches);
        var app = builder.Build();
        _ = app.Services.GetRequiredService<InMemoryMatchDirectory>();
        app.UseWebSockets();
        app.MapGet("/health", () => Results.Json(new { status = "ok", contractVersion = ContractJson.Version }));
        app.MapGet("/matches/{matchId}/ws", async (HttpContext context, string matchId, InMemoryMatchDirectory directory) =>
        {
            var actor = directory.Find(matchId);
            if (actor is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            var audience = context.Request.Query["audience"].ToString() switch
            {
                "playerOne" => (Audience?)Audience.PlayerOne,
                "playerTwo" => Audience.PlayerTwo,
                "spectator" => Audience.Spectator,
                _ => null
            };
            if (audience is null) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            await WebSocketMatchEndpoint.HandleAsync(context, actor, audience.Value).ConfigureAwait(false);
        });
        return app;
    }
}
