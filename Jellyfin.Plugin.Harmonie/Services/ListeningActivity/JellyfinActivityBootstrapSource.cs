using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Data.Enums;
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
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            IsPlayed = true,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
        });

        foreach (var item in items)
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
                DateTimeOffset.UtcNow));
        }
    }

    private IEnumerable<User> GetUsers() => JellyfinCompat.GetUsers(_userManager);

    private static DateTimeOffset ToUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return new DateTimeOffset(value, TimeSpan.Zero);
        }

        return new DateTimeOffset(value).ToUniversalTime();
    }
}
