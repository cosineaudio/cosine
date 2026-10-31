using System.Reflection;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class UserServiceTests
{
    [Fact]
    public void SubsonicSecret_Is32BytesHex()
    {
        // Access the private static method via reflection
        var method = typeof(UserService).GetMethod(
            "GenerateSubsonicSecret",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var secret = (string)method!.Invoke(null, null)!;

        // 32 bytes = 64 hex chars
        Assert.Equal(64, secret.Length);
        Assert.True(secret.All(c => "0123456789abcdef".Contains(c)));
    }

    [Fact]
    public void SubsonicSecret_GeneratesUniqueValues()
    {
        var method = typeof(UserService).GetMethod(
            "GenerateSubsonicSecret",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var secrets = new HashSet<string>();
        for (int i = 0; i < 100; i++)
        {
            var secret = (string)method!.Invoke(null, null)!;
            Assert.True(secrets.Add(secret), "Generated duplicate secret");
        }
    }

    [Fact]
    public void Md5_ProducesCorrectHash()
    {
        var method = typeof(UserService).GetMethod(
            "ComputeMd5",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, new object[] { "test" })!;

        // MD5("test") = 098f6bcd4621d373cade4e832627b4f6
        Assert.Equal("098f6bcd4621d373cade4e832627b4f6", result);
    }

    [Fact]
    public void SubsonicSecret_HasValidFormat()
    {
        var method = typeof(UserService).GetMethod(
            "GenerateSubsonicSecret",
            BindingFlags.NonPublic | BindingFlags.Static);

        var secret = (string)method!.Invoke(null, null)!;

        // Should be lowercase hex
        Assert.Matches("^[0-9a-f]{64}$", secret);
    }
}
