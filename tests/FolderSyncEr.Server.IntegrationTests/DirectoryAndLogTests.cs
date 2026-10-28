using System.Net;
using System.Net.Http.Json;
using FolderSyncEr.Contracts;

namespace FolderSyncEr.Server.IntegrationTests;

public class DirectoryAndLogTests : IClassFixture<TestServerFixture>
{
    private readonly TestServerFixture _fixture;

    public DirectoryAndLogTests(TestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateDirectory_AndList_ReturnsDirectoryRecord()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-dir");

        var dirPath = "projects/subfolder";
        var res = await client.PostAsJsonAsync(ApiRoutes.Directories, new CreateDirectoryRequest(dirPath));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var dirDto = await res.Content.ReadFromJsonAsync<FileDto>();
        Assert.NotNull(dirDto);
        Assert.True(dirDto.IsDirectory);
        Assert.Equal(dirPath, dirDto.Path);

        // List files should include directory
        var listRes = await client.GetAsync(ApiRoutes.Files);
        var list = await listRes.Content.ReadFromJsonAsync<FileListResponse>();
        Assert.Contains(list!.Items, f => f.Path == dirPath && f.IsDirectory);
    }

    [Fact]
    public async Task ListFiles_WithSinceSeq_OnlyReturnsChangesAfterSeq()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-seq");

        // 1. Initial list to get current snapshotSeq
        var initialListRes = await client.GetAsync(ApiRoutes.Files);
        var initialList = await initialListRes.Content.ReadFromJsonAsync<FileListResponse>();
        var baselineSeq = initialList!.SnapshotSeq;

        // 2. Upload a new file
        var path = "seq/new_item.txt";
        using var uploadReq = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={path}");
        uploadReq.Content = new ByteArrayContent("Sequence filter test content"u8.ToArray());
        var uploadRes = await client.SendAsync(uploadReq);
        Assert.Equal(HttpStatusCode.Created, uploadRes.StatusCode);

        // 3. Query with sinceSeq = baselineSeq
        var filteredRes = await client.GetAsync($"{ApiRoutes.Files}?sinceSeq={baselineSeq}");
        var filteredList = await filteredRes.Content.ReadFromJsonAsync<FileListResponse>();
        Assert.NotNull(filteredList);
        Assert.Contains(filteredList.Items, f => f.Path == path);
        Assert.All(filteredList.Items, f => Assert.True(f.ChangeSeq > baselineSeq));
    }

    [Fact]
    public async Task ActivityLog_RecordsUploadActions_AndIsQueryable()
    {
        var (client, login) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-log");

        var path = "logs/test_file.txt";
        using var uploadReq = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={path}");
        uploadReq.Content = new ByteArrayContent("Logging test"u8.ToArray());
        await client.SendAsync(uploadReq);

        var logRes = await client.GetAsync($"{ApiRoutes.Logs}?action=Upload");
        Assert.Equal(HttpStatusCode.OK, logRes.StatusCode);
        var pagedLogs = await logRes.Content.ReadFromJsonAsync<PagedResult<ActivityLogDto>>();
        Assert.NotNull(pagedLogs);
        Assert.True(pagedLogs.TotalCount > 0);
        Assert.Contains(pagedLogs.Items, l => l.Action == "Upload" && l.Path == path && l.Result == "Success");
    }

    [Fact]
    public async Task StorageEndpoint_ReturnsSpaceInfo()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync();

        var res = await client.GetAsync(ApiRoutes.Storage);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var info = await res.Content.ReadFromJsonAsync<StorageInfoDto>();
        Assert.NotNull(info);
        Assert.True(info.TotalSpace > 0);
    }
}

