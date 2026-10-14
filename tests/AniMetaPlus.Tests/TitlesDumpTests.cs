using System;
using System.IO;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using AniMetaPlus.Titles;
using Xunit;

namespace AniMetaPlus.Tests;

public class TitlesDumpTests
{
    [Theory]
    [InlineData("23|1|x-jat|Cowboy Bebop", "23", "main", "x-jat", "Cowboy Bebop")]
    [InlineData("9541|3|x-jat|shingeki", "9541", "short", "x-jat", "shingeki")]
    [InlineData("17715|4|ja|-禅- グローグー", "17715", "official", "ja", "-禅- グローグー")]
    [InlineData("100|2|en|A | B | C", "100", "syn", "en", "A | B | C")]
    [InlineData("100|6|en|Card", "100", "card", "en", "Card")]
    public void ParsesDatLines(string line, string aid, string type, string language, string title)
    {
        Assert.True(TitlesDump.TryParseDatLine(line, out var a, out var t, out var l, out var n));
        Assert.Equal(aid, a);
        Assert.Equal(type, t);
        Assert.Equal(language, l);
        Assert.Equal(title, n);
    }

    [Theory]
    [InlineData("# created: Tue Oct 13 03:00:02 2026")]
    [InlineData("")]
    [InlineData("abc|1|en|Title")]
    [InlineData("1|1|en")]
    [InlineData("1|1|en|")]
    public void SkipsCommentsAndMalformedLines(string line)
        => Assert.False(TitlesDump.TryParseDatLine(line, out _, out _, out _, out _));

    [Fact]
    public async Task ConvertsDatToTheXmlLayoutAndGroupsByAnime()
    {
        const string dat = "# header\n2|1|x-jat|Second\n1|1|x-jat|Tom & Jerry <TV>\n2|4|en|Second EN\n1|2|en|Q\"uote\n";
        using var input = new StringReader(dat);
        using var output = new StringWriter();

        var count = await TitlesDump.ConvertDat(input, output, CancellationToken.None);

        Assert.Equal(2, count);

        var doc = XDocument.Parse(output.ToString());
        var anime = doc.Root!.Elements("anime");
        Assert.Collection(
            anime,
            first =>
            {
                Assert.Equal("2", first.Attribute("aid")!.Value);
                Assert.Equal(2, Assert.IsAssignableFrom<System.Collections.Generic.IEnumerable<XElement>>(first.Elements("title")).Count());
            },
            second =>
            {
                Assert.Equal("1", second.Attribute("aid")!.Value);
                Assert.Contains(second.Elements("title"), t => t.Value == "Tom & Jerry <TV>" && t.Attribute("type")!.Value == "main");
                Assert.Contains(second.Elements("title"), t => t.Value == "Q\"uote" && t.Attribute(XNamespace.Xml + "lang")!.Value == "en");
            });
    }

    [Fact]
    public async Task ImportsTheLocalDumpWhenPresent()
    {
        // The owner's own copy of the dump. Read only, never downloaded: AniDB bans repeat
        // downloads of it.
        var dump = FindLocalDump();
        if (dump is null)
        {
            Assert.Skip("anidb-data/anime-titles.dat.gz is not present");
        }

        var target = Path.Combine(Path.GetTempPath(), "animetaplus-test-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var count = await TitlesDump.Import(dump, target, CancellationToken.None);

            Assert.True(count > 10000, $"only {count} anime imported");

            var doc = XDocument.Load(target);
            var bebop = Assert.Single(doc.Root!.Elements("anime"), a => a.Attribute("aid")!.Value == "23");
            Assert.Contains(bebop.Elements("title"), t => t.Value == "Cowboy Bebop" && t.Attribute("type")!.Value == "main");
        }
        finally
        {
            File.Delete(target);
        }
    }

    private static string? FindLocalDump()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "anidb-data", "anime-titles.dat.gz");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
