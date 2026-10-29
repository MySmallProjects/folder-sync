using System.Security.Cryptography;
using System.Text;
using FolderSyncEr.Client.Api;
using FolderSyncEr.Client.Platform;
using FolderSyncEr.Server.Data;
using FolderSyncEr.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSyncEr.Client.Tests;

public class ClientTestServerFixture : WebApplicationFactory<Program>
{
    public string TestRootPath { get; } = Path.Combine(Path.GetTempPath(), "foldersyncer_client_test_" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(TestRootPath, "test.db");
    public string StoragePath => Path.Combine(TestRootPath, "storage");

    public ClientTestServerFixture()
    {
        Directory.CreateDirectory(TestRootPath);
        Directory.CreateDirectory(StoragePath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={DbPath}",
                ["Storage:RootPath"] = StoragePath,
                ["Bootstrap:AdminUsername"] = "clienttestadmin",
                ["Bootstrap:AdminPassword"] = "AdminPass123!"
            });
        });

        builder.ConfigureServices(services =>
        {
            var dbOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ServerDbContext>));
            if (dbOptions != null) services.Remove(dbOptions);

            services.AddDbContext<ServerDbContext>(options =>
            {
                options.UseSqlite($"Data Source={DbPath}");
            });

            var blobDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IBlobStorageService));
            if (blobDescriptor != null) services.Remove(blobDescriptor);

            services.AddSingleton<IBlobStorageService>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BlobStorageService>>();
                return new BlobStorageService(config, logger);
            });
        });

        builder.UseEnvironment("Testing");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(TestRootPath))
        {
            try { Directory.Delete(TestRootPath, recursive: true); } catch { }
        }
    }
}

public class ClientApiIntegrationTests : IClassFixture<ClientTestServerFixture>, IDisposable
{
    private readonly ClientTestServerFixture _fixture;
    private readonly string _tempSecretFile;
    private readonly ISecretStore _secretStore;
    private readonly AuthTokenManager _tokenManager;
    private readonly IFileSyncApi _api;

    public ClientApiIntegrationTests(ClientTestServerFixture fixture)
    {
        _fixture = fixture;
        _tempSecretFile = Path.Combine(Path.GetTempPath(), $"client_api_test_{Guid.NewGuid():N}.dat");
        _secretStore = new EncryptedFileFallbackSecretStore(_tempSecretFile);
        _tokenManager = new AuthTokenManager(_secretStore, NullLogger<AuthTokenManager>.Instance);

        // Create client handler backed by in-memory test server
        var handler = _fixture.Server.CreateHandler();
        var authHandler = new AuthHeaderHandler(_tokenManager)
        {
            InnerHandler = handler
        };

        var httpClient = new HttpClient(authHandler)
        {
            BaseAddress = _fixture.Server.BaseAddress
        };

        _api = new FileSyncApiClient(httpClient, _tokenManager, NullLogger<FileSyncApiClient>.Instance)
        {
            ServerUrl = _fixture.Server.BaseAddress.ToString().TrimEnd('/')
        };
    }

    [Fact]
    public async Task CheckConnection_ReturnsHealthyAndLatency()
    {
        var status = await _api.CheckConnectionAsync();

        Assert.True(status.IsConnected);
        Assert.Equal("Healthy", status.Status);
        Assert.NotNull(status.ServerTimeUtc);
        Assert.True(status.LatencyMs >= 0);
    }

