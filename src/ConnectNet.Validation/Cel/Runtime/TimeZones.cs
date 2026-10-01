using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace ConnectNet.Validation.Cel.Runtime;

/// <summary>
/// Resolves IANA time zone names, including the legacy link names (<c>US/Central</c>,
/// <c>Japan</c>, …) that some tzdata installations no longer materialize as zone files: the
/// links are read from <c>tzdata.zi</c> when present, with a built-in table as a last resort.
/// </summary>
internal static class TimeZones
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Cache = new(StringComparer.Ordinal);
    private static readonly Lazy<Dictionary<string, string>> Links = new(LoadLinks);

    private static readonly Dictionary<string, string> BuiltInLinks = new(StringComparer.Ordinal)
    {
        ["US/Alaska"] = "America/Anchorage",
        ["US/Aleutian"] = "America/Adak",
        ["US/Arizona"] = "America/Phoenix",
        ["US/Central"] = "America/Chicago",
        ["US/East-Indiana"] = "America/Indiana/Indianapolis",
        ["US/Eastern"] = "America/New_York",
        ["US/Hawaii"] = "Pacific/Honolulu",
        ["US/Indiana-Starke"] = "America/Indiana/Knox",
        ["US/Michigan"] = "America/Detroit",
        ["US/Mountain"] = "America/Denver",
        ["US/Pacific"] = "America/Los_Angeles",
        ["US/Samoa"] = "Pacific/Pago_Pago",
        ["Canada/Atlantic"] = "America/Halifax",
        ["Canada/Central"] = "America/Winnipeg",
        ["Canada/Eastern"] = "America/Toronto",
        ["Canada/Mountain"] = "America/Edmonton",
        ["Canada/Newfoundland"] = "America/St_Johns",
        ["Canada/Pacific"] = "America/Vancouver",
        ["GB"] = "Europe/London",
        ["Japan"] = "Asia/Tokyo",
        ["Singapore"] = "Asia/Singapore",
        ["Hongkong"] = "Asia/Hong_Kong",
        ["PRC"] = "Asia/Shanghai",
        ["ROK"] = "Asia/Seoul",
        ["NZ"] = "Pacific/Auckland",
        ["Egypt"] = "Africa/Cairo",
        ["Israel"] = "Asia/Jerusalem",
        ["Turkey"] = "Europe/Istanbul",
        ["Cuba"] = "America/Havana",
        ["Iran"] = "Asia/Tehran",
        ["Poland"] = "Europe/Warsaw",
        ["Portugal"] = "Europe/Lisbon",
        ["Eire"] = "Europe/Dublin",
        ["Iceland"] = "Atlantic/Reykjavik",
        ["Jamaica"] = "America/Jamaica",
        ["Libya"] = "Africa/Tripoli",
        ["Navajo"] = "America/Denver",
        ["Mexico/General"] = "America/Mexico_City",
        ["Brazil/East"] = "America/Sao_Paulo",
        ["Chile/Continental"] = "America/Santiago",
        ["Australia/ACT"] = "Australia/Sydney",
        ["Australia/NSW"] = "Australia/Sydney",
        ["Australia/Victoria"] = "Australia/Melbourne",
        ["Australia/Queensland"] = "Australia/Brisbane",
        ["Australia/South"] = "Australia/Adelaide",
        ["Australia/West"] = "Australia/Perth",
        ["Australia/Tasmania"] = "Australia/Hobart",
        ["Australia/North"] = "Australia/Darwin",
        ["GMT"] = "UTC",
        ["Etc/UTC"] = "UTC",
        ["Zulu"] = "UTC",
        ["UCT"] = "UTC",
    };

    public static TimeZoneInfo? Find(string name)
    {
        return Cache.GetOrAdd(name, static n =>
        {
            var zone = TryFind(n);
            if (zone != null) return zone;
            if (Links.Value.TryGetValue(n, out var target) || BuiltInLinks.TryGetValue(n, out target))
                return target == "UTC" ? TimeZoneInfo.Utc : TryFind(target);
            return null;
        });
    }

    private static TimeZoneInfo? TryFind(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> LoadLinks()
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in new[] { "/usr/share/zoneinfo/tzdata.zi", "/usr/share/lib/zoneinfo/tzdata.zi" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    // Link lines: "L <target> <link-name>"
                    if (line.Length > 2 && line[0] == 'L' && line[1] == ' ')
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 3)
                            links[parts[2]] = parts[1];
                    }
                }
                break;
            }
            catch (IOException)
            {
                // fall back to the built-in table
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return links;
    }
}
