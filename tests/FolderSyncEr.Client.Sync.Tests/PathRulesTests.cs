using System;
using FolderSyncEr.Contracts;
using Xunit;

namespace FolderSyncEr.Client.Sync.Tests;

public class PathRulesTests
{
    [Theory]
    [InlineData("docs/readme.txt", "docs/readme.txt")]
    [InlineData(@"docs\sub\file.txt", "docs/sub/file.txt")]
    [InlineData("/root/path/file.txt", "root/path/file.txt")]
    [InlineData(@"\root\path\file.txt\", "root/path/file.txt")]
    [InlineData("   spaced/path.txt   ", "spaced/path.txt")]
    public void NormalizePath_NormalizesSlashesAndTrims(string raw, string expected)
    {
        var normalized = PathRules.NormalizePath(raw);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("valid_file.txt")]
    [InlineData("sub/folder/file.pdf")]
    [InlineData("spaced folder/my document (1).docx")]
    [InlineData("deep/nested/sub/dir/archive.tar.gz")]
    [InlineData("café/résumé.txt")]
    public void TryValidatePath_AcceptsValidCrossPlatformPaths(string path)
    {
        var isValid = PathRules.TryValidatePath(path, out var normalized, out var error);
        Assert.True(isValid, $"Expected '{path}' to be valid, but got error: {error}");
        Assert.Empty(error);
        Assert.False(string.IsNullOrWhiteSpace(normalized));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryValidatePath_RejectsEmptyPaths(string? path)
    {
        var isValid = PathRules.TryValidatePath(path!, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData("foo/../bar.txt")]
    [InlineData("foo/../../etc/shadow")]
    [InlineData(".")]
    [InlineData("./local.txt")]
    [InlineData("foo/./bar.txt")]
    public void TryValidatePath_RejectsPathTraversal(string path)
    {
        var isValid = PathRules.TryValidatePath(path, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("traversal", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("foo//bar.txt")]
    [InlineData("///")]
    [InlineData("a/ /b.txt")]
    public void TryValidatePath_RejectsEmptyOrConsecutiveSlashSegments(string path)
    {
        var isValid = PathRules.TryValidatePath(path, out _, out var error);
        Assert.False(isValid);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("PRN.doc")]
    [InlineData("aux")]
    [InlineData("sub/NUL")]
    [InlineData("folder/COM1.log")]
    [InlineData("COM9")]
    [InlineData("LPT1.txt")]
    [InlineData("LPT8")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    public void TryValidatePath_RejectsWindowsReservedNames(string path)
    {
        var isValid = PathRules.TryValidatePath(path, out _, out var error);
        Assert.False(isValid, $"Expected '{path}' to be rejected due to reserved name.");
        Assert.Contains("reserved", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("file<name>.txt")]
    [InlineData("file>name.txt")]
    [InlineData("file:stream.txt")]
    [InlineData("quote\"name.txt")]
    [InlineData("pipe|name.txt")]
    [InlineData("query?name.txt")]
    [InlineData("wild*card.txt")]
    public void TryValidatePath_RejectsIllegalFilenameCharacters(string path)
    {
        var isValid = PathRules.TryValidatePath(path, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("illegal characters", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("foo\0bar.txt")]
    [InlineData("foo\nbar.txt")]
    [InlineData("foo\rbar.txt")]
    [InlineData("foo\tbar.txt")]
    [InlineData("foo\u001Fbar.txt")]
    public void TryValidatePath_RejectsControlCharacters(string path)
    {
        var isValid = PathRules.TryValidatePath(path, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("control characters", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("folder./file.txt")]
    [InlineData("folder /file.txt")]
    [InlineData("file.")]
    [InlineData("file ")]
    public void TryValidatePath_RejectsTrailingPeriodOrSpaceInSegments(string path)
    {
        var isValid = PathRules.TryValidatePath(path, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("period or space", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidatePath_RejectsPathsExceeding1024Characters()
    {
        var longPath = new string('a', 1025);
        var isValid = PathRules.TryValidatePath(longPath, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("maximum length", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToLookupKey_ReturnsConsistentUppercaseInvariant()
    {
        var key1 = PathRules.ToLookupKey("MyFolder/Report.txt");
        var key2 = PathRules.ToLookupKey("myfolder\\report.TXT");
        Assert.Equal("MYFOLDER/REPORT.TXT", key1);
        Assert.Equal(key1, key2);
    }
}
