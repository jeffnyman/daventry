using Daventry.Core.Storage;

namespace Daventry.Core.Tests;

/// <summary>The managed MD5 the detection uses in the browser agrees with the runtime's on every block boundary.</summary>
public sealed class Md5Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(119)]
    [InlineData(5000)]
    [InlineData(65536)]
    public void MatchesTheRuntimeMd5(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);

        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data)), Md5.ComputeHex(data));
    }

    [Fact]
    public void KnowsTheReferenceVectors()
    {
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", Md5.ComputeHex([]));
        Assert.Equal("9e107d9d372bb6826bd81d3542a419d6", Md5.ComputeHex("The quick brown fox jumps over the lazy dog"u8));
    }
}
