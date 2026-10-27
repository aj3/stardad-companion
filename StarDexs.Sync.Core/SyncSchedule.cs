using System.Globalization;

namespace StarDexs.Sync.Core;

/// <summary>
/// When the player asked, in their stardad.space account, for this PC to sync on its own: every few hours, or
/// once a day at a set time on this PC's clock, and/or when the game updates. Off unless they turned it on.
/// </summary>
public sealed record SyncSchedule(int EveryHours, int? AtMinute, bool OnGameUpdate)
{
    /// <summary>The choices the website offers (0: no timed sync).</summary>
    public static readonly int[] Hours = [0, 3, 6, 12, 24];

    public bool IsOn => EveryHours > 0 || OnGameUpdate;

    /// <summary>The schedule stardad.space sent, or null if it isn't one this app knows (then nothing runs on its own).</summary>
    public static SyncSchedule? From(int everyHours, int? atMinute, bool onGameUpdate)
    {
        if (!Hours.Contains(everyHours) || atMinute is < 0 or >= 24 * 60) return null;
        return new SyncSchedule(everyHours, everyHours == 24 ? atMinute : null, onGameUpdate);
    }

    /// <summary>The first timed sync after <paramref name="since"/>; null when there are no timed syncs.</summary>
    public DateTimeOffset? Next(DateTimeOffset since, TimeZoneInfo? zone = null)
    {
        if (EveryHours <= 0) return null;
        if (EveryHours != 24 || AtMinute is not { } at) return since.AddHours(EveryHours);
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(since, zone);
        var day = local.Date;
        for (var i = 0; i < 3; i++, day = day.AddDays(1))
        {
            var clock = day.AddMinutes(at);
            var slot = new DateTimeOffset(clock, zone.GetUtcOffset(clock));
            if (slot > since) return slot;
        }
        return since.AddDays(1);
    }

    /// <summary>A timed sync is due: the first one after <paramref name="since"/> has come.</summary>
    public bool IsDue(DateTimeOffset since, DateTimeOffset now, TimeZoneInfo? zone = null) => Next(since, zone) is { } next && next <= now;

    /// <summary>For the panel: "Every 6 hours", "Every day at 6:00 PM, and when the game updates".</summary>
    public string Describe()
    {
        var timed = EveryHours switch
        {
            0 => null,
            24 when AtMinute is { } at => "Every day at " + DateTime.Today.AddMinutes(at).ToString("h:mm tt", CultureInfo.InvariantCulture),
            24 => "Every day",
            _ => $"Every {EveryHours} hours",
        };
        return (timed, OnGameUpdate) switch
        {
            (null, true) => "When the game updates",
            (null, false) => "Off",
            (_, true) => timed + ", and when the game updates",
            _ => timed,
        };
    }
}
