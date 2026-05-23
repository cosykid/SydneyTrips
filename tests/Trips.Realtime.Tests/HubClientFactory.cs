using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Trips.Realtime.Tests;

internal static class HubClientFactory
{
    /// <summary>
    /// Build a SignalR <see cref="HubConnection"/> pointed at the test server's <c>/hubs/trip</c>
    /// endpoint. Uses the factory's <see cref="HttpMessageHandler"/> so connections short-circuit
    /// through TestServer rather than over a real loopback socket.
    ///
    /// If <paramref name="client"/> is supplied, its cookie jar is shared with the SignalR
    /// transport so the anonymous <c>trips_session</c> cookie flows on the negotiate request and
    /// the WebSocket upgrade. With no client, the connection negotiates anonymously and the
    /// server stamps a fresh session GUID into the response — useful for "fresh browser" tests.
    /// </summary>
    public static HubConnection Build(RealtimeApiFactory factory, AnonymousClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/trip"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.SkipNegotiation = false;
                if (client is not null)
                {
                    // Wrap the test-server handler in a copy of the client's cookie-jar handler so
                    // SignalR's negotiate + poll requests both carry the trips_session cookie.
                    options.HttpMessageHandlerFactory = _ =>
                    {
                        var jar = new CookieJarHandler { InnerHandler = factory.Server.CreateHandler() };
                        // Pre-seed the jar with the client's cookies so we don't have to re-prime.
                        foreach (System.Net.Cookie c in client.CookieJar.Container.GetAllCookies())
                        {
                            jar.Container.Add(new System.Net.Cookie(c.Name, c.Value, c.Path, c.Domain));
                        }
                        return jar;
                    };
                }
                else
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                }
            })
            .Build();
    }
}
