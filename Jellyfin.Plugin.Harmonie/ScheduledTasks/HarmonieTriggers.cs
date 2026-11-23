using System;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Harmonie.ScheduledTasks;

/// <summary>
/// Factory helpers for <see cref="TaskTriggerInfo"/> values. Localises
/// the Jellyfin 10.10 (string-based <c>Type</c>) vs 10.11+ (enum-based
/// <c>Type</c>) split so each scheduled task can declare its triggers
/// in one line without an <c>#if</c> block.
/// </summary>
internal static class HarmonieTriggers
{
    /// <summary>
    /// Fires once per day at the given wall-clock time of day.
    /// </summary>
    public static TaskTriggerInfo Daily(TimeSpan timeOfDay) => new()
    {
#if NET8_0
        Type = TaskTriggerInfo.TriggerDaily,
#else
        Type = TaskTriggerInfoType.DailyTrigger,
#endif
        TimeOfDayTicks = timeOfDay.Ticks,
    };

    /// <summary>
    /// Fires once per week on the given weekday at the given time of day.
    /// </summary>
    public static TaskTriggerInfo Weekly(DayOfWeek dayOfWeek, TimeSpan timeOfDay) => new()
    {
#if NET8_0
        Type = TaskTriggerInfo.TriggerWeekly,
#else
        Type = TaskTriggerInfoType.WeeklyTrigger,
#endif
        DayOfWeek = dayOfWeek,
        TimeOfDayTicks = timeOfDay.Ticks,
    };

    /// <summary>
    /// Fires repeatedly with the given interval between runs.
    ///
    /// Jellyfin re-arms this timer at every server start to
    /// <c>max(last run end, now) + interval</c>, so a server that restarts
    /// more often than the interval never fires it. Only use it for
    /// intervals well below the typical uptime; longer cadences belong
    /// on <see cref="Daily"/> or <see cref="Weekly"/>.
    /// </summary>
    public static TaskTriggerInfo Interval(TimeSpan interval) => new()
    {
#if NET8_0
        Type = TaskTriggerInfo.TriggerInterval,
#else
        Type = TaskTriggerInfoType.IntervalTrigger,
#endif
        IntervalTicks = interval.Ticks,
    };

    /// <summary>
    /// Fires once at server startup.
    /// </summary>
    public static TaskTriggerInfo Startup() => new()
    {
#if NET8_0
        Type = TaskTriggerInfo.TriggerStartup,
#else
        Type = TaskTriggerInfoType.StartupTrigger,
#endif
    };
}
