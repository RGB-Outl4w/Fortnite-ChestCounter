namespace ChestCounter;

/// <summary>An Epic account that recorded replays on this PC. Stats are kept per profile.</summary>
public sealed record Profile(string Key, string Name, int Matches, int Chests, DateTime LastPlayed);

/// <summary>
/// Replays saved on a PC are normally those of its player, so the replay owner (the recording player) is tracked.
/// When several accounts recorded replays here, each one gets its own profile and the player picks the account
/// they play on: the overlay and the report only show that profile.
/// </summary>
public static class Profiles
{
    /// <summary>Stable key of a match's account: Epic id (survives a name change), else the display name.</summary>
    public static string Key(MatchRecord m) => !string.IsNullOrEmpty(m.EpicId) ? m.EpicId : m.PlayerName ?? "";

    /// <summary>Matches that count (Battle Royale of the season): those of other modes saved by older versions are left out.</summary>
    public static IEnumerable<MatchRecord> Counted(TrackerData d) => d.Matches.Where(GameModes.Counts);

    public static List<Profile> List(TrackerData d) => Counted(d)
        .GroupBy(Key)
        .Select(g =>
        {
            var latest = g.OrderByDescending(When).First();
            var name = g.OrderByDescending(When).Select(m => m.PlayerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            return new Profile(g.Key, name ?? L.T("Unknown account", "Неизвестный аккаунт"), g.Count(), g.Sum(m => m.ChestsOpened), When(latest));
        })
        .OrderByDescending(p => p.LastPlayed)
        .ToList();

    private static DateTime When(MatchRecord m) => m.MatchStartUtc ?? m.AnalyzedAtUtc;

    /// <summary>The selected profile when it exists, else the most recently played one (null = no match yet).</summary>
    public static Profile? Current(TrackerData d, Config c)
    {
        var all = List(d);
        return all.FirstOrDefault(p => p.Key == c.Profile) ?? all.FirstOrDefault();
    }

    /// <summary>The data of one profile only (same objects, filtered list), as seen by the overlay and the report.</summary>
    public static TrackerData View(TrackerData d, Config c)
    {
        var current = Current(d, c);
        if (current == null)
        {
            return new TrackerData
            {
                Version = d.Version,
                AnalyzerVersion = d.AnalyzerVersion,
                CreatedUtc = d.CreatedUtc,
                Matches = Counted(d).ToList(),
                ProcessedFiles = d.ProcessedFiles,
                ManualChests = d.ManualChests,
                ManualAdjust = Manual(d, null),
                Obtained = d.Obtained,
                ObtainedIds = d.Obtained.GetValueOrDefault("") ?? new(),
            };
        }
        return new TrackerData
        {
            Version = d.Version,
            AnalyzerVersion = d.AnalyzerVersion,
            CreatedUtc = d.CreatedUtc,
            Matches = Counted(d).Where(m => Key(m) == current.Key).ToList(),
            ProcessedFiles = d.ProcessedFiles,
            ManualChests = d.ManualChests,
            ManualAdjust = Manual(d, current.Key),
            Obtained = d.Obtained,
            ObtainedIds = (d.Obtained.GetValueOrDefault(current.Key) ?? new()).Concat(d.Obtained.GetValueOrDefault("") ?? new()).Distinct().ToList(),
        };
    }

    /// <summary>
    /// Chests added by hand for a profile. An adjustment made before the first match (no profile yet, key "") follows
    /// the profile shown.
    /// </summary>
    public static int Manual(TrackerData d, string? profileKey) =>
        d.ManualChests.GetValueOrDefault("") + (string.IsNullOrEmpty(profileKey) ? 0 : d.ManualChests.GetValueOrDefault(profileKey));

    /// <summary>Ticks (or unticks) a treasure of the collection for the profile shown.</summary>
    public static void SetObtained(TrackerData d, Config c, string prizeId, bool on)
    {
        var key = Current(d, c)?.Key ?? "";
        if (!d.Obtained.TryGetValue(key, out var list))
        {
            d.Obtained[key] = list = new List<string>();
        }
        list.Remove(prizeId);
        if (on)
        {
            list.Add(prizeId);
        }
        if (key.Length > 0 && d.Obtained.TryGetValue("", out var early))
        {
            early.Remove(prizeId); // ticked before the first match: now the profile's
        }
    }

    /// <summary>Sets the manual adjustment of the profile shown (0 removes it).</summary>
    public static void SetManual(TrackerData d, Config c, int chests)
    {
        var key = Current(d, c)?.Key ?? "";
        d.ManualChests.Remove("");
        if (chests == 0)
        {
            d.ManualChests.Remove(key);
        }
        else
        {
            d.ManualChests[key] = chests;
        }
    }

    public static string Describe(Profile p) =>
        $"{p.Name}  ({L.Count(p.Matches, "match", "matches", "матч", "матча", "матчей")}, {L.Count(p.Chests, "chest", "chests", "сундук", "сундука", "сундуков")})";
}
