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

    public void Dispose()
    {
        if (File.Exists(_tempSecretFile))
        {
            try { File.Delete(_tempSecretFile); } catch { }
        }
    }
}

