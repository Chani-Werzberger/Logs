using LogsPlatform.Infrastructure;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentHashes()
    {
        var first = PasswordHasher.Hash("correct horse battery staple");
        var second = PasswordHasher.Hash("correct horse battery staple");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.False(PasswordHasher.Verify("wrong password", hash));
    }

    [Fact]
    public void Hash_NeverEqualsThePlaintextPassword()
    {
        var password = "correct horse battery staple";
        var hash = PasswordHasher.Hash(password);

        Assert.NotEqual(password, hash);
        Assert.DoesNotContain(password, hash);
    }
}
