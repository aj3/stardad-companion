using StarDexs.Sync.Core;
using Xunit;

namespace StarDexs.Sync.Tests;

public class ScheduleTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-6), "Test", "Test");
    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(-6));

    [Fact]
    public void New_links_default_to_three_hours_but_saved_off_and_custom_schedules_survive()
    {
        using var install = new FakeInstall();
        var store = new StateStore(install.Data, new FakeProtector());
        var state = store.Load();
        store.Link(state, "aaaaaaaaaaaaaaaa", "Pilot", "token");
        var linked = state.Accounts["aaaaaaaaaaaaaaaa"];
        Assert.Equal(SyncSchedule.Default, linked.Schedule);
        Assert.NotNull(linked.ScheduleSince);
        Assert.False(linked.Schedule!.IsDue(linked.ScheduleSince!.Value, linked.ScheduleSince.Value.AddHours(3).AddSeconds(-1)));
        Assert.True(linked.Schedule.IsDue(linked.ScheduleSince.Value, linked.ScheduleSince.Value.AddHours(3)));
        linked.Schedule = null;
        store.Save(state);
        state = store.Load();
        store.Link(state, "aaaaaaaaaaaaaaaa", "Pilot", "new-token");
        Assert.Null(state.Accounts["aaaaaaaaaaaaaaaa"].Schedule);
        state.Accounts["aaaaaaaaaaaaaaaa"].Schedule = new SyncSchedule(6, null, false);
        store.Save(state);
        state = store.Load();
        store.Link(state, "aaaaaaaaaaaaaaaa", "Pilot", "new-token");
        Assert.Equal(6, state.Accounts["aaaaaaaaaaaaaaaa"].Schedule!.EveryHours);
    }

    [Fact]
    public void Every_few_hours_counts_from_the_last_sync()
    {
        var schedule = new SyncSchedule(6, null, false);
        Assert.Equal(At(26, 15), schedule.Next(At(26, 9), Zone));
        Assert.False(schedule.IsDue(At(26, 9), At(26, 14, 59), Zone));
        Assert.True(schedule.IsDue(At(26, 9), At(26, 15), Zone));
    }

    [Fact]
    public void Once_a_day_runs_at_the_chosen_time_on_this_PCs_clock()
    {
        var schedule = new SyncSchedule(24, 18 * 60, false);
        Assert.Equal(At(26, 18), schedule.Next(At(26, 9), Zone));
        Assert.Equal(At(27, 18), schedule.Next(At(26, 18), Zone)); // already ran today
        Assert.True(schedule.IsDue(At(24, 9), At(26, 7), Zone)); // PC was off at 6 PM: runs once when it's back
    }

    [Fact]
    public void Only_the_websites_choices_are_accepted()
    {
        Assert.NotNull(SyncSchedule.From(12, null, false));
        Assert.Null(SyncSchedule.From(1, null, false));
        Assert.Null(SyncSchedule.From(24, 24 * 60, false));
        Assert.Null(SyncSchedule.From(6, 30, false)!.AtMinute); // a time only means something for once a day
        Assert.Null(new SyncSchedule(0, null, true).Next(At(26, 9), Zone)); // game updates only: no timed sync
    }

    [Fact]
    public void Says_what_it_does()
    {
        Assert.Equal("Every 6 hours", new SyncSchedule(6, null, false).Describe());
        Assert.Equal("Every day at 6:00 PM, and when the game updates", new SyncSchedule(24, 18 * 60, true).Describe());
        Assert.Equal("Every day at 7:05 AM", new SyncSchedule(24, 7 * 60 + 5, false).Describe());
        Assert.Equal("When the game updates", new SyncSchedule(0, null, true).Describe());
    }
}
