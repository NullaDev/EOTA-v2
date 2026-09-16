using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Eota.Client.Core;
using Eota.Client.Transport.WebSocket;

namespace Eota.Server.IntegrationTests;

public sealed class HeadlessProcessTests
{
    [Fact]
    public async Task IndependentHeadlessProcessRunsTwoRemoteClientsToGoldenOutcome()
    {
        var configuration = typeof(HeadlessProcessTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var hostDll = Path.Combine(Fixture.Root, "src", "Eota.Server.Host", "bin", configuration, "net8.0", "Eota.Server.Host.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Fixture.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { hostDll, "--urls", "http://127.0.0.1:0", "--fixture", Fixture.Path,
            "--Logging:LogLevel:Default", "Information" }) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            Uri address;
            const string marker = "Now listening on: ";
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                if (line is null) { throw new InvalidOperationException("Host exited: " + await stderr); }
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0) { address = new Uri(line[(index + marker.Length)..].Trim()); break; }
            }
            var remainingOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
            using var http = new HttpClient { BaseAddress = address };
            using var health = await http.GetAsync(new Uri("/health", UriKind.Relative), deadline.Token);
            health.EnsureSuccessStatusCode();
            Uri Endpoint(string role) => new UriBuilder(address)
            { Scheme = "ws", Path = "/matches/p4-demo/ws", Query = "audience=" + role }.Uri;
            var contentHash = Eota.Server.Infrastructure.FileMatchLoader.Load(Fixture.Path, 123456789).Content.Hash.ToString();
            await using (var one = new GameClient(await WebSocketGameTransport.ConnectAsync(Endpoint("playerOne"), "p4-demo", contentHash, deadline.Token)))
            await using (var two = new GameClient(await WebSocketGameTransport.ConnectAsync(Endpoint("playerTwo"), "p4-demo", contentHash, deadline.Token)))
            {
                await one.SynchronizeAsync(deadline.Token);
                await two.SynchronizeAsync(deadline.Token);
                using var commands = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixture.Path, "commands.json")));
                foreach (var command in commands.RootElement.EnumerateArray())
                {
                    var client = command.GetProperty("playerId").GetInt32() == 0 ? one : two;
                    var ack = await client.SubmitAsync(Fixture.ReadPayload(command), command.GetProperty("expectedPlayerRevision").GetUInt64(),
                        cancellationToken: deadline.Token);
                    Assert.True(ack.Accepted, ack.Code);
                }
                foreach (var client in new[] { one, two })
                {
                    await client.SynchronizeAsync(deadline.Token);
                    Assert.Equal("PlayerOneWon", client.Store.View!.Outcome);
                    Assert.Equal("Finished", client.Store.View.Status);
                    Assert.Equal(73, client.Store.DrainPresentationFrames().Length);
                    Assert.False(client.Store.NeedsSnapshot);
                }
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(deadline.Token);
            await remainingOutput;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync();
            await stderr;
        }
    }
}