    [Fact]
    public async Task FullClientFlow_Login_Upload_Download_Conflict_Logout()
    {
        // 1. Login
        var login = await _api.LoginAsync("clienttestadmin", "AdminPass123!", "Client-Laptop");
        Assert.NotNull(login);
        Assert.Equal("clienttestadmin", login.User.Username);

        // 2. GetMe
        var me = await _api.GetMeAsync();
        Assert.NotNull(me);
        Assert.Equal("clienttestadmin", me.Username);

        // 3. Create Directory
        var dir = await _api.CreateDirectoryAsync("notes/daily");
        Assert.NotNull(dir);
        Assert.True(dir.IsDirectory);

        // 4. Streamed Upload
        var fileBytes = "Cross-platform sync client payload 🚀"u8.ToArray();
        var shaHex = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
        using (var uploadStream = new MemoryStream(fileBytes))
        {
            var uploadResult = await _api.UploadFileStreamAsync(
                path: "notes/daily/entry1.txt",
                contentStream: uploadStream,
                sha256Hex: shaHex,
                ifNoneMatch: "*"
            );

            Assert.True(uploadResult.IsSuccess);
            Assert.NotNull(uploadResult.File);
            Assert.Equal(1, uploadResult.File.Version);
            Assert.Equal(shaHex, uploadResult.File.Sha256);
        }

        // 5. Download Stream
        var meta = await _api.GetFileMetaByPathAsync("notes/daily/entry1.txt");
        Assert.NotNull(meta);

        using (var downloadStream = await _api.DownloadFileStreamAsync(meta.Id))
        using (var ms = new MemoryStream())
        {
            await downloadStream.CopyToAsync(ms);
            var downloadedBytes = ms.ToArray();
            Assert.Equal(fileBytes, downloadedBytes);
        }

        // 6. Optimistic Concurrency Conflict
        using (var conflictStream = new MemoryStream("Modified content"u8.ToArray()))
        {
            var staleHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
            var conflictResult = await _api.UploadFileStreamAsync(
                path: "notes/daily/entry1.txt",
                contentStream: conflictStream,
                ifMatchSha256: staleHash
            );

            Assert.False(conflictResult.IsSuccess);
            Assert.True(conflictResult.IsConflict);
            Assert.NotNull(conflictResult.Conflict);
            Assert.Equal(1, conflictResult.Conflict.Server.Version);
        }

        // 7. Logout
        await _api.LogoutAsync();
        var session = await _tokenManager.GetSessionAsync();
        Assert.Null(session);
    }

    [Fact]
    public async Task MultiUserAdmin_StorageByUsername_RoleLockdown_AndLoginStatusErrors()
    {
        // 1. Login as Admin
        var adminLogin = await _api.LoginAsync("clienttestadmin", "AdminPass123!", "Admin-Console");
        Assert.Equal("Admin", adminLogin.User.Role);

        using var adminHttp = _fixture.CreateClient();
        adminHttp.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);

        // 2. Create two regular users (alice & bob) via POST /api/users
        var createAliceResp = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(
            adminHttp, "/api/users", new FolderSyncEr.Contracts.CreateUserRequest("alice", "AlicePass123!"));
        Assert.Equal(System.Net.HttpStatusCode.Created, createAliceResp.StatusCode);
        var aliceDto = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<FolderSyncEr.Contracts.AdminUserDto>(createAliceResp.Content);
        Assert.NotNull(aliceDto);
        Assert.Equal("alice", aliceDto.Username);
        Assert.Equal("User", aliceDto.Role);

        var createBobResp = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(
            adminHttp, "/api/users", new FolderSyncEr.Contracts.CreateUserRequest("bob", "BobPass123!"));
        Assert.Equal(System.Net.HttpStatusCode.Created, createBobResp.StatusCode);
        var bobDto = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<FolderSyncEr.Contracts.AdminUserDto>(createBobResp.Content);
        Assert.NotNull(bobDto);

        // 3. Login as alice and upload a file; verify storage is under storage/alice/ and /api/users is 403 Forbidden
        await _api.LoginAsync("alice", "AlicePass123!", "Shared-PC");
        var aliceToken = (await _tokenManager.GetSessionAsync())!.AccessToken;

