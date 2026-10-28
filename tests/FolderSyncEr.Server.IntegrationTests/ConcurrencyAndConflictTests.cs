using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FolderSyncEr.Contracts;

namespace FolderSyncEr.Server.IntegrationTests;

public class ConcurrencyAndConflictTests : IClassFixture<TestServerFixture>
{
    private readonly TestServerFixture _fixture;

    public ConcurrencyAndConflictTests(TestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IfNoneMatch_OnExistingFile_Returns409Conflict()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-cc-1");

        var path = "conflict/if_none_match.txt";
        var content1 = "First Version"u8.ToArray();

        // 1. Initial upload
        using var req1 = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={path}");
        req1.Content = new ByteArrayContent(content1);
        req1.Headers.Add(ApiHeaders.IfNoneMatch, "*");
        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

        // 2. Second upload with If-None-Match: * must fail with 409 Conflict
        var content2 = "Second Version"u8.ToArray();
        using var req2 = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={path}");
        req2.Content = new ByteArrayContent(content2);
        req2.Headers.Add(ApiHeaders.IfNoneMatch, "*");
        var res2 = await client.SendAsync(req2);

        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);
        var conflict = await res2.Content.ReadFromJsonAsync<ConflictDto>();
        Assert.NotNull(conflict);
        Assert.Equal(path, conflict.Path);
        Assert.Equal(1, conflict.Server.Version);
    }

    [Fact]
    public async Task IfMatch_MatchingBaseSha256_Succeeds_AndMismatchReturns409Conflict()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-cc-2");

        var path = "conflict/if_match_test.txt";
        var content1 = "Original Text"u8.ToArray();
        var hash1 = Convert.ToHexString(SHA256.HashData(content1)).ToLowerInvariant();

        // 1. Upload initial file
        using var req1 = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={path}");
        req1.Content = new ByteArrayContent(content1);
        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);
        var fileDto1 = await res1.Content.ReadFromJsonAsync<FileDto>();
        Assert.NotNull(fileDto1);

        // 2. Update with matching If-Match -> 200 OK, Version = 2
        var content2 = "Updated Text"u8.ToArray();
        var hash2 = Convert.ToHexString(SHA256.HashData(content2)).ToLowerInvariant();
        using var req2 = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={path}");
        req2.Content = new ByteArrayContent(content2);
        req2.Headers.Add(ApiHeaders.IfMatch, $"\"{hash1}\"");
        var res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var fileDto2 = await res2.Content.ReadFromJsonAsync<FileDto>();
        Assert.NotNull(fileDto2);
        Assert.Equal(2, fileDto2.Version);
        Assert.Equal(hash2, fileDto2.Sha256);

        // 3. Update with stale If-Match (hash1 instead of hash2) -> 409 Conflict
        var content3 = "Stale Update"u8.ToArray();
        using var req3 = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={path}");
        req3.Content = new ByteArrayContent(content3);
        req3.Headers.Add(ApiHeaders.IfMatch, $"\"{hash1}\"");
        var res3 = await client.SendAsync(req3);

        Assert.Equal(HttpStatusCode.Conflict, res3.StatusCode);
        var conflict = await res3.Content.ReadFromJsonAsync<ConflictDto>();
        Assert.NotNull(conflict);
        Assert.Equal(2, conflict.Server.Version);
        Assert.Equal(hash2, conflict.Server.Sha256);
    }

    [Fact]
    public async Task DeleteFile_TombstonesFile_AndEnforcesIfMatch()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-cc-3");

        var path = "conflict/delete_target.txt";
        var content = "To be deleted"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        // Upload
        using var uploadReq = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={path}");
        uploadReq.Content = new ByteArrayContent(content);
        var uploadRes = await client.SendAsync(uploadReq);
        var fileDto = await uploadRes.Content.ReadFromJsonAsync<FileDto>();
        Assert.NotNull(fileDto);

        // 1. Delete with wrong If-Match -> 409 Conflict
        using var delWrongReq = new HttpRequestMessage(HttpMethod.Delete, $"api/files/{fileDto.Id}");
        delWrongReq.Headers.Add(ApiHeaders.IfMatch, "\"wronghash123\"");
        var delWrongRes = await client.SendAsync(delWrongReq);
        Assert.Equal(HttpStatusCode.Conflict, delWrongRes.StatusCode);

        // 2. Delete with correct If-Match -> 204 NoContent
        using var delRightReq = new HttpRequestMessage(HttpMethod.Delete, $"api/files/{fileDto.Id}");
        delRightReq.Headers.Add(ApiHeaders.IfMatch, $"\"{hash}\"");
        var delRightRes = await client.SendAsync(delRightReq);
        Assert.Equal(HttpStatusCode.NoContent, delRightRes.StatusCode);

        // 3. Normal list does not include deleted file
        var listRes1 = await client.GetAsync(ApiRoutes.Files);
        var list1 = await listRes1.Content.ReadFromJsonAsync<FileListResponse>();
        Assert.DoesNotContain(list1!.Items, f => f.Id == fileDto.Id);

        // 4. List with includeDeleted=true includes it with IsDeleted = true
        var listRes2 = await client.GetAsync($"{ApiRoutes.Files}?includeDeleted=true");
        var list2 = await listRes2.Content.ReadFromJsonAsync<FileListResponse>();
        var deletedItem = list2!.Items.FirstOrDefault(f => f.Id == fileDto.Id);
        Assert.NotNull(deletedItem);
        Assert.True(deletedItem.IsDeleted);
    }
}

