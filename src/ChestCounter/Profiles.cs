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
                ManualEntries = ManualHistory(d, c),
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
            ManualEntries = ManualHistory(d, c),
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
        d.ManualHistory.Remove("");
        d.ManualHistory.Remove(key); // a total typed as a whole: the history restarts from it ("earlier" entry)
        if (chests == 0)
        {
            d.ManualChests.Remove(key);
        }
        else
        {
            d.ManualChests[key] = chests;
        }
    }

    // ---- History of what was added by hand (issue #27): each number typed is added, and can be removed again ---------

    /// <summary>
    /// The additions of the profile shown, newest first. What the history does not explain (added before it existed,
    /// or by an older version of the counter) is one "earlier" entry, so that the entries always add up to the total.
    /// </summary>
    public static List<ManualEntry> ManualHistory(TrackerData d, Config c)
    {
        var key = Current(d, c)?.Key ?? "";
        // Copies: the "earlier" entry is adjusted for the display only.
        var list = (d.ManualHistory.GetValueOrDefault(key) ?? new List<ManualEntry>())
            .Select(e => new ManualEntry { Id = e.Id, Chests = e.Chests, AddedUtc = e.AddedUtc, Kind = e.Kind, CareerMatches = e.CareerMatches, WeekFromUtc = e.WeekFromUtc })
            .ToList();
        Explain(list, Manual(d, key));
        return list.OrderBy(e => e.Kind == ManualEntry.Earlier).ThenByDescending(e => e.AddedUtc).ToList();
    }

    /// <summary>
    /// Adds chests by hand (a negative number removes some). An estimate replaces the previous estimate.
    /// <paramref name="weekFromUtc"/>: start of the treasure week they were opened in ("Opened this week").
    /// </summary>
    public static void AddManual(TrackerData d, Config c, int chests, int? careerMatches = null, DateTime? weekFromUtc = null)
    {
        var (key, list) = Settle(d, c);
        if (careerMatches != null)
        {
            list.RemoveAll(e => e.Kind == ManualEntry.Estimate);
        }
        list.Add(new ManualEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Chests = chests,
            AddedUtc = DateTime.UtcNow,
            Kind = careerMatches != null ? ManualEntry.Estimate : ManualEntry.Hand,
            CareerMatches = careerMatches,
            WeekFromUtc = careerMatches == null ? weekFromUtc : null,
        });
        Keep(d, key, list);
    }

    /// <summary>Removes one entry of the history (its chests leave the total). False when it does not exist.</summary>
    public static bool RemoveManual(TrackerData d, Config c, string id)
    {
        var (key, list) = Settle(d, c);
        var removed = list.RemoveAll(e => e.Id == id) > 0;
        Keep(d, key, list);
        return removed;
    }

    /// <summary>The history of the profile shown, made to add up to its total (the "" adjustment moves into it).</summary>
    private static (string Key, List<ManualEntry> List) Settle(TrackerData d, Config c)
    {
        var key = Current(d, c)?.Key ?? "";
        var total = Manual(d, key);
        var list = d.ManualHistory.GetValueOrDefault(key) ?? new List<ManualEntry>();
        if (key.Length > 0)
        {
            d.ManualChests.Remove("");
            d.ManualHistory.Remove("");
        }
        Explain(list, total);
        return (key, list);
    }

    /// <summary>Adds the part of <paramref name="total"/> the entries do not explain to the "earlier" entry.</summary>
    private static void Explain(List<ManualEntry> list, int total)
    {
        var missing = total - list.Sum(e => e.Chests);
        if (missing == 0)
        {
            return;
        }
        var earlier = list.FirstOrDefault(e => e.Kind == ManualEntry.Earlier);
        if (earlier == null)
        {
            list.Add(new ManualEntry { Id = ManualEntry.Earlier, Chests = missing, Kind = ManualEntry.Earlier });
        }
        else if ((earlier.Chests += missing) == 0)
        {
            list.Remove(earlier);
        }
    }

    private static void Keep(TrackerData d, string key, List<ManualEntry> list)
    {
        var total = list.Sum(e => e.Chests);
        if (total == 0) d.ManualChests.Remove(key); else d.ManualChests[key] = total;
        if (list.Count == 0) d.ManualHistory.Remove(key); else d.ManualHistory[key] = list;
    }

    public static string Describe(Profile p) =>
        $"{p.Name}  ({L.Count(p.Matches, "match", "matches", "матч", "матча", "матчей")}, {L.Count(p.Chests, "chest", "chests", "сундук", "сундука", "сундуков")})";
}
