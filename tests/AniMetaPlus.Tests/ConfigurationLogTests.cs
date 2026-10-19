using AniMetaPlus.Configuration;
using Xunit;

namespace AniMetaPlus.Tests;

public class ConfigurationLogTests
{
    // The settings go to the log to match a problem against; who the owner is to a source does not.
    [Fact]
    public void LeavesClientNamesAndKeysOutOfTheLog()
    {
        var configuration = new PluginConfiguration { MalClientId = "abcdef0123456789", AniDbClientName = "secretclient", MaxGenres = 7 };

        var text = ConfigurationLog.Describe(configuration);

        Assert.DoesNotContain("abcdef0123456789", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("secretclient", text, System.StringComparison.Ordinal);
        Assert.Contains("MalClientId=(set)", text, System.StringComparison.Ordinal);
        Assert.Contains("MaxGenres=7", text, System.StringComparison.Ordinal);
        Assert.Contains("Sources=[", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"", text, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AniDbClientName", true)]
    [InlineData("MalClientId", true)]
    [InlineData("ApiKey", true)]
    [InlineData("AniDbClientVersion", false)]
    [InlineData("MaxGenres", false)]
    public void KnowsWhichSettingsIdentifyTheOwner(string name, bool sensitive)
        => Assert.Equal(sensitive, ConfigurationLog.IsSensitive(name));
}
