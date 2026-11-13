using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChestCounter;

public sealed class ChestRecord
{
    public float TimeSeconds { get; set; }
    public string Kind { get; set; } = "";
    public string Blueprint { get; set; } = "";
    /// <summary>True when the chest was destroyed by the player instead of being opened.</summary>
    public bool Destroyed { get; set; }
    public List<string> Items { get; set; } = new();
    public List<string> TreasureItems { get; set; } = new();
}

public sealed class TreasureFind
{
    /// <summary>Name to display (re-resolved at display time, in the current language).</summary>
    public string DisplayName => PrizeId != null ? TreasureNames.Friendly(PrizeId) : L.Name(Item);

    public string Item { get; set; } = "";
    public string RawItem { get; set; } = "";
    /// <summary>Prize id sent by the server with the golden chest, e.g. "DelilahPrize:dpid_item4".</summary>
    public string? PrizeId { get; set; }
    /// <summary>True when detected from the golden treasure chest itself.</summary>
    public bool FromGoldenChest { get; set; }
    /// <summary>True when detected only from the prize handler (exact time, item unknown unless sent at that moment).</summary>
    public bool FromPrizeHandler { get; set; }
    /// <summary>True when the time is exact (golden chest or prize handler), false when estimated.</summary>
    public bool TimeExact { get; set; }
    /// <summary>True when the source chest was destroyed rather than opened.</summary>
    public bool SourceDestroyed { get; set; }
    public string SourceLabel => SourceDestroyed ? $"{L.Name(SourceKind)} ({L.T("destroyed", "разрушен")})" : L.Name(SourceKind);
    /// <summary>When it was obtained (replay seconds). Exact for golden chests, estimated otherwise.</summary>
    public float? ObtainedAtSeconds { get; set; }
    /// <summary>Kind of container / interaction it came from (Coffre, Canon à butin...).</summary>
    public string SourceKind { get; set; } = "";
    public string SourceBlueprint { get; set; } = "";
    public bool SourceIsChest { get; set; } = true;
    /// <summary>True when the source is known for sure (golden chest), false when it is the last interaction before use.</summary>
    public bool SourceCertain { get; set; }
    /// <summary>For estimated sources: other (non-chest) interactions between the source and the first use.</summary>
    public List<string> AlsoPossible { get; set; } = new();
    /// <summary>Team treasure: name of the teammate whose chest gave it ("" when the teammate was out of sight).</summary>
    public string? Teammate { get; set; }
}

/// <summary>One addition made by hand to the number of chests (negative: chests removed).</summary>
public sealed class ManualEntry
{
    /// <summary>Typed in the pencil's box.</summary>
    public const string Hand = "hand";

    /// <summary>From the matches of Fortnite's Career tab: a new estimate replaces the previous one.</summary>
    public const string Estimate = "estimate";

    /// <summary>What was added before the history existed (or by an older version): one entry, id "earlier".</summary>
    public const string Earlier = "earlier";

    public string Id { get; set; } = "";
    public int Chests { get; set; }
    public DateTime? AddedUtc { get; set; }
    public string Kind { get; set; } = Hand;

    /// <summary>Matches of the Career tab the estimate was made from.</summary>
    public int? CareerMatches { get; set; }

    /// <summary>
    /// Start of the treasure week the player said these chests were opened in ("Opened this week"): they count in that
    /// week's chances too, at the normal odds. Null = no week (season only).
    /// </summary>
    public DateTime? WeekFromUtc { get; set; }
}

/// <summary>Chests of one teammate in a match, as recorded in the player's own replay.</summary>
public sealed class TeamChestCount
{
    public string Name { get; set; } = "";
    public string? EpicId { get; set; }
    public bool IsBot { get; set; }
    public int Chests { get; set; }
    public int Destroyed { get; set; }
}

/// <summary>A treasure seen in a match, from a golden chest opened by another player.</summary>
public sealed class SeenPrize
{
    public string Player { get; set; } = "";
    public string PrizeId { get; set; } = "";
    public float TimeSeconds { get; set; }
}

