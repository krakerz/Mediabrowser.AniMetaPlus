using System.IO;
using System.Xml;
using AniMetaPlus.Providers.AniDB.Metadata;
using Xunit;

namespace AniMetaPlus.Tests;

public class AnnIdTests
{
    private static string? Read(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml));
        reader.MoveToContent();
        return AniDbSeriesProvider.ParseAnnId(reader);
    }

    // AniDB lists Anime News Network as resource type 1 (Sword Art Online: 13858), before MyAnimeList's type 2.
    [Fact]
    public void ReadsTheAnnIdAniDbLinks()
        => Assert.Equal("13858", Read("""
            <resources>
              <resource type="1"><externalentity><identifier>13858</identifier></externalentity></resource>
              <resource type="2"><externalentity><identifier>11757</identifier></externalentity></resource>
            </resources>
            """));

    [Theory]
    [InlineData("<resources><resource type=\"2\"><externalentity><identifier>11757</identifier></externalentity></resource></resources>")]
    [InlineData("<resources><resource type=\"1\"><externalentity><identifier>abc</identifier></externalentity></resource></resources>")]
    [InlineData("<resources />")]
    public void ReadsNoneWhereAniDbLinksNoAnnEntry(string xml)
        => Assert.Null(Read(xml));
}
