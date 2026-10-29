using UrlShortener.Api.Users;

namespace UrlShortener.Tests.Users;

public class PasswordHelperTests
{
    [Fact]
    public void Hash_and_verify_roundtrip()
    {
        var hash = PasswordHelper.Hash("password123");
        Assert.True(PasswordHelper.Verify("password123", hash));
    }

    [Fact]
    public void Wrong_password_does_not_verify()
    {
        var hash = PasswordHelper.Hash("correct");
        Assert.False(PasswordHelper.Verify("wrong", hash));
    }

    [Fact]
    public void Same_password_produces_different_hashes()
    {
        var h1 = PasswordHelper.Hash("same");
        var h2 = PasswordHelper.Hash("same");
        Assert.NotEqual(h1, h2);
    }
}
