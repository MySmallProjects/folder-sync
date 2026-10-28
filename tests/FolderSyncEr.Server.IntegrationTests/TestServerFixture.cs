using System.Net.Http.Headers;
using System.Net.Http.Json;
using FolderSyncEr.Contracts;
using FolderSyncEr.Server.Data;
using FolderSyncEr.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FolderSyncEr.Server.IntegrationTests;

public class TestServerFixture : WebApplicationFactory<Program>
{
    public string TestRootPath { get; } = Path.Combine(Path.GetTempPath(), "foldersyncer_test_" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(TestRootPath, "test.db");
    public string StoragePath => Path.Combine(TestRootPath, "storage");

    public TestServerFixture()
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
                ["Bootstrap:AdminUsername"] = "testadmin",
                ["Bootstrap:AdminPassword"] = "Admin12345!"
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
                var logger = sp.GetRequiredService<ILogger<BlobStorageService>>();
                return new BlobStorageService(config, logger);
            });
        });

        builder.UseEnvironment("Testing");
    }

    public async Task<(HttpClient Client, LoginResponse Login)> CreateAuthenticatedClientAsync(
        string username = "testadmin",
        string password = "Admin12345!",
        string deviceId = "dev-1")
    {
        var client = CreateClient();
        var loginReq = new LoginRequest(username, password, deviceId, "Test Device");

        var response = await client.PostAsJsonAsync(ApiRoutes.Login, loginReq);
        response.EnsureSuccessStatusCode();

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return (client, login);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(TestRootPath))
        {
            try
            {
                Directory.Delete(TestRootPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
