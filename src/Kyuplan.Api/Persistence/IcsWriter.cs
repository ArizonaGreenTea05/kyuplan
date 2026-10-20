using System.Globalization;
using System.Text;

namespace Kyuplan.Api.Persistence;

/// <summary>One entry of a calendar: either a point in time (<paramref name="Start"/>, <paramref name="End"/>) or a whole day (<paramref name="Day"/>).</summary>
public sealed record IcsEntry(string Uid, string Summary, DateTimeOffset? Start, DateTimeOffset? End, DateOnly? Day, bool Cancelled);

/// <summary>Writes entries as an iCalendar file (RFC 5545) that calendar apps can subscribe to. Times are written in UTC, so no time zone definition is needed.</summary>
public static class IcsWriter
{
    private const int MaxLineOctets = 75;

    public static string Write(string calendarName, IEnumerable<IcsEntry> entries, DateTimeOffset now)
    {
        var text = new StringBuilder();
        Line(text, "BEGIN:VCALENDAR");
        Line(text, "VERSION:2.0");
        Line(text, "PRODID:-//kyuplan//Calendar feed//EN");
        Line(text, "CALSCALE:GREGORIAN");
        Line(text, "METHOD:PUBLISH");
        Line(text, "NAME:" + Escape(calendarName));
        Line(text, "X-WR-CALNAME:" + Escape(calendarName));
        Line(text, "REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        Line(text, "X-PUBLISHED-TTL:PT1H");

        foreach (var entry in entries)
        {
            Line(text, "BEGIN:VEVENT");
            Line(text, "UID:" + entry.Uid);
            Line(text, "DTSTAMP:" + Utc(now));
            if (entry.Day is { } day)
            {
                Line(text, "DTSTART;VALUE=DATE:" + Date(day));
                Line(text, "DTEND;VALUE=DATE:" + Date(day.AddDays(1)));
                Line(text, "TRANSP:TRANSPARENT");
            }
            else
            {
                var start = entry.Start!.Value;
                Line(text, "DTSTART:" + Utc(start));
                Line(text, "DTEND:" + Utc(entry.End is { } end && end > start ? end : start));
            }
            Line(text, "SUMMARY:" + Escape(entry.Summary));
            Line(text, "STATUS:" + (entry.Cancelled ? "CANCELLED" : "CONFIRMED"));
            Line(text, "END:VEVENT");
        }

        Line(text, "END:VCALENDAR");
        return text.ToString();
    }

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Date(DateOnly value) => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value
        .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

    /// <summary>Ends the line with CRLF and folds it so that no line is longer than 75 octets, never inside a character.</summary>
    private static void Line(StringBuilder text, string line)
    {
        var octets = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > MaxLineOctets)
            {
                text.Append("\r\n ");
                octets = 1;
            }
            text.Append(rune.ToString());
            octets += size;
        }
        text.Append("\r\n");
    }
}
