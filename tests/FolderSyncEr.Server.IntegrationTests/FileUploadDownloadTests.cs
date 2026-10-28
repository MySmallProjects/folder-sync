using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FolderSyncEr.Contracts;

namespace FolderSyncEr.Server.IntegrationTests;

public class FileUploadDownloadTests : IClassFixture<TestServerFixture>
{
    private readonly TestServerFixture _fixture;

    public FileUploadDownloadTests(TestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UploadFile_WithValidStreamAndSha256_ReturnsCreatedAndPersists()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-upload");

        var fileContent = "Hello, Cross-Device Sync Server!"u8.ToArray();
        var sha256Hex = Convert.ToHexString(SHA256.HashData(fileContent)).ToLowerInvariant();
        var filePath = "docs/welcome.txt";

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={filePath}");
        request.Content = new ByteArrayContent(fileContent);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add(ApiHeaders.ContentSha256, sha256Hex);
        request.Headers.Add(ApiHeaders.LastModifiedUtc, DateTime.UtcNow.ToString("o"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var fileDto = await response.Content.ReadFromJsonAsync<FileDto>();
        Assert.NotNull(fileDto);
        Assert.Equal(filePath, fileDto.Path);
        Assert.Equal(sha256Hex, fileDto.Sha256);
        Assert.Equal(fileContent.Length, fileDto.Size);
        Assert.Equal(1, fileDto.Version);

        // Verify ETag on response
        Assert.True(response.Headers.Contains(ApiHeaders.ETag));
    }

    [Fact]
    public async Task UploadFile_WithMismatchSha256_Returns422UnprocessableEntity()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-hash-err");

        var fileContent = "Content A"u8.ToArray();
        var wrongSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path=test/wronghash.txt");
        request.Content = new ByteArrayContent(fileContent);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add(ApiHeaders.ContentSha256, wrongSha256);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UploadFile_WithPathTraversal_Returns400BadRequest()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync();

        var fileContent = "Malicious"u8.ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path=../../etc/passwd");
        request.Content = new ByteArrayContent(fileContent);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DownloadFile_ReturnsExactBytesAndEtag()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-download");

        var fileContent = Encoding.UTF8.GetBytes("Download test payload with special characters: 🚀 12345");
        var sha256Hex = Convert.ToHexString(SHA256.HashData(fileContent)).ToLowerInvariant();
        var filePath = "reports/download_test.txt";

        // Upload first
        using var uploadReq = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={filePath}");
        uploadReq.Content = new ByteArrayContent(fileContent);
        uploadReq.Headers.Add(ApiHeaders.ContentSha256, sha256Hex);
        var uploadRes = await client.SendAsync(uploadReq);
        Assert.Equal(HttpStatusCode.Created, uploadRes.StatusCode);
        var fileDto = await uploadRes.Content.ReadFromJsonAsync<FileDto>();
        Assert.NotNull(fileDto);

        // Download by ID
        var downloadRes = await client.GetAsync($"api/files/{fileDto.Id}/content");
        Assert.Equal(HttpStatusCode.OK, downloadRes.StatusCode);
        var downloadedBytes = await downloadRes.Content.ReadAsByteArrayAsync();

        Assert.Equal(fileContent, downloadedBytes);
        Assert.Equal($"\"{sha256Hex}\"", downloadRes.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task DownloadFile_WithRangeHeader_Returns206PartialContent()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-range");

        var fullString = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var fileContent = Encoding.UTF8.GetBytes(fullString);
        var sha256Hex = Convert.ToHexString(SHA256.HashData(fileContent)).ToLowerInvariant();
        var filePath = "data/range_test.txt";

        // Upload
        using var uploadReq = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={filePath}");
        uploadReq.Content = new ByteArrayContent(fileContent);
        uploadReq.Headers.Add(ApiHeaders.ContentSha256, sha256Hex);
        var uploadRes = await client.SendAsync(uploadReq);
        var fileDto = await uploadRes.Content.ReadFromJsonAsync<FileDto>();
        Assert.NotNull(fileDto);

        // Download range 5-9 (5 bytes: '56789')
        using var rangeReq = new HttpRequestMessage(HttpMethod.Get, $"api/files/{fileDto.Id}/content");
        rangeReq.Headers.Range = new RangeHeaderValue(5, 9);
        var rangeRes = await client.SendAsync(rangeReq);

        Assert.Equal(HttpStatusCode.PartialContent, rangeRes.StatusCode);
        var rangeBytes = await rangeRes.Content.ReadAsByteArrayAsync();
        Assert.Equal("56789", Encoding.UTF8.GetString(rangeBytes));
    }
}