        using var aliceHttp = _fixture.CreateClient();
        aliceHttp.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", aliceToken);
        var forbiddenUsersResp = await aliceHttp.GetAsync("/api/users");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbiddenUsersResp.StatusCode);

        using (var ms = new MemoryStream("Alice private file"u8.ToArray()))
        {
            var res = await _api.UploadFileStreamAsync("hello.txt", ms, ifNoneMatch: "*");
            Assert.True(res.IsSuccess);
        }
        Assert.True(Directory.Exists(Path.Combine(_fixture.StoragePath, "alice")));

        // 4. Login as bob on the same device and upload same relative path; verify storage is under storage/bob/
        await _api.LoginAsync("bob", "BobPass123!", "Shared-PC");
        using (var ms = new MemoryStream("Bob private file"u8.ToArray()))
        {
            var res = await _api.UploadFileStreamAsync("hello.txt", ms, ifNoneMatch: "*");
            Assert.True(res.IsSuccess);
        }
        Assert.True(Directory.Exists(Path.Combine(_fixture.StoragePath, "bob")));

        var bobFiles = await _api.ListFilesAsync();
        Assert.Single(bobFiles.Items);
        Assert.Equal("Bob private file".Length, bobFiles.Items[0].Size);

        // 5. Admin disables alice -> LoginViewModel reports that alice is disabled
        var disableResp = await adminHttp.PatchAsync(
            $"/api/users/{aliceDto.Id}/status",
            System.Net.Http.Json.JsonContent.Create(new FolderSyncEr.Contracts.UpdateUserStatusRequest(IsDisabled: true, IsDeleted: null)));
        Assert.Equal(System.Net.HttpStatusCode.OK, disableResp.StatusCode);

        // Active JWT token for disabled user is immediately rejected
        var rejectedFilesResp = await aliceHttp.GetAsync("/api/files");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejectedFilesResp.StatusCode);

        var loginVm = new FolderSyncEr.Client.App.ViewModels.LoginViewModel(_api, _tokenManager);
        loginVm.Username = "alice";
        loginVm.Password = "AlicePass123!";
        await loginVm.LoginCommand.ExecuteAsync(null);
        Assert.False(loginVm.IsLoggedIn);
        Assert.Contains("User account 'alice' is disabled.", loginVm.ErrorMessage);

        // 6. Non-existent user -> LoginViewModel reports that user does not exist
        loginVm.Username = "nonexistent_user";
        loginVm.Password = "anypass";
        await loginVm.LoginCommand.ExecuteAsync(null);
        Assert.False(loginVm.IsLoggedIn);
        Assert.Contains("User 'nonexistent_user' does not exist.", loginVm.ErrorMessage);

        // 7. Admin marks alice as deleted -> LoginViewModel reports deleted/does not exist
        var deleteResp = await adminHttp.PatchAsync(
            $"/api/users/{aliceDto.Id}/status",
            System.Net.Http.Json.JsonContent.Create(new FolderSyncEr.Contracts.UpdateUserStatusRequest(IsDisabled: false, IsDeleted: true)));
        Assert.Equal(System.Net.HttpStatusCode.OK, deleteResp.StatusCode);

        loginVm.Username = "alice";
        loginVm.Password = "AlicePass123!";
        await loginVm.LoginCommand.ExecuteAsync(null);
        Assert.False(loginVm.IsLoggedIn);
        Assert.Contains("does not exist", loginVm.ErrorMessage);

        // 8. Admin restores alice and changes her password -> alice can sign in with new password
        var restoreResp = await adminHttp.PatchAsync(
            $"/api/users/{aliceDto.Id}/status",
            System.Net.Http.Json.JsonContent.Create(new FolderSyncEr.Contracts.UpdateUserStatusRequest(IsDisabled: false, IsDeleted: false)));
        Assert.Equal(System.Net.HttpStatusCode.OK, restoreResp.StatusCode);

        var pwResp = await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(
            adminHttp, $"/api/users/{aliceDto.Id}/password", new FolderSyncEr.Contracts.ChangeUserPasswordRequest("NewAlicePass999!"));
        Assert.Equal(System.Net.HttpStatusCode.OK, pwResp.StatusCode);

        loginVm.Username = "alice";
        loginVm.Password = "NewAlicePass999!";
        await loginVm.LoginCommand.ExecuteAsync(null);
        Assert.True(loginVm.IsLoggedIn);

        // 9. Folder rename & folder delete (used by website file tree actions)
        var folderDto = await _api.CreateDirectoryAsync("projects/2026");
        using (var ms = new MemoryStream("Spec document"u8.ToArray()))
        {
            var up = await _api.UploadFileStreamAsync("projects/2026/spec.txt", ms, ifNoneMatch: "*");
            Assert.True(up.IsSuccess);
        }

        // Rename folder 'projects/2026' -> 'projects/archive'
        var movedFolder = await _api.MoveFileAsync("projects/2026", "projects/archive");
        Assert.NotNull(movedFolder);
        Assert.Equal("projects/archive", movedFolder.Path);

        var movedChild = await _api.GetFileMetaByPathAsync("projects/archive/spec.txt");
        Assert.NotNull(movedChild);
        Assert.False(movedChild.IsDeleted);

        // Delete folder 'projects/archive' -> soft-deletes both the folder and 'projects/archive/spec.txt'
        var deletedOk = await _api.DeleteFileAsync(movedFolder.Id);
        Assert.True(deletedOk);

        var activeAfterDelete = await _api.ListFilesAsync(includeDeleted: false);
        Assert.DoesNotContain(activeAfterDelete.Items, f => f.Path.StartsWith("projects/archive", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (File.Exists(_tempSecretFile))
        {
            try { File.Delete(_tempSecretFile); } catch { }
        }
    }
}

