using System.Net;
using System.Net.Http.Json;
using FolderSyncEr.Contracts;

namespace FolderSyncEr.Server.IntegrationTests;

public class StatusAndAuthTests : IClassFixture<TestServerFixture>
{
    private readonly TestServerFixture _fixture;

    public StatusAndAuthTests(TestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StatusEndpoint_ReturnsHealthyAndDbReachable()
    {
        var client = _fixture.CreateClient();
        var response = await client.GetAsync(ApiRoutes.Status);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.NotNull(status);
        Assert.Equal("Healthy", status.Status);
        Assert.True(status.DbReachable);
        Assert.True(status.ServerTimeUtc > DateTime.UtcNow.AddMinutes(-5));
        Assert.False(string.IsNullOrWhiteSpace(status.Version));
        Assert.NotNull(status.CompiledUtc);
        Assert.True(status.CompiledUtc > DateTime.UtcNow.AddYears(-1));
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokensAndUserInfo()
    {
        var client = _fixture.CreateClient();
        var loginReq = new LoginRequest("testadmin", "Admin12345!", "dev-test", "Desktop Client");

        var response = await client.PostAsJsonAsync(ApiRoutes.Login, loginReq);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));
        Assert.Equal("testadmin", result.User.Username);
        Assert.Equal("Admin", result.User.Role);
    }

    [Fact]
    public async Task Login_WithInvalidPassword_ReturnsUnauthorized()
    {
        var client = _fixture.CreateClient();
        var loginReq = new LoginRequest("testadmin", "WrongPassword123!", "dev-test", "Desktop Client");

        var response = await client.PostAsJsonAsync(ApiRoutes.Login, loginReq);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_RotatesSuccessfully_AndRejectsReusedToken()
    {
        var client = _fixture.CreateClient();
        var loginReq = new LoginRequest("testadmin", "Admin12345!", "dev-rot", "Laptop Client");
        var loginRes = await client.PostAsJsonAsync(ApiRoutes.Login, loginReq);
        var login = await loginRes.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);

        // 1st Refresh
        var refreshReq = new RefreshRequest(login.RefreshToken, "dev-rot");
        var refreshRes1 = await client.PostAsJsonAsync(ApiRoutes.Refresh, refreshReq);
        Assert.Equal(HttpStatusCode.OK, refreshRes1.StatusCode);
        var refreshed1 = await refreshRes1.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(refreshed1);
        Assert.NotEqual(login.RefreshToken, refreshed1.RefreshToken);

        // Reusing the first (now revoked) refresh token must be rejected
        var reuseRes = await client.PostAsJsonAsync(ApiRoutes.Refresh, refreshReq);
        Assert.Equal(HttpStatusCode.Unauthorized, reuseRes.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        var client = _fixture.CreateClient();
        var response = await client.GetAsync(ApiRoutes.Files);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

