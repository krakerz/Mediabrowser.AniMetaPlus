// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB metadata provider for people.
/// </summary>
/// <param name="paths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
public class AniDbPersonProvider(IApplicationPaths paths) : IRemoteMetadataProvider<Person, PersonLookupInfo>
{
    private readonly IApplicationPaths _paths = paths;

    /// <inheritdoc />
    public string Name => ProviderNames.Display;

    /// <inheritdoc />
    public Task<MetadataResult<Person>> GetMetadata(PersonLookupInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Person>();

        if (!string.IsNullOrEmpty(info.ProviderIds.GetValueOrDefault(ProviderNames.AniDb)))
        {
            return Task.FromResult(result);
        }

        var person = AniDbSeriesProvider.GetPersonInfo(Core.PluginPaths.CacheFolder(_paths), info.Name);
        if (!string.IsNullOrEmpty(person?.Id))
        {
            result.Item = new Person();
            result.HasMetadata = true;

            result.Item.SetProviderId(ProviderNames.AniDb, person.Id);
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(PersonLookupInfo searchInfo, CancellationToken cancellationToken)
    {
        return Task.FromResult(Enumerable.Empty<RemoteSearchResult>());
    }

    /// <inheritdoc />
    public Task<HostImageResponse> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}
