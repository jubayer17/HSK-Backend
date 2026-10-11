namespace HousekeepingParser.Core;

/// <summary>Business dates and times are German local time (C10); storage is UTC.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public static class Berlin
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);

    public static DateOnly Today(this IClock clock) => DateOnly.FromDateTime(ToLocal(clock.UtcNow));

    public static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);
}
