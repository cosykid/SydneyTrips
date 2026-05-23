using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Trips.Core.Contracts;

namespace Trips.Api.Tests;

[Collection("ApiTests")]
public sealed class AuthEndpointsTests : IAsyncLifetime
{
    private readonly TripsApiFactory _factory;

    public AuthEndpointsTests(TripsApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Register_returns_token_pair()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new RegisterRequest("alice@example.com", "password123", "Alice"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        tokens.Should().NotBeNull();
        tokens!.AccessToken.Should().NotBeNullOrEmpty();
        tokens.RefreshToken.Should().NotBeNullOrEmpty();
        tokens.Email.Should().Be("alice@example.com");
    }

    [Fact]
    public async Task Register_rejects_duplicate_email()
    {
        var client = _factory.CreateClient();
        var ok = await client.PostAsJsonAsync("/auth/register", new RegisterRequest("dup@example.com", "password123", "Dup"));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        var dup = await client.PostAsJsonAsync("/auth/register", new RegisterRequest("dup@example.com", "password123", "Dup"));
        dup.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_validates_input()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new RegisterRequest("not-an-email", "short", ""));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_returns_token_for_valid_credentials()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest("login@example.com", "password123", "Login User"));

        var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("login@example.com", "password123"));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await login.Content.ReadFromJsonAsync<AuthTokenResponse>();
        tokens!.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_rejects_bad_password()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest("badpw@example.com", "password123", "Bad PW"));

        var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("badpw@example.com", "wrong-password"));
        login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_issues_new_pair()
    {
        var client = _factory.CreateClient();
        var register = await client.PostAsJsonAsync("/auth/register", new RegisterRequest("refresh@example.com", "password123", "Refresh"));
        var first = await register.Content.ReadFromJsonAsync<AuthTokenResponse>();

        var refresh = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(first!.RefreshToken));
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await refresh.Content.ReadFromJsonAsync<AuthTokenResponse>();
        second!.RefreshToken.Should().NotBe(first.RefreshToken);
    }
}
