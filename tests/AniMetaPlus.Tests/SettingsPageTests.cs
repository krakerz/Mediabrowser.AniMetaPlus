using System;
using System.IO;
using Xunit;

namespace AniMetaPlus.Tests;

public class SettingsPageTests
{
    // Web.targets writes the page through WriteLinesToFile, which on Linux turns every backslash
    // into a slash: an escaped quote in the script then breaks the whole settings page.
    [Theory]
    [InlineData("src/Shared/Web/page.core.js")]
    [InlineData("src/Shared/Web/page.body.html")]
    public void PageSourcesHoldNoBackslash(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), file));

        Assert.DoesNotContain('\\', text);
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AniMetaPlus.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
