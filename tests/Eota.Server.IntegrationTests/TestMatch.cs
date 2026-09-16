using Eota.Client.Core;
using Eota.Client.Transport.InProcess;
using Eota.Client.Transport.WebSocket;
using Eota.Server.Application;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Eota.Server.IntegrationTests;

internal sealed class TestMatch(MatchActor actor, WebApplication? host) : IAsyncDisposable
{
    public MatchActor Actor { get; } = actor;
    public Uri? Address => host is null ? null : new Uri(host.Urls.Single());

    public static async Task<TestMatch> StartAsync(bool remote, string? fixturePath = null)
    {
        fixturePath ??= Fixture.Path;
        if (!remote) { return new TestMatch(new MatchActor("p4-demo", FileMatchLoader.Load(fixturePath, 123456789)), null); }
        var app = await Host.Program.BuildAsync(["--urls", "http://127.0.0.1:0", "--fixture", fixturePath,
            "--Logging:LogLevel:Default", "Warning"]);
        await app.StartAsync();
        return new TestMatch(app.Services.GetRequiredService<InMemoryMatchDirectory>().Find("p4-demo")!, app);
    }

    public async Task<IGameClientTransport> ConnectAsync(Audience audience)
    {
        if (host is null) { return await InProcessGameTransport.ConnectAsync(Actor, audience); }
        var role = audience switch { Audience.PlayerOne => "playerOne", Audience.PlayerTwo => "playerTwo", _ => "spectator" };
        var endpoint = new UriBuilder(Address!) { Scheme = "ws", Path = $"/matches/{Actor.MatchId}/ws", Query = $"audience={role}" }.Uri;
        return await WebSocketGameTransport.ConnectAsync(endpoint, Actor.MatchId, Actor.RuleContentHash);
    }

    public async ValueTask DisposeAsync()
    {
        if (host is null) { await Actor.DisposeAsync(); return; }
        await host.StopAsync();
        await host.DisposeAsync();
    }
}
