using System.Globalization;

namespace ChestCounter;

/// <summary>
/// User interface language (English or Russian), chosen on first launch and saved in config.json.
/// Texts are written inline as <c>L.T("English", "русский")</c>. The names stored in data.json (chest kinds,
/// treasure items) are fixed keys from the first versions of the tool, translated when displayed (<see cref="Name"/>).
/// </summary>
public static class L
{
    /// <summary>Languages offered, in menu order.</summary>
    public static readonly string[] Codes = { "en", "ru" };

    /// <summary>"en" or "ru".</summary>
    public static string Code { get; private set; } = "en";

    public static bool Ru => Code == "ru";

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-GB");

    public static void Set(string? code)
    {
        Code = Normalize(code) ?? "en";
        Culture = CultureInfo.GetCultureInfo(T("en-GB", "ru-RU"));
    }

    /// <summary>"en", "ru" or null when the answer is not recognized.</summary>
    public static string? Normalize(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "en" or "eng" or "english" or "английский" => "en",
        "ru" or "rus" or "russian" or "русский" => "ru",
        _ => null,
    };

    /// <summary>Name of a language in that language (menus).</summary>
    public static string NativeName(string code) => code == "ru" ? "Русский" : "English";

    /// <summary>Language of Windows when it is one of ours, English otherwise.</summary>
    public static string SystemDefault => Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? "en";

    public static string T(string en, string ru) => Ru ? ru : en;

    public static string N(int n) => n.ToString("N0", Culture);

    /// <summary>
    /// "3 chests" / "3 сундука". English: singular for 1 only. Russian has three forms: 1 сундук (also 21, 31…),
    /// 2 сундука (2 to 4, also 22 to 24…), 5 сундуков (everything else, 11 to 14 included).
    /// </summary>
    public static string Count(int n, string enOne, string enMany, string ruOne, string ruFew, string ruMany)
    {
        if (!Ru)
        {
            return $"{N(n)} {(n == 1 ? enOne : enMany)}";
        }
        int a = Math.Abs(n) % 100, b = a % 10;
        return $"{N(n)} {(a is >= 11 and <= 14 ? ruMany : b == 1 ? ruOne : b is >= 2 and <= 4 ? ruFew : ruMany)}";
    }

    /// <summary>"12th" / "12-е" (place).</summary>
    public static string Ordinal(int n)
    {
        if (Ru)
        {
            return $"{n}-е";
        }
        var suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{n}{suffix}";
    }

    // ---- Times shown to the player (Settings): local time or UTC, 12- or 24-hour clock ------------------

    /// <summary>Show times in UTC instead of the PC's time zone.</summary>
    public static bool UtcTimes { get; set; }

    /// <summary>"auto" (the Windows setting), "12" or "24".</summary>
    public static string ClockFormat { get; set; } = "auto";

    /// <summary>The Windows setting: 24-hour unless the short time pattern has an "h" (12-hour) in it.</summary>
    public static bool WindowsClock24 => !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h');

    public static bool Clock24 => ClockFormat switch { "12" => false, "24" => true, _ => WindowsClock24 };

    /// <summary>A UTC moment as it is shown: in UTC or in the PC's time zone.</summary>
    public static DateTime Shown(DateTime utc)
    {
        var u = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return UtcTimes ? u : u.ToLocalTime();
    }

    /// <summary>" UTC" after a time shown in UTC, nothing in local time.</summary>
    public static string Zone => UtcTimes ? " UTC" : "";

    /// <summary>The .NET pattern of a time of day ("HH:mm" / "h:mm tt"), with seconds or not.</summary>
    public static string TimePattern(bool seconds = false) => Clock24 ? (seconds ? "HH:mm:ss" : "HH:mm") : (seconds ? "h:mm:ss tt" : "h:mm tt");

    /// <summary>A time of day of a moment already converted by <see cref="Shown"/> ("22:54" / "10:54 PM", + " UTC").</summary>
    public static string Clock(DateTime shown, bool seconds = false) => shown.ToString(TimePattern(seconds), Culture) + Zone;

    /// <summary>Time stamp of the activity log, now ("[22:54:07]").</summary>
    public static string Stamp => $"[{Clock(Shown(DateTime.UtcNow), seconds: true)}]";

    /// <summary>The PC's time zone as an offset from UTC ("UTCв€’8", "UTC+5:30").</summary>
    public static string LocalOffset
    {
        get
        {
            var o = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
            var sign = o < TimeSpan.Zero ? "в€’" : "+";
            o = o.Duration();
            return o == TimeSpan.Zero ? "UTC" : $"UTC{sign}{o.Hours}{(o.Minutes != 0 ? $":{o.Minutes:00}" : "")}";
        }
    }

    /// <summary>Short date + time of a match ("5 Nov 10:54 PM" / "05.11 22:54"), of a moment already converted by <see cref="Shown"/>.</summary>
    public static string DateTimeShort(DateTime shown) => shown.ToString((Ru ? "dd.MM " : "d MMM ") + TimePattern(), Culture) + Zone;

    // Keys stored in data.json -> display names.
    private static readonly Dictionary<string, (string En, string Ru)> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Coffre"] = ("Chest", "Сундук"),
        ["Coffre rare"] = ("Rare chest", "Редкий сундук"),
        ["Coffre enterré"] = ("Buried chest", "Закопанный сундук"),
        ["Coffre d'esprit (rare)"] = ("Sprite chest (rare)", "Сундук духа (редкий)"),
        ["Coffre des affiches d'esprit"] = ("Sprite poster chest", "Сундук с плакатами духов"),
        ["Autre coffre"] = ("Other chest", "Другой сундук"),
        ["Canon à butin"] = ("Loot cannon", "Пушка с добычей"),
        ["Caisse de munitions"] = ("Ammo box", "Ящик с боеприпасами"),
        ["Caisse de nourriture"] = ("Food box", "Ящик с едой"),
        ["Autre conteneur"] = ("Other container", "Другой контейнер"),
        ["Trésor"] = ("Treasure", "Сокровище"),
        ["Trésor (type inconnu)"] = ("Treasure (unknown type)", "Сокровище (тип неизвестен)"),
        ["Feu d'artifice doré"] = ("Golden firework", "Золотой фейерверк"),
        ["Extracteur portable"] = ("Portable extractor", "Портативный экстрактор"),
        ["Localisateur d'esprit"] = ("Sprite locator", "Локатор духов"),
        ["Localisateur chanceux"] = ("Lucky locator", "Счастливый локатор"),
        ["Accélérateur d'extraction"] = ("Extraction accelerator", "Ускоритель извлечения"),
        ["inconnu"] = ("unknown", "неизвестно"),
    };

    /// <summary>Display name of a chest kind or treasure item (a name that is not a known key is shown as it is).</summary>
    public static string Name(string? stored)
    {
        if (stored != null)
        {
            stored = ChestKinds.Canonical(stored); // older name of a kind
            stored = TreasureNames.Canonical(stored);
        }
        if (string.IsNullOrEmpty(stored) || !Names.TryGetValue(stored, out var t))
        {
            return stored ?? "";
        }
        return Ru ? t.Ru : t.En;
    }
}
