using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Trips.Realtime.Tests;

internal static class HubClientFactory
{
    /// <summary>
    /// Build a SignalR <see cref="HubConnection"/> pointed at the test server's <c>/hubs/trip</c>
    /// endpoint with the supplied bearer token. Uses the factory's <see cref="HttpMessageHandler"/>
    /// so connections short-circuit through TestServer rather than over a real loopback socket.
    /// </summary>
    public static HubConnection Build(RealtimeApiFactory factory, string? accessToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/trip"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.SkipNegotiation = false;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                if (!string.IsNullOrEmpty(accessToken))
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                }
            })
            .Build();
    }
}
