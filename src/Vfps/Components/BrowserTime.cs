using Microsoft.JSInterop;

namespace Vfps.Components;

/// <summary>
/// How a point in time is shown in the UI: in the viewer's own time zone, in one format everywhere.
/// Blazor Server renders on the server, so a page starts out in UTC until the round trip to the
/// browser in <see cref="GetZoneAsync"/> has resolved the zone - an accepted brief flash, the same
/// as the dark-mode detection in wwwroot/js/themeToggle.js.
/// </summary>
public static class BrowserTime
{
    /// <summary>
    /// The browser's time zone. Falls back to UTC: a missing or unrecognized zone shouldn't break
    /// a page, just leave its times in UTC.
    /// </summary>
    public static async Task<TimeZoneInfo> GetZoneAsync(IJSRuntime js, ILogger logger)
    {
        try
        {
            var ianaId = await js.InvokeAsync<string>("vfpsTimeZone.get");
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (Exception ex) when (ex is JSException or TimeZoneNotFoundException)
        {
            logger.LogWarning(ex, "Failed to resolve the browser's time zone, defaulting to UTC");
            return TimeZoneInfo.Utc;
        }
    }

    public static string Format(DateTimeOffset timestamp, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(timestamp, zone).ToString("yyyy-MM-dd HH:mm:ss");
}
