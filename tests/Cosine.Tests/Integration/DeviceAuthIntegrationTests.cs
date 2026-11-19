using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Cosine.Tests.Integration;

/// <summary>
/// The native-client sign-in: a device logs in once, uses bearer access tokens against the
/// cookie-free API, refreshes them from its refresh token, and loses access when it signs out,
/// is revoked, or the account's password changes.
/// </summary>
public sealed class DeviceAuthIntegrationTests : IAsyncLifetime
{
    private const string Username = "device-tester";
    private const string Password = "device-test-password";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("cosine_device_auth_test")
        .WithUsername("cosine")
        .WithPassword("cosine_test")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();

    private Factory _factory = null!;
    private string _mediaRoot = null!;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        _mediaRoot = Path.Combine(Path.GetTempPath(), $"cosine-device-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_mediaRoot);

        _factory = new Factory(_postgres.GetConnectionString(), _redis.GetConnectionString(), _mediaRoot);
        using (var _ = _factory.CreateClient()) { }

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        _userId = (await users.CreateUserAsync(Username, Password)).Id;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        if (Directory.Exists(_mediaRoot)) Directory.Delete(_mediaRoot, recursive: true);
    }

    [Fact]
    public async Task Login_ReturnsTokens_AndBearerAuthenticatesTheApi_WithoutACookie()
    {
        using var client = NativeClient();
        var login = await LoginAsync(client, "Pixel 9");

        Assert.False(string.IsNullOrEmpty(login.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(login.GetProperty("refreshToken").GetString()));
        Assert.Equal(Username, login.GetProperty("user").GetProperty("username").GetString());

        var response = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Authorize(client, login.GetProperty("accessToken").GetString()!);
        response = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Native clients must not be handed a browser session cookie.
        var loginResponse = await NativeClient().PostAsJsonAsync("/auth/device/login",
            new { Username, Password, DeviceName = "x" });
        Assert.False(loginResponse.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Login_WithWrongPassword_IsUnauthorized()
    {
        using var client = NativeClient();
        var response = await client.PostAsJsonAsync("/auth/device/login",
            new { Username, Password = "wrong", DeviceName = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_MintsAWorkingAccessToken_AndTheRefreshTokenKeepsWorking()
    {
        using var client = NativeClient();
        var login = await LoginAsync(client, "Pixel");
        var refreshToken = login.GetProperty("refreshToken").GetString()!;

        for (var i = 0; i < 2; i++)
        {
            var refreshed = await client.PostAsJsonAsync("/auth/device/refresh", new { refreshToken });
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            var body = await refreshed.Content.ReadFromJsonAsync<JsonElement>();

            Authorize(client, body.GetProperty("accessToken").GetString()!);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("00000000000000000000000000000000.secret")]
    public async Task Refresh_WithAnInvalidToken_IsUnauthorized(string refreshToken)
    {
        using var client = NativeClient();
        var response = await client.PostAsJsonAsync("/auth/device/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithTheRightIdButWrongSecret_IsUnauthorized()
    {
        using var client = NativeClient();
        var login = await LoginAsync(client, "Pixel");
        var id = login.GetProperty("refreshToken").GetString()!.Split('.')[0];

        var response = await client.PostAsJsonAsync("/auth/device/refresh", new { refreshToken = $"{id}.forged" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheRefreshToken()
    {
        using var client = NativeClient();
        var refreshToken = (await LoginAsync(client, "Pixel")).GetProperty("refreshToken").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/auth/device/logout", new { refreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/auth/device/refresh", new { refreshToken })).StatusCode);
    }

    [Fact]
    public async Task Devices_AreListed_AndRevokingOneStopsItsRefresh()
    {
        using var phone = NativeClient();
        var phoneLogin = await LoginAsync(phone, "Phone");
        using var tablet = NativeClient();
        var tabletLogin = await LoginAsync(tablet, "Tablet");

        Authorize(phone, phoneLogin.GetProperty("accessToken").GetString()!);
        var devices = await phone.GetFromJsonAsync<JsonElement>("/auth/devices");
        var tabletId = devices.EnumerateArray()
            .Single(d => d.GetProperty("name").GetString() == "Tablet")
            .GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync($"/auth/devices/{tabletId}")).StatusCode);

        var tabletRefresh = await tablet.PostAsJsonAsync("/auth/device/refresh",
            new { refreshToken = tabletLogin.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, tabletRefresh.StatusCode);

        var phoneRefresh = await phone.PostAsJsonAsync("/auth/device/refresh",
            new { refreshToken = phoneLogin.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, phoneRefresh.StatusCode);
    }

    [Fact]
    public async Task PasswordChange_RevokesDeviceTokens_AndOutstandingAccessTokens()
    {
        using var client = NativeClient();
        var login = await LoginAsync(client, "Pixel");
        Authorize(client, login.GetProperty("accessToken").GetString()!);

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            await users.ChangePasswordAsync(_userId, Password, "a-brand-new-password");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        var refresh = await client.PostAsJsonAsync("/auth/device/refresh",
            new { refreshToken = login.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Hub_AcceptsTheAccessTokenFromTheQueryString()
    {
        using var client = NativeClient();
        var accessToken = (await LoginAsync(client, "Pixel")).GetProperty("accessToken").GetString()!;

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/playback"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();
        await hub.StartAsync();
        Assert.Equal(HubConnectionState.Connected, hub.State);
    }

    [Fact]
    public async Task QueryStringToken_IsIgnoredOutsideHubs()
    {
        using var client = NativeClient();
        var accessToken = (await LoginAsync(client, "Pixel")).GetProperty("accessToken").GetString()!;
        var response = await client.GetAsync($"/auth/me?access_token={accessToken}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient NativeClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Requested-With", "CosineAndroid");
        return client;
    }

    private static void Authorize(HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private static async Task<JsonElement> LoginAsync(HttpClient client, string deviceName)
    {
        var response = await client.PostAsJsonAsync("/auth/device/login",
            new { Username, Password, DeviceName = deviceName });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed class Factory(string postgres, string redis, string mediaRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:cosine", postgres);
            builder.UseSetting("ConnectionStrings:redis", redis);
            builder.UseSetting("Media:RootPath", mediaRoot);
            builder.UseSetting("Transcoding:CachePath", Path.Combine(mediaRoot, ".transcode-cache"));
            builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)));
            builder.UseSetting("Jwt:Issuer", "Cosine");
            builder.UseSetting("Jwt:Audience", "Cosine");
        }
    }
}
