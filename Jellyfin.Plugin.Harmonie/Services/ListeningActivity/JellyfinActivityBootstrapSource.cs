using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Data.Enums;
#if NET8_0
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
#endif
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Harmonie.Services.ListeningActivity;

internal interface IListeningActivityBootstrapSource
{
    IReadOnlyList<ListeningActivityBootstrapRecord> Read(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the aggregate per-user activity that Jellyfin already has. The
/// resulting snapshot is imported once; later plays are recorded as events.
/// </summary>
internal sealed class JellyfinActivityBootstrapSource : IListeningActivityBootstrapSource
{
    private const int PageSize = 500;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;

    public JellyfinActivityBootstrapSource(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager)
    {
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
    }

    public IReadOnlyList<ListeningActivityBootstrapRecord> Read(
        CancellationToken cancellationToken)
    {
        var records = new List<ListeningActivityBootstrapRecord>();
        foreach (var user in GetUsers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadUser(user, records, cancellationToken);
        }

        return records;
    }

    private void ReadUser(
        User user,
        List<ListeningActivityBootstrapRecord> records,
        CancellationToken cancellationToken)
    {
        var startIndex = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsPlayed = true,
                Recursive = true,
                StartIndex = startIndex,
                Limit = PageSize,
            });
            if (page.Count == 0)
            {
                return;
            }

            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is not Audio audio)
                {
                    continue;
                }

                var data = _userDataManager.GetUserData(user, audio);
                if (data?.LastPlayedDate is null || data.PlayCount <= 0)
                {
                    continue;
                }

                records.Add(new ListeningActivityBootstrapRecord(
                    user.Id,
                    audio.Id,
                    ToUtc(data.LastPlayedDate.Value),
                    data.PlayCount,
                    data.IsFavorite));
            }

            startIndex += page.Count;
            if (page.Count < PageSize)
            {
                return;
            }
        }
    }

    private IEnumerable<User> GetUsers()
    {
#if NET8_0
        return _userManager.Users;
#else
        return _userManager.GetUsers();
#endif
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return new DateTimeOffset(value, TimeSpan.Zero);
        }

        return new DateTimeOffset(value).ToUniversalTime();
    }
}
