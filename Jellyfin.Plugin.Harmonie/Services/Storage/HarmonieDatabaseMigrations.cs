using System.Collections.Generic;
using Jellyfin.Plugin.Harmonie.Services.Storage.Migrations;

namespace Jellyfin.Plugin.Harmonie.Services.Storage;

internal static class HarmonieDatabaseMigrations
{
    internal static IReadOnlyList<IHarmonieDatabaseMigration> All { get; } =
        new IHarmonieDatabaseMigration[]
        {
            new Migration001ListeningActivity(),
            new Migration002RecommendationMetricsIndex(),
            new Migration003PlaybackSessionCheckpoints(),
        };
}
