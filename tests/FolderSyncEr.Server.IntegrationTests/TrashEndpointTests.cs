using System.Net;
using System.Net.Http.Json;
using System.Text;
using FolderSyncEr.Contracts;

namespace FolderSyncEr.Server.IntegrationTests;

public class TrashEndpointTests : IClassFixture<TestServerFixture>
{
    private readonly TestServerFixture _fixture;

    public TrashEndpointTests(TestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DeleteFile_AppearsInTrash_AndCanBeRestoredWithContent()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-trash-1");

        var path = $"trash/restore_test_{Guid.NewGuid():N}.txt";
        var originalContent = "Restore me from the trash!";
        var uploaded = await UploadFileAsync(client, path, originalContent);

        // Soft-delete the file
        var delRes = await client.DeleteAsync($"api/files/{uploaded.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delRes.StatusCode);

        // Verify it appears in GET /api/trash
        var trashList = await client.GetFromJsonAsync<List<DeletedItemDto>>(ApiRoutes.Trash);
        Assert.NotNull(trashList);
        var deletedEntry = Assert.Single(trashList, i => i.Id == uploaded.Id);
        Assert.Equal(path, deletedEntry.Path);
        Assert.False(deletedEntry.IsDirectory);

        // Restore the file at its original path
        var restoreRes = await client.PostAsJsonAsync($"{ApiRoutes.Trash}/{uploaded.Id}/restore", new RestoreRequest(null));
        Assert.Equal(HttpStatusCode.OK, restoreRes.StatusCode);
        var restored = await restoreRes.Content.ReadFromJsonAsync<RestoreResponse>();
        Assert.NotNull(restored);
        Assert.Equal(path, restored.Path);
        Assert.Equal(1, restored.RestoredCount);

        // Verify it is no longer in trash and its content downloads intact
        var trashAfter = await client.GetFromJsonAsync<List<DeletedItemDto>>(ApiRoutes.Trash);
        Assert.DoesNotContain(trashAfter!, i => i.Id == uploaded.Id);

        var dlRes = await client.GetAsync($"api/files/{uploaded.Id}/content");
        Assert.Equal(HttpStatusCode.OK, dlRes.StatusCode);
        var downloadedText = await dlRes.Content.ReadAsStringAsync();
        Assert.Equal(originalContent, downloadedText);
    }

    [Fact]
    public async Task DeleteFolderAndContents_FoldsIntoSingleTrashItem_AndRestoresTogether()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-trash-dir");

        var folderPath = $"trash/folder_{Guid.NewGuid():N}";
        var dirRes = await client.PostAsJsonAsync(ApiRoutes.Directories, new CreateDirectoryRequest(folderPath));
        var dirDto = (await dirRes.Content.ReadFromJsonAsync<FileDto>())!;

        var f1 = await UploadFileAsync(client, $"{folderPath}/a.txt", "File A");
        var f2 = await UploadFileAsync(client, $"{folderPath}/sub/b.txt", "File B");

        // Delete the files and folder at the same time
        await client.DeleteAsync($"api/files/{f1.Id}");
        await client.DeleteAsync($"api/files/{f2.Id}");
        await client.DeleteAsync($"api/files/{dirDto.Id}");

        // Trash list folds children into the deleted folder
        var trashList = await client.GetFromJsonAsync<List<DeletedItemDto>>(ApiRoutes.Trash);
        var folderTrash = Assert.Single(trashList!, i => i.Id == dirDto.Id);
        Assert.True(folderTrash.IsDirectory);
        Assert.Equal(2, folderTrash.ContainedItemCount);
        Assert.DoesNotContain(trashList!, i => i.Id == f1.Id || i.Id == f2.Id);

        // Restoring the folder restores the folder + both files
        var restoreRes = await client.PostAsJsonAsync($"{ApiRoutes.Trash}/{dirDto.Id}/restore", new RestoreRequest(null));
        Assert.Equal(HttpStatusCode.OK, restoreRes.StatusCode);
        var restored = await restoreRes.Content.ReadFromJsonAsync<RestoreResponse>();
        Assert.Equal(3, restored!.RestoredCount);

        // Both files are active again
        var filesList = await client.GetFromJsonAsync<FileListResponse>(ApiRoutes.Files);
        Assert.Contains(filesList!.Items, f => f.Id == f1.Id && !f.IsDeleted);
        Assert.Contains(filesList.Items, f => f.Id == f2.Id && !f.IsDeleted);
    }

    [Fact]
    public async Task RestoreToCustomTargetPath_RestoresAsCopy()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-trash-copy");

        var baseName = $"trash/doc_{Guid.NewGuid():N}";
        var origPath = $"{baseName}.txt";
        var copyPath = $"{baseName} (restored 2026-10-28).txt";

        var uploaded = await UploadFileAsync(client, origPath, "Original version");
        await client.DeleteAsync($"api/files/{uploaded.Id}");

        var restoreRes = await client.PostAsJsonAsync($"{ApiRoutes.Trash}/{uploaded.Id}/restore", new RestoreRequest(copyPath));
        Assert.Equal(HttpStatusCode.OK, restoreRes.StatusCode);
        var restored = await restoreRes.Content.ReadFromJsonAsync<RestoreResponse>();
        Assert.Equal(copyPath, restored!.Path);

        var metaRes = await client.GetFromJsonAsync<FileDto>($"api/files/{uploaded.Id}");
        Assert.Equal(copyPath, metaRes!.Path);
        Assert.False(metaRes.IsDeleted);
    }

    [Fact]
    public async Task PurgeAndEmptyTrash_PermanentlyRemoveDeletedItems()
    {
        var (client, _) = await _fixture.CreateAuthenticatedClientAsync(deviceId: "dev-trash-purge");

        var f1 = await UploadFileAsync(client, $"trash/p1_{Guid.NewGuid():N}.txt", "Purge 1");
        var f2 = await UploadFileAsync(client, $"trash/p2_{Guid.NewGuid():N}.txt", "Purge 2");

        await client.DeleteAsync($"api/files/{f1.Id}");
        await client.DeleteAsync($"api/files/{f2.Id}");

        // Purge f1 individually
        var purgeRes = await client.DeleteAsync($"{ApiRoutes.Trash}/{f1.Id}");
        Assert.Equal(HttpStatusCode.OK, purgeRes.StatusCode);
        var purgeDto = await purgeRes.Content.ReadFromJsonAsync<PurgeResponse>();
        Assert.Equal(1, purgeDto!.PurgedCount);

        // Empty remaining trash
        var emptyRes = await client.DeleteAsync(ApiRoutes.Trash);
        Assert.Equal(HttpStatusCode.OK, emptyRes.StatusCode);
        var emptyDto = await emptyRes.Content.ReadFromJsonAsync<PurgeResponse>();
        Assert.True(emptyDto!.PurgedCount >= 1);

        var trashAfter = await client.GetFromJsonAsync<List<DeletedItemDto>>(ApiRoutes.Trash);
        Assert.DoesNotContain(trashAfter!, i => i.Id == f1.Id || i.Id == f2.Id);
    }

    private static async Task<FileDto> UploadFileAsync(HttpClient client, string path, string content)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiRoutes.Files}?path={Uri.EscapeDataString(path)}");
        req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        var res = await client.SendAsync(req);
        Assert.True(res.IsSuccessStatusCode, $"Upload failed with {res.StatusCode}");
        return (await res.Content.ReadFromJsonAsync<FileDto>())!;
    }
}

