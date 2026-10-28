using System;
using FolderSyncEr.Client.Sync.Common;
using Xunit;

namespace FolderSyncEr.Client.Sync.Tests;

public class HashConverterTests
{
    [Fact]
    public void HexToBase64_And_Base64ToHex_Roundtrip()
    {
        // 32-byte SHA-256 hash in lowercase hex
        var originalHex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var base64 = HashConverter.HexToBase64(originalHex);

        Assert.NotNull(base64);
        Assert.Equal("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=", base64);

        var roundtrippedHex = HashConverter.Base64ToHex(base64);
        Assert.Equal(originalHex, roundtrippedHex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-hex!")]
    public void InvalidHex_ReturnsNull(string? input)
    {
        var result = HashConverter.HexToBase64(input);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-base64!")]
    public void InvalidBase64_ReturnsNull(string? input)
    {
        var result = HashConverter.Base64ToHex(input);
        Assert.Null(result);
    }
}