public sealed class MatchRecord
{
    public string SessionId { get; set; } = "";
    public string ReplayFile { get; set; } = "";
    public DateTime AnalyzedAtUtc { get; set; }
    public DateTime? MatchStartUtc { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Playlist { get; set; }
    public string? GameVersion { get; set; }
    public string? PlayerName { get; set; }
    public string? EpicId { get; set; }
    public bool TrackedByUsername { get; set; }
    /// <summary>Set when none of the configured names played this match (name of the replay owner).</summary>
    public string? NotTrackedOwner { get; set; }
    public bool IsReplayOwner { get; set; }
    public int? Placement { get; set; }
    public int? TotalPlayers { get; set; }
    public int? Eliminations { get; set; }
    public int ChestsOpened { get; set; }
    public int AmmoBoxesOpened { get; set; }
    public int OtherContainersOpened { get; set; }
    /// <summary>Non-chest interactions by kind (loot cannons, ammo boxes, food boxes...).</summary>
    public Dictionary<string, int> OtherInteractions { get; set; } = new();
    public int AnalyzerVersion { get; set; } = 1;
    /// <summary>Kept for the files of older versions; what counts is decided by <see cref="GameModes.Counts"/>.</summary>
    public bool IsBattleRoyale { get; set; } = true;

    /// <summary>Map of the match, from the replay header (e.g. /Delilah/Maps/Delilah_Terrain). Null before version 1.5.</summary>
    public string? Map { get; set; }

    /// <summary>Private match (custom matchmaking key).</summary>
    public bool IsCustomMatch { get; set; }
    public List<ChestRecord> Chests { get; set; } = new();
    public List<TreasureFind> TreasureFinds { get; set; } = new();
    /// <summary>Treasures found by teammates (shared with the whole team). Not counted in the player's own totals.</summary>
    public List<TreasureFind> TeamTreasureFinds { get; set; } = new();

    /// <summary>
    /// Golden chests opened by other players (not the player, not a teammate) within reach of the replay (about 150 m):
    /// who got what, and when.
    /// </summary>
    public List<SeenPrize> SeenPrizes { get; set; } = new();

    /// <summary>Chests opened or destroyed by each teammate (random teammates included). Not in the player's own total.</summary>
    public List<TeamChestCount> TeamChests { get; set; } = new();
    public int TeamChestsOpened { get; set; }

    /// <summary>Time and distance spent in vehicles (driver or passenger).</summary>
    public int VehicleSeconds { get; set; }
    public int VehicleMeters { get; set; }

    /// <summary>
    /// Loot piles left along the player's drives with no recorded chest to explain them: Fortnite did not record
    /// what the vehicle destroyed in this replay, so chests are missing from the count (see GitHub issue 8).
    /// </summary>
    public int UnrecordedVehicleLoot { get; set; }

    /// <summary>From this many unexplained piles on, the match is flagged (one or two can be somebody else's loot).</summary>
    [JsonIgnore] public bool VehicleChestsMissing => UnrecordedVehicleLoot >= 3;
}

public sealed class Config
{
    /// <summary>"en" or "ru" (asked on first launch).</summary>
    public string? Language { get; set; }

    /// <summary>Selected profile (Epic id or name of the account the player plays on); null = most recent one.</summary>
    public string? Profile { get; set; }
    public string DemosFolder { get; set; } = "";

    /// <summary>Open the tool automatically when Fortnite starts (invisible standby process started with Windows).</summary>
    public bool StartWithFortnite { get; set; }

    public bool ShowOverlay { get; set; } = true;

    /// <summary>Minimizing the window sends the counter to the notification area (system tray) instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>"prompt" (offer new versions), "auto" (install them) or "off" (never check GitHub).</summary>
    public string UpdateMode { get; set; } = "prompt";

    /// <summary>Times shown in "local" time (the PC's time zone) or in "utc".</summary>
    public string TimeZone { get; set; } = "local";

    /// <summary>Clock of the times shown: "auto" (the Windows setting), "12" (10:54 PM) or "24" (22:54).</summary>
    public string ClockFormat { get; set; } = "auto";

    /// <summary>Applies the time settings to every time shown (<see cref="L.Shown"/>, <see cref="L.Clock"/>).</summary>
    public void ApplyTimeSettings()
    {
        TimeZone = TimeZone == "utc" ? "utc" : "local";
        ClockFormat = ClockFormat is "12" or "24" ? ClockFormat : "auto";
        L.UtcTimes = TimeZone == "utc";
        L.ClockFormat = ClockFormat;
    }

    /// <summary>Notices of content.json the player closed (by id), so that they do not come back.</summary>
    public List<string> DismissedNotices { get; set; } = new();

    /// <summary>Version of the tool the player last used: what's new is shown once after an update.</summary>
    public string? LastVersion { get; set; }

    /// <summary>Look of the overlay: "hud" (figures only, like the game's HUD) or "classic" (dark card).</summary>
    public string OverlayStyle { get; set; } = "hud";
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    /// <summary>Locked overlay = click-through, cannot be moved (Ctrl+Shift+L to toggle).</summary>
    public bool OverlayLocked { get; set; }

    /// <summary>Size of the overlay in percent (100 = normal), on top of the Windows display scaling.</summary>
    public int OverlaySize { get; set; } = 100;

    /// <summary>Opacity of the overlay in percent (100 = opaque).</summary>
    public int OverlayOpacity { get; set; } = 100;

    /// <summary>Count the chests of the match in progress (re-reads the replay being recorded).</summary>
    public bool LiveUpdate { get; set; } = true;

    /// <summary>Minimum delay between two readings of the replay in progress (seconds, 10 minimum).</summary>
    public int LiveIntervalSeconds { get; set; } = 30;

    /// <summary>Show the squad total (the player's chests + those of all teammates) on the overlay and the home screen.</summary>
    public bool ShowSquadTotal { get; set; }

    /// <summary>Show the squad total in the report too (tile and column of the latest matches). Off by default, like the overlay's.</summary>
    public bool ShowSquadInReport { get; set; }

