using System.Net;
using BTCPayServer.Plugins.LightningTerminal.Services;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Litrpc;
using Xunit;

namespace BTCPayServer.Plugins.LightningTerminal.UnitTests;

/// <summary>
/// Pins the transport used against litd's plaintext listener.
/// </summary>
/// <remarks>
/// litd serves that port from a bare http.Server with no h2c wrapper, so it speaks HTTP/1.1 and
/// nothing else. GrpcChannel stamps requests HTTP/2 by default and GrpcWebHandler only rewrites the
/// framing, so the wrong options fail in the transport with PROTOCOL_ERROR before litd sees the call.
/// These tests run against a listener with the same constraint.
/// </remarks>
public class UpstreamTransportTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _address;
    private readonly List<(string Method, string Path, string? ContentType, string? Authorization)> _received = [];

    public UpstreamTransportTests()
    {
        var port = FreePort();
        _address = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{_address}/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }

            lock (_received)
            {
                _received.Add((context.Request.HttpMethod, context.Request.Url!.AbsolutePath,
                    context.Request.ContentType, context.Request.Headers["Authorization"]));
            }

            // Answer as a grpc-web endpoint would, so a call that arrives gets a gRPC status back
            // rather than a transport failure - which is exactly the distinction under test.
            context.Response.ContentType = "application/grpc-web+proto";
            context.Response.Headers["grpc-status"] = ((int)StatusCode.Unimplemented).ToString();
            context.Response.Headers["grpc-message"] = "stub";
            context.Response.StatusCode = 200;
            context.Response.Close();
        }
    }

    private async Task<RpcException> CallWith(GrpcChannelOptions options, Metadata? headers = null)
    {
        var client = new Litrpc.Status.StatusClient(GrpcChannel.ForAddress(_address, options));
        return await Assert.ThrowsAsync<RpcException>(() => client
            .SubServerStatusAsync(new SubServerStatusReq(), headers, DateTime.UtcNow.AddSeconds(10))
            .ResponseAsync);
    }

    [Fact]
    public async Task TheConfiguredOptionsReachAnHttp11OnlyServer()
    {
        // Unimplemented is the stub's answer, so the call completed a round trip.
        var error = await CallWith(LitdClient.UpstreamChannelOptions());

        Assert.Equal(StatusCode.Unimplemented, error.StatusCode);
        Assert.Contains(("POST", "/litrpc.Status/SubServerStatus"), _received.Select(r => (r.Method, r.Path)));
        Assert.Contains(_received, r => r.ContentType?.StartsWith("application/grpc-web") == true);
    }

    [Fact]
    public async Task WithoutTheHttpVersionPinTheCallNeverArrives()
    {
        // The regression this guards: the same handler, minus the version pin, is what shipped and
        // failed with "The HTTP/2 server sent invalid data on the connection".
        var error = await CallWith(new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler()),
            DisposeHttpClient = true
        });

        Assert.Equal(StatusCode.Internal, error.StatusCode);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task TheBasicAuthHeaderSurvivesTheGrpcWebFraming()
    {
        // litd's proxy compares this against base64(password:password) and swaps in a macaroon, so it
        // has to arrive byte for byte.
        const string password = "sUpErSeCuRe";
        var credential = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{password}:{password}"));

        await CallWith(LitdClient.UpstreamChannelOptions(),
            new Metadata { { "authorization", $"Basic {credential}" } });

        Assert.Contains(_received, r => r.Authorization == $"Basic {credential}");
    }

    public void Dispose()
    {
        _listener.Stop();
        ((IDisposable)_listener).Dispose();
    }
}
