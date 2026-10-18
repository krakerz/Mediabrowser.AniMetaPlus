using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using Xunit;

namespace AniMetaPlus.Tests;

public class FranchisesTests
{
    private static Func<string, IReadOnlyList<(string Id, string Type)>?> Relations(params (string From, string To, string Type)[] edges)
        => id => edges.Where(e => e.From == id).Select(e => (e.To, e.Type)).ToList();

    private static Dictionary<string, FranchiseNode> Library(params (string Id, string Name, int? Year)[] items)
        => items.ToDictionary(i => i.Id, i => new FranchiseNode(i.Name, i.Year == null ? null : new DateTime(i.Year.Value, 1, 1)));

    [Fact]
    public void JoinsAChainThroughAnAnimeOutsideTheLibrary()
    {
        // 1 -> 2 (not in the library, cached) -> 3.
        var library = Library(("1", "First", 2010), ("3", "Third", 2014));

        var found = Franchises.Group(library, Relations(("1", "2", "Sequel"), ("2", "3", "Sequel")));

        var franchise = Assert.Single(found);
        Assert.Equal("1", franchise.Key);
        Assert.Equal(["1", "3"], franchise.AnimeIds);
    }

    [Fact]
    public void JoinsThroughTheReverseRelationAlone()
    {
        // Only the sequel's answer is cached, and it names the prequel.
        var library = Library(("1", "First", 2010), ("2", "Second", 2012));

        var found = Franchises.Group(library, Relations(("2", "1", "Prequel")));

        Assert.Equal(["1", "2"], Assert.Single(found).AnimeIds);
    }

    [Theory]
    [InlineData("Sequel")]
    [InlineData("Prequel")]
    [InlineData("Side Story")]
    [InlineData("Parent Story")]
    [InlineData("Summary")]
    [InlineData("Full Story")]
    public void JoinsOnTheseRelations(string type)
    {
        var found = Franchises.Group(Library(("1", "A", 2010), ("2", "B", 2011)), Relations(("1", "2", type)));

        Assert.Single(found);
    }

    [Theory]
    [InlineData("Same Setting")]
    [InlineData("Alternative Setting")]
    [InlineData("Alternative Version")]
    [InlineData("Character")]
    [InlineData("Other")]
    public void IgnoresTheOtherRelations(string type)
    {
        var found = Franchises.Group(Library(("1", "A", 2010), ("2", "B", 2011)), Relations(("1", "2", type)));

        Assert.Empty(found);
    }

    [Fact]
    public void ALoneLibraryAnimeIsNoFranchise()
    {
        // Two related anime, but only one of them is in the library.
        var found = Franchises.Group(Library(("1", "A", 2010)), Relations(("1", "2", "Sequel"), ("2", "3", "Sequel")));

        Assert.Empty(found);
    }

    [Fact]
    public void KeepsSeparateFranchisesApart()
    {
        var library = Library(("1", "A", 2010), ("2", "B", 2011), ("10", "C", 2010), ("20", "D", 2011));

        var found = Franchises.Group(library, Relations(("1", "2", "Sequel"), ("10", "20", "Sequel")));

        Assert.Equal(["1", "10"], found.Select(f => f.Key));
    }

    [Fact]
    public void KeysByTheLowestIdOfTheWholeComponentCountedByNumber()
    {
        var library = Library(("100", "A", 2010), ("99", "B", 2011));

        var found = Franchises.Group(library, Relations(("100", "99", "Sequel")));

        Assert.Equal("99", Assert.Single(found).Key);
    }

    [Fact]
    public void NamesTheFranchiseAfterTheEarliestPremiere()
    {
        var library = Library(("1", "Sequel", 2015), ("2", "Original", 2010), ("3", "Movie", 2012));

        var found = Franchises.Group(library, Relations(("1", "2", "Prequel"), ("2", "3", "Sequel")));

        Assert.Equal("Original", Assert.Single(found).Name);
    }

    [Fact]
    public void NamesByTheLowestIdWithoutPremiereDates()
    {
        var library = Library(("7", "Seven", null), ("5", "Five", null));

        var found = Franchises.Group(library, Relations(("5", "7", "Sequel")));

        Assert.Equal("Five", Assert.Single(found).Name);
    }

    [Fact]
    public void ADatedMemberBeatsAnUndatedOneWithALowerId()
    {
        var library = Library(("1", "Undated", null), ("2", "Dated", 2010));

        var found = Franchises.Group(library, Relations(("1", "2", "Sequel")));

        Assert.Equal("Dated", Assert.Single(found).Name);
    }

