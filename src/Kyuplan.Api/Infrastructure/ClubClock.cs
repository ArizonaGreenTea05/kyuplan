namespace Kyuplan.Api.Infrastructure;

/// <summary>
/// "Now" and day boundaries in the club's time zone. Spond delivers UTC timestamps while a training
/// that starts at 23:30 UTC belongs to the next day in Germany, so every date calculation goes through here.
/// </summary>
public sealed class ClubClock(TimeProvider time, TimeZoneInfo zone)
{
    public TimeZoneInfo Zone => zone;

    public DateTimeOffset Now => ToClub(time.GetUtcNow());

    public DateOnly Today => DateOnly.FromDateTime(Now.Date);

    public DateTimeOffset ToClub(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, zone);

    /// <summary>Midnight at the start of <paramref name="day"/> in the club time zone.</summary>
    public DateTimeOffset StartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1); // midnight skipped by a DST change
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>Builds a club-local timestamp, e.g. for a training that starts at 18:30 local time.</summary>
    public DateTimeOffset At(DateOnly day, TimeOnly clockTime)
    {
        var local = day.ToDateTime(clockTime);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
