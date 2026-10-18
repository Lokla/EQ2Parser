using System.Net;
using System.Net.Sockets;
using EQ2Parser.App.Services;

namespace EQ2Parser.Shell.Tests;

/// <summary>
/// The guard is what lets a test run hold settings pointing anywhere and
/// still never reach the real site. HttpClient.DefaultProxy is process
/// state, so these run in one collection that puts it back afterwards.
/// No test here can reach the internet: the outside host is a reserved
/// .invalid name, and the assertion is that the request died at the
/// guard's closed port, not at name resolution.
/// </summary>
[Collection(nameof(DefaultProxyCollection))]
public sealed class LoopbackOnlyGuardTests : IDisposable
{
    private readonly IWebProxy _saved = HttpClient.DefaultProxy;

    public void Dispose() => HttpClient.DefaultProxy = _saved;

    [Fact]
    public async Task A_Request_To_Any_Other_Host_Dies_At_The_Guard()
    {
        LoopbackOnlyGuard.Install();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.PostAsync("https://eq2lexicon.invalid/api/parses/ingest", new StringContent("{}")));

        // Refused at 127.0.0.1:9 — the target name was never even looked up
        // (an unguarded request to .invalid fails at DNS, not like this).
        var socket = FindInner<SocketException>(ex);
        Assert.NotNull(socket);
        Assert.Equal(SocketError.ConnectionRefused, socket!.SocketErrorCode);
    }

    [Fact]
    public async Task Loopback_Still_Gets_Through()
    {
        LoopbackOnlyGuard.Install();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            var reply = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
            await stream.WriteAsync(reply);
        });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var body = await http.GetStringAsync($"http://127.0.0.1:{port}/api/auth/whoami");

        Assert.Equal("ok", body);
        await serve;
    }

    [Theory]
    [InlineData("http://127.0.0.1:8000/", true)]
    [InlineData("http://[::1]:8000/", true)]
    [InlineData("http://localhost:8000/", true)]
    [InlineData("http://LOCALHOST/", true)]
    [InlineData("https://varsoon.eq2lexicon.com/", false)]
    [InlineData("http://127.0.0.1.nip.io/", false)]
    [InlineData("http://10.0.0.5/", false)]
    public void Only_Loopback_Counts_As_Loopback(string url, bool loopback) =>
        Assert.Equal(loopback, LoopbackOnlyGuard.IsLoopback(new Uri(url)));

    private static T? FindInner<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is T match)
                return match;
        }
        return null;
    }
}

[CollectionDefinition(nameof(DefaultProxyCollection), DisableParallelization = true)]
public sealed class DefaultProxyCollection;
