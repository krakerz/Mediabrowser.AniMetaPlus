using AniMetaPlus.Configuration;
using Xunit;

namespace AniMetaPlus.Tests;

public class EpisodeTitlePreferenceTests
{
    [Fact]
    public void EmptyFollowsTheShowTitles()
    {
        var configuration = new PluginConfiguration { TitlePreference = TitlePreferenceType.JapaneseRomaji };

        Assert.Equal(TitlePreferenceType.JapaneseRomaji, configuration.EpisodeTitles);
    }

    [Fact]
    public void SetValueOverridesTheShowTitles()
    {
        var configuration = new PluginConfiguration { TitlePreference = TitlePreferenceType.JapaneseRomaji, EpisodeTitlePreference = "Localized" };

        Assert.Equal(TitlePreferenceType.Localized, configuration.EpisodeTitles);
    }

    [Fact]
    public void UnknownValueFollowsTheShowTitles()
    {
        var configuration = new PluginConfiguration { TitlePreference = TitlePreferenceType.Japanese, EpisodeTitlePreference = "Klingon" };

        Assert.Equal(TitlePreferenceType.Japanese, configuration.EpisodeTitles);
    }
}