    [Fact]
    public void ReadsOnlyTheRelatedAnimeOfACachedAnswer()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """
                <anime id="1">
                  <similaranime><anime id="9" approval="1" total="2">Similar</anime></similaranime>
                  <relatedanime>
                    <anime id="4" type="Sequel">Second</anime>
                    <anime id="5" type="Other">Else</anime>
                  </relatedanime>
                  <characters><anime id="8" type="Sequel" /></characters>
                </anime>
                """);

            var relations = Franchises.ReadRelations(file);

            Assert.Equal([("4", "Sequel"), ("5", "Other")], relations);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ReadsNothingFromABrokenFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "<anime id=\"1\"><relatedanime>");

            Assert.Null(Franchises.ReadRelations(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task MakesACollectionThenUpdatesTheSameOneOnTheNextRun()
    {
        var host = new FakeCollections();
        var first = await Franchises.Sync(host, [new("1", "Show", ["a", "b"])], [], null, CancellationToken.None);

        Assert.Equal(1, first.Created);
        Assert.Equal(2, first.Added);

        var second = await Franchises.Sync(host, [new("1", "Show", ["a", "b", "c"])], first.Records, null, CancellationToken.None);

        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Updated);
        Assert.Equal(1, second.Added);
        Assert.Single(host.Collections);
        Assert.Equal(["a", "b", "c"], host.Collections.Values.Single().Members.Order());
    }

    [Fact]
    public async Task TakesOutTheItemsThatLeftAndKeepsTheOnesTheOwnerAdded()
    {
        var host = new FakeCollections();
        var first = await Franchises.Sync(host, [new("1", "Show", ["a", "b"])], [], null, CancellationToken.None);
        host.Collections.Values.Single().Members.Add("mine");

        var second = await Franchises.Sync(host, [new("1", "Show", ["a"])], first.Records, null, CancellationToken.None);

        Assert.Equal(1, second.Removed);
        Assert.Equal(["a", "mine"], host.Collections.Values.Single().Members.Order());
    }

    [Fact]
    public async Task MakesTheCollectionAgainWhenTheOwnerDeletedIt()
    {
        var host = new FakeCollections();
        var first = await Franchises.Sync(host, [new("1", "Show", ["a", "b"])], [], null, CancellationToken.None);
        host.Collections.Clear();

        var second = await Franchises.Sync(host, [new("1", "Show", ["a", "b"])], first.Records, null, CancellationToken.None);

        Assert.Equal(1, second.Created);
        Assert.Single(host.Collections);
        Assert.Single(second.Records);
    }

    [Fact]
    public async Task DoesNotRenameWhatTheOwnerRenamed()
    {
        var host = new FakeCollections();
        var first = await Franchises.Sync(host, [new("1", "Show", ["a", "b"])], [], null, CancellationToken.None);
        host.Collections.Values.Single().Name = "My name";

        var second = await Franchises.Sync(host, [new("1", "Other", ["a", "b"])], first.Records, null, CancellationToken.None);

        Assert.Equal("My name", host.Collections.Values.Single().Name);
        Assert.Equal(0, second.Updated);
    }

    [Fact]
    public async Task RenamesWhenTheNameIsStillTheOneItSet()
    {
        var host = new FakeCollections();
        var first = await Franchises.Sync(host, [new("1", "Show", ["a", "b"])], [], null, CancellationToken.None);

        await Franchises.Sync(host, [new("1", "Other", ["a", "b"])], first.Records, null, CancellationToken.None);

        Assert.Equal("Other", host.Collections.Values.Single().Name);
    }

    [Fact]
    public async Task KeepsTheCollectionWhenTheKeyMovesAndLeavesAGoneFranchiseAlone()
    {
        var host = new FakeCollections();
        var first = await Franchises.Sync(host, [new("5", "Show", ["a", "b"]), new("9", "Gone", ["x", "y"])], [], null, CancellationToken.None);

        // A lower id joined the first franchise; the second franchise is no more.
        var second = await Franchises.Sync(host, [new("2", "Show", ["a", "b", "c"])], first.Records, null, CancellationToken.None);

        Assert.Equal(0, second.Created);
        Assert.Equal(2, host.Collections.Count);
        Assert.Equal(["2", "9"], second.Records.Select(r => r.Id).Order());
        Assert.Equal(["x", "y"], host.Collections[second.Records.Single(r => r.Id == "9").CollectionId].Members.Order());
    }

    private sealed class FakeCollection
    {
        public string Name { get; set; } = string.Empty;

        public List<string> Members { get; } = [];
    }

    private sealed class FakeCollections : IHostCollections
    {
        private int _next;

        public Dictionary<string, FakeCollection> Collections { get; } = [];

        public HostCollection? FindCollection(string collectionId)
            => Collections.TryGetValue(collectionId, out var c) ? new HostCollection(c.Name, c.Members.ToList()) : null;

        public Task<string> CreateCollection(string name, IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
        {
            var id = "c" + _next++;
            var collection = new FakeCollection { Name = name };
            collection.Members.AddRange(itemIds);
            Collections[id] = collection;

            return Task.FromResult(id);
        }

        public Task AddToCollection(string collectionId, IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
        {
            Collections[collectionId].Members.AddRange(itemIds);

            return Task.CompletedTask;
        }

        public Task RemoveFromCollection(string collectionId, IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
        {
            Collections[collectionId].Members.RemoveAll(itemIds.Contains);

            return Task.CompletedTask;
        }

        public Task RenameCollection(string collectionId, string name, CancellationToken cancellationToken)
        {
            Collections[collectionId].Name = name;

            return Task.CompletedTask;
        }
    }
}
