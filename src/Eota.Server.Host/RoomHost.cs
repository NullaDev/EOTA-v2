using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Eota.Server.Infrastructure;
using Eota.Server.Transport.WebSocket;
using Eota.Transport.Contracts;
using Microsoft.AspNetCore.RateLimiting;

namespace Eota.Server.Host;

internal static class RoomHost
{
    public static WebApplication Build(WebApplicationBuilder builder, string path)
    {
        var room = new HostedRoom(path);
        builder.Services.AddSingleton(_ => room);
        builder.Services.AddHostedService<RoomClockService>();
        builder.Services.AddHostedService<RoomOperationsService>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("room", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        var app = builder.Build(); _ = app.Services.GetRequiredService<HostedRoom>();
        app.UseWebSockets(); app.UseRateLimiter();
        app.MapGet("/health", () => Results.Json(new { status = "ok", contractVersion = ContractJson.Version, matchId = room.MatchId }));
        app.MapGet("/matches/{matchId}/room", async (HttpContext context, string matchId) =>
        {
            var audience = Authenticate(context, room, matchId);
            if (audience is null) { return; }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ContractJson.Serialize(await room.DescribeAsync(audience.Value, context.RequestAborted).ConfigureAwait(false)), context.RequestAborted).ConfigureAwait(false);
        }).RequireRateLimiting("room");
        app.MapPost("/matches/{matchId}/ready", async (HttpContext context, string matchId) =>
        {
            var audience = Authenticate(context, room, matchId);
            if (audience is null) { return; }
            try
            {
                using var body = new MemoryStream(); var buffer = new byte[4096];
                while (true)
                {
                    var count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                    if (count == 0) { break; }
                    // Up to 2048 card fingerprints; ordinary WebSocket commands retain their 64 KiB limit.
                    if (body.Length + count > 512 * 1024) { context.Response.StatusCode = 413; return; }
                    body.Write(buffer, 0, count);
                }
                var admission = ContractJson.Deserialize<RoomAdmission>(new UTF8Encoding(false, true).GetString(body.ToArray()));
                var description = await room.AdmitAsync(audience.Value, admission, context.RequestAborted).ConfigureAwait(false);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(ContractJson.Serialize(description), context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or DecoderFallbackException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(error is InvalidDataException ? error.Message : "invalid-admission-message", context.RequestAborted).ConfigureAwait(false);
            }
        }).RequireRateLimiting("room");
        app.MapGet("/matches/{matchId}/ws", async (HttpContext context, string matchId) =>
        {
            var audience = Authenticate(context, room, matchId);
            if (audience is null) { return; }
            var description = await room.DescribeAsync(audience.Value, context.RequestAborted).ConfigureAwait(false);
            if (context.Request.Headers[MatchHandshake.ProtocolHashHeader].ToString() != description.ProtocolHash)
            { context.Response.StatusCode = 412; return; }
            var settings = context.Request.Headers[RoomTiming.SettingsHashHeader].ToString();
            if (settings != description.RoomSettingsHash)
            { context.Response.StatusCode = 412; return; }
            if (room.Actor is not { } actor) { context.Response.StatusCode = 425; return; }
            await WebSocketMatchEndpoint.HandleAsync(context, actor, audience.Value).ConfigureAwait(false);
        }).RequireRateLimiting("room");
        return app;
    }

    private static Audience? Authenticate(HttpContext context, HostedRoom room, string matchId)
    {
        if (matchId != room.MatchId) { context.Response.StatusCode = 404; return null; }
        Audience? audience = context.Request.Query["audience"].ToString() switch
        { "playerOne" => Audience.PlayerOne, "playerTwo" => Audience.PlayerTwo, "spectator" => Audience.Spectator, _ => null };
        var authorization = context.Request.Headers.Authorization.ToString();
        if (audience is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            || !room.Authenticate(audience.Value, authorization[7..]))
        { context.Response.StatusCode = 401; return null; }
        return audience;
    }
}

internal sealed partial class RoomClockService(HostedRoom room, ILogger<RoomClockService> logger) : BackgroundService
{
    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Room clock stopped for {MatchId}")]
    private static partial void ClockFailed(ILogger logger, string matchId, Exception error);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            { await room.TickAsync(stoppingToken).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) { ClockFailed(logger, room.MatchId, error); }
    }
}

internal sealed partial class RoomOperationsService(HostedRoom room, ILogger<RoomOperationsService> logger) : BackgroundService
{
    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Operations report unavailable for {MatchId}")]
    private static partial void ReportFailed(ILogger logger, string matchId, Exception error);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await room.WriteOperationsAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ReportFailed(logger, room.MatchId, error); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