    /// <summary>Show the treasures found by teammates in the report (estimate). Off by default.</summary>
    public bool ShowTeamTreasuresInReport { get; set; }
}

public sealed class TrackerData
{
    public int Version { get; set; } = 1;
    public int AnalyzerVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<MatchRecord> Matches { get; set; } = new();

    /// <summary>Key = file name|size|last write ticks. Value = session id, "skipped" or "error: ...".</summary>
    public Dictionary<string, string> ProcessedFiles { get; set; } = new();

    /// <summary>
    /// Chests added (or removed, when negative) by hand, per profile key: what the player had opened before using
    /// the tool. "" = set before the first match gave the profile its key.
    /// </summary>
    public Dictionary<string, int> ManualChests { get; set; } = new();

    /// <summary>
    /// What was added by hand, entry by entry, per profile key (the pencil of "Chests in total"). Its sum is
    /// <see cref="ManualChests"/>, which stays the figure used everywhere (and the one older versions read).
    /// </summary>
    public Dictionary<string, List<ManualEntry>> ManualHistory { get; set; } = new();

    /// <summary>Treasures ticked as obtained in the collection, per profile key (saved by the graphical window).</summary>
    public Dictionary<string, List<string>> Obtained { get; set; } = new();

    /// <summary>Treasures ticked for the profile shown (filled by <see cref="Profiles.View"/>, not saved).</summary>
    [JsonIgnore] public List<string> ObtainedIds { get; set; } = new();

    /// <summary>Manual adjustment of the profile shown (filled by <see cref="Profiles.View"/>, not saved).</summary>
    [JsonIgnore] public int ManualAdjust { get; set; }

    /// <summary>The additions by hand of the profile shown, entry by entry (set by <see cref="Profiles.View"/>).</summary>
    [JsonIgnore] public List<ManualEntry> ManualEntries { get; set; } = new();

    /// <summary>Chests counted in the replays (what averages and treasure odds are based on).</summary>
    [JsonIgnore] public int CountedChests => Matches.Sum(m => m.ChestsOpened);

    /// <summary>The player's total: counted chests + the manual adjustment (never below zero).</summary>
    [JsonIgnore] public int TotalChests => Math.Max(0, CountedChests + ManualAdjust);

    /// <summary>Chests of all teammates, in all matches.</summary>
    [JsonIgnore] public int TeamChests => Matches.Sum(m => m.TeamChestsOpened);

    /// <summary>The squad total: the player's total + the chests of every teammate.</summary>
    [JsonIgnore] public int SquadChests => TotalChests + TeamChests;
}

/// <summary>Reads/writes JSON files atomically in the data folder.</summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Folder { get; }

    /// <summary>
    /// %APPDATA%\ChestCounter. The tool was called ChestTracker before version 1.1: its folder is moved
    /// (history, settings and treasure names are kept).
    /// </summary>
    public static string DefaultFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "ChestCounter");
        var old = Path.Combine(appData, "ChestTracker");
        try
        {
            if (!Directory.Exists(folder) && File.Exists(Path.Combine(old, "data.json")))
            {
                Directory.Move(old, folder);
            }
        }
        catch
        {
            // the old folder is in use (old version still running): start fresh, nothing is lost
        }
        return folder;
    }

    public string ConfigPath => Path.Combine(Folder, "config.json");

    public string DataPath => Path.Combine(Folder, "data.json");
    public string ReportPath => Path.Combine(Folder, "report.html");
    public string OverlayPath => Path.Combine(Folder, "overlay.html");
    public string CsvPath => Path.Combine(Folder, "matches.csv");
    public string ErrorLogPath => Path.Combine(Folder, "errors.log");

    public Store(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        MigrateFileNames();
    }

    /// <summary>Versions before 1.1 used French file names: drop the old generated pages.</summary>
    private void MigrateFileNames()
    {
        try
        {
            foreach (var generated in new[] { "rapport.html", "parties.csv" })
            {
                var path = Path.Combine(Folder, generated);
                if (File.Exists(path))
                {
                    File.Delete(path); // regenerated as report.html / matches.csv
                }
            }
        }
        catch
        {
            // not critical: the old files simply stay there
        }
    }

    public Config? LoadConfig() => Load<Config>(ConfigPath);
    public void SaveConfig(Config config) => Save(ConfigPath, config);
    /// <summary>A new history starts at the current analysis version (nothing to re-analyze).</summary>
    public TrackerData LoadData() => Load<TrackerData>(DataPath) ?? new TrackerData { AnalyzerVersion = ReplayAnalyzer.CurrentVersion };
    public void SaveData(TrackerData data) => Save(DataPath, data);

    private static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            // Keep a copy of the unreadable file rather than silently losing the history.
            var backup = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(path, backup, overwrite: true); } catch { backup = "-"; /* disk full, file locked: start anyway */ }
            Console.WriteLine(L.T($"Unreadable file ({ex.Message}), backup copy: {backup}", $"Файл не читается ({ex.Message}), резервная копия: {backup}"));

            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
