using System.Text;

namespace ChestCounter;

/// <summary>
/// Watches the Fortnite Demos folder, analyzes every finished replay once and accumulates the results.
/// </summary>
public sealed class Tracker
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);

    /// <summary>Replay finalized by Fortnite (no longer live, real key written): only a few quiet seconds are needed.</summary>
    private static readonly TimeSpan FinalizedQuietPeriod = TimeSpan.FromSeconds(4);

    /// <summary>A finished replay not modified for this long is complete for sure (replays left from earlier).</summary>
    private static readonly TimeSpan OldReplayAge = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Fortnite appends to the replay of a match in progress about every 25 s. Still flagged "live" but untouched for
    /// this long: the recording was cut (Fortnite crashed or was killed) and will never be finished.
    /// </summary>
    private static readonly TimeSpan AbandonedLiveAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMinutes(3);

    private readonly Store _store;
    private readonly Config _config;
    private readonly TrackerData _data;
    private readonly Dictionary<string, long> _lastSeenSize = new();

    /// <summary>Failed attempts per replay: a failure that may not come back (file in use, timeout) is retried.</summary>
    private readonly Dictionary<string, int> _attempts = new();
    private const int MaxAttempts = 3;

    /// <summary>A parse that timed out and has not stopped yet. The parser keeps static state: never two at once.</summary>
    private static Thread? _orphan;

    /// <summary>The previous parse is still stopping: nothing can be read right now.</summary>
    private sealed class ParserBusyException : Exception { }

    public Tracker(Store store, Config config, TrackerData data)
    {
        _store = store;
        _config = config;
        _data = data;

        // Matches of other modes saved by older versions (OG, Reload, LTM...): no longer counted (kept in the file).
        // Before version 1.1, matches of other accounts were rejected ("autre joueur"): they now get their own profile.
        foreach (var key in _data.ProcessedFiles.Where(kv => kv.Value.StartsWith("autre joueur", StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
        {
            _data.ProcessedFiles.Remove(key);
        }
    }

    public TrackerData Data => _data;

    /// <summary>True while a replay is waiting to be finished / to become stable.</summary>
    public bool HasPending { get; private set; }

    /// <summary>True when a match has just ended and its replay is about to be read: check again within seconds.</summary>
    public bool IsFinishing { get; private set; }

    /// <summary>Gives the memory used by the analysis back to Windows (the tool idles at a few MB).</summary>
    private static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Called after each replay of a scan (the window's actions are run there during a long batch).</summary>
    public Action? BetweenFiles { get; set; }

    /// <summary>True when the counter is quitting: a scan stops after the replay being read.</summary>
    public Func<bool>? StopRequested { get; set; }

    public static string FileKey(FileInfo f) => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";

    public IEnumerable<FileInfo> ReplayFiles()
    {
        var dir = new DirectoryInfo(_config.DemosFolder);
        if (!dir.Exists)
        {
            return Enumerable.Empty<FileInfo>();
        }
        return dir.EnumerateFiles("*.replay", SearchOption.TopDirectoryOnly).OrderBy(f => f.LastWriteTimeUtc);
    }

    public IEnumerable<FileInfo> UnprocessedFiles() => ReplayFiles().Where(f => !_data.ProcessedFiles.ContainsKey(FileKey(f)));

    /// <summary>Replays not analyzed yet and not being recorded (what the next scan will read).</summary>
    public int BacklogCount() => UnprocessedFiles().Count(f => ReplayHeaderProbe.IsLive(f.FullName) != true);

    public void MarkSkipped(IEnumerable<FileInfo> files)
    {
        foreach (var f in files)
        {
            _data.ProcessedFiles[FileKey(f)] = "skipped";
        }
        _store.SaveData(_data);
    }

    /// <summary>Result of the analysis of one replay.</summary>
    private sealed record Outcome(MatchRecord? Match = null, bool Updated = false, string? Skipped = null, string? Error = null, bool Retry = false);

    /// <summary>
    /// One pass over the folder. Returns the number of new matches recorded. Several replays at once (replays left
    /// from earlier) are shown as a progress bar and a summary; a single one (end of a match) gets its own line.
    /// </summary>
    /// <param name="ignoreQuietPeriod">Process finished replays immediately (used by --once).</param>
    public int Scan(bool ignoreQuietPeriod = false)
    {
        HasPending = false;
        IsFinishing = false;
        var ready = new List<FileInfo>();
        foreach (var file in UnprocessedFiles().ToList())
        {
            file.Refresh();
            if (!file.Exists)
            {
                continue;
            }
            if (!IsReady(file, ignoreQuietPeriod))
            {
                HasPending = true;
                continue;
            }
            ready.Add(file);
        }
        if (ready.Count == 0)
        {
            return 0;
        }

        var batch = ready.Count >= 2;
        var profilesBefore = Profiles.List(_data).Count;
        var bar = batch ? new ProgressBar(L.T("Analyzing replays", "Анализ повторов"), ready.Count) : null;
        var outcomes = new List<Outcome>();
        for (var i = 0; i < ready.Count; i++)
        {
            var file = ready[i];
            if (batch)
            {
                bar!.Update(i, file.Name);
                ReportWriter.Progress = new ScanProgress(i, ready.Count);
                ReportWriter.WriteAll(_store, _config, _data);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                var mb = (file.Length / 1024 / 1024.0).ToString("F1", L.Culture);
                var justEnded = DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMinutes(2);
                Console.WriteLine(justEnded
                    ? L.T($"{L.Stamp} Match over: analyzing the replay ({mb} MB)…", $"{L.Stamp} Матч окончен: анализирую повтор ({mb} МБ)…")
                    : L.T($"{L.Stamp} Analyzing {file.Name} ({mb} MB)…", $"{L.Stamp} Анализирую {file.Name} ({mb} МБ)…"));
                Console.ResetColor();
            }

            var outcome = Process(file);
            outcomes.Add(outcome);
            if (_liveFile == file.FullName)
            {
                ClearLive(); // read (counted, skipped or unreadable): the match is over, whatever the outcome
            }
            if (!batch)
            {
                PrintOutcome(outcome);
            }
            // A long batch (first launch, new analysis version): the window stays usable between two replays,
            // and closing the counter stops it (the rest is analyzed at the next launch).
            BetweenFiles?.Invoke();
            if (StopRequested?.Invoke() == true)
            {
                break;
            }
        }

        if (batch)
        {
            bar!.Update(ready.Count);
            bar.Clear();
            ReportWriter.Progress = null;
            PrintBatchSummary(outcomes);
            if (profilesBefore < 2 && Profiles.List(_data).Count >= 2)
            {
                NewProfilesFound = true;
            }
        }

        var added = outcomes.Count(o => o.Match != null);
        if (added > 0 || batch)
        {
            ReportWriter.WriteAll(_store, _config, _data);
        }
        return added;
    }

    /// <summary>Set when a scan found replays of a second account: the player should choose their profile.</summary>
    public bool NewProfilesFound { get; set; }

    private bool IsReady(FileInfo file, bool ignoreQuietPeriod)
    {
        var live = ReplayHeaderProbe.IsLive(file.FullName);
        var quietFor = DateTime.UtcNow - file.LastWriteTimeUtc;
        if (live == true)
        {
            // Match still being recorded, unless the recording was cut long ago (Process deals with it).
            return quietFor > AbandonedLiveAge;
        }

        if (live == false && (ignoreQuietPeriod || quietFor > OldReplayAge))
        {
            return true;
        }

        // The file must not have changed for a while and its size must be stable between two scans.
        var stable = _lastSeenSize.TryGetValue(file.FullName, out var size) && size == file.Length;
        _lastSeenSize[file.FullName] = file.Length;

        if (live == null)
        {
            // Header could not be read: be extra careful.
            return stable && quietFor > TimeSpan.FromMinutes(2);
        }

        // Fortnite clears the "live" flag and writes the real encryption key when it stops recording: the replay
        // is complete. Read it as soon as its size has not changed for a few seconds (two checks in a row).
        if (!ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            IsFinishing = true;
            return stable && quietFor >= FinalizedQuietPeriod;
        }
        return stable && quietFor > QuietPeriod;
    }

    private Outcome Process(FileInfo file)
    {
        var key = FileKey(file);

        // Recording cut in the middle of the match: Fortnite never wrote the decryption key, nothing can be read.
        // Without this, the replay would stay "pending" forever (no auto update, auto close delayed every time).
        if (ReplayHeaderProbe.IsLive(file.FullName) == true && ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            _data.ProcessedFiles[key] = "incomplete";
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("incomplete replay (Fortnite closed during the match): this match cannot be counted",
                "неполный повтор (Fortnite закрылся во время матча): этот матч нельзя посчитать"));
        }

        MatchRecord record;
        try
        {
            record = Analyze(file, ParseTimeout);
        }
        catch (ParserBusyException)
        {
            HasPending = true;
            return new Outcome(Retry: true);
        }
        catch (Exception ex)
        {
            // File in use, analysis too slow while the game loads the PC: may work next time. Only a failure that
            // repeats is written down (a recorded error is not tried again until the analysis version changes).
            var attempt = _attempts[key] = _attempts.GetValueOrDefault(key) + 1;
            if (ex is IOException or UnauthorizedAccessException or TimeoutException && attempt < MaxAttempts)
            {
                HasPending = true;
                return new Outcome(Retry: true);
            }
            _attempts.Remove(key);
            var message = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
            ErrorLog.Write($"Unreadable replay: {file.FullName} ({file.Length} bytes, {file.LastWriteTime:yyyy-MM-dd HH:mm:ss})", ex);
            _data.ProcessedFiles[key] = "error: " + message;
            _store.SaveData(_data);
            return new Outcome(Error: $"{file.Name} : {message}");
        }

        record.ReplayFile = file.Name;

        if (GameModes.WhyNot(record) is { } notCounted)
        {
            // Only Battle Royale on the season's map counts (no OG, Reload, LTM, Creative...). Also drop a record left
            // by an older version of the tool for this replay.
            _data.Matches.RemoveAll(m => m.SessionId == record.SessionId);
            _data.ProcessedFiles[key] = "autre mode: " + (record.Playlist ?? "?");
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T($"not counted: {notCounted}", $"не посчитано: {notCounted}"));
        }

        var existing = _data.Matches.FirstOrDefault(m => m.SessionId == record.SessionId);
        if (existing != null && existing.AnalyzerVersion >= record.AnalyzerVersion)
        {
            _data.ProcessedFiles[key] = record.SessionId;
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("match already counted (same match, renamed file)", "матч уже посчитан (тот же матч, файл переименован)"));
        }

        if (existing != null)
        {
            // Re-analysis with a newer version of the tool: replace the old record.
            record.AnalyzedAtUtc = existing.AnalyzedAtUtc;
            _data.Matches.Remove(existing);
        }

        _data.Matches.Add(record);
        _data.ProcessedFiles[key] = record.SessionId;
        _store.SaveData(_data);
        if (_liveFile == file.FullName)
        {
            ClearLive(); // the final analysis replaces the live estimate
        }

        // First match ever: its account becomes the profile.
        if (string.IsNullOrEmpty(_config.Profile))
        {
            _config.Profile = Profiles.Key(record);
            try { _store.SaveConfig(_config); } catch { /* ignore */ }
        }

        return new Outcome(record, Updated: existing != null);
    }

    private void PrintOutcome(Outcome o)
    {
        if (o.Retry)
        {
            Console.WriteLine(L.T("   Not readable right now: trying again shortly.", "   Сейчас прочитать не удалось: скоро повторю попытку."));
            return;
        }
        if (o.Error != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(L.T($"   Cannot read this replay: {o.Error}", $"   Не удалось прочитать этот повтор: {o.Error}"));
            Console.ResetColor();
            PrintErrorLogHint();
            return;
        }
        if (o.Skipped != null)
        {
            Console.WriteLine($"   {L.T("Skipped", "Пропущено")} : {o.Skipped}.");
            return;
        }

        var m = o.Match!;
        var current = Profiles.Current(_data, _config);
        // A match that has just ended on another account (several accounts on this PC, or a new one): that is the
        // account being played now, the counter follows it.
        var end = (m.MatchStartUtc ?? m.AnalyzedAtUtc).AddSeconds(m.DurationSeconds ?? 0);
        if (current != null && Profiles.Key(m) != current.Key && !o.Updated && DateTime.UtcNow - end < TimeSpan.FromMinutes(15))
        {
            _config.Profile = Profiles.Key(m);
            try { _store.SaveConfig(_config); } catch { /* ignore */ }
            PrintMatch(m, o.Updated);
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            var who = m.PlayerName ?? L.T("unknown account", "неизвестный аккаунт");
            Console.WriteLine(L.T($"   Match played on the account {who}: the counter now shows its profile (\"Switch profile\" on the home tab to go back to {current.Name}).",
                $"   Матч сыгран на аккаунте {who}: счётчик теперь показывает его профиль («Сменить профиль» на главной вкладке, чтобы вернуться к {current.Name})."));
            Console.ResetColor();
            return;
        }
        PrintMatch(m, o.Updated);
        if (current != null && Profiles.Key(m) != current.Key)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            var name = m.PlayerName ?? L.T("unknown account", "неизвестный аккаунт");
            Console.WriteLine(L.T($"   Match played on another account ({name}): it counts for its own profile. Profile shown: {current.Name} (\"Switch profile\" in the menu).",
                $"   Матч сыгран на другом аккаунте ({name}): он идёт в его собственный профиль. Показан профиль: {current.Name} («Сменить профиль» в меню)."));
            Console.ResetColor();
        }
    }

    private void PrintErrorLogHint()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(L.T($"   Details in {_store.ErrorLogPath}. Usually caused by a Fortnite update: report it at {AppInfo.IssuesUrl}",
            $"   Подробности в {_store.ErrorLogPath}. Обычно причина — обновление Fortnite: сообщи об этом на {AppInfo.IssuesUrl}"));
        Console.ResetColor();
    }

    private void PrintBatchSummary(List<Outcome> outcomes)
    {
        var matches = outcomes.Where(o => o.Match != null).Select(o => o.Match!).ToList();
        var skipped = outcomes.Count(o => o.Skipped != null);
        outcomes = outcomes.Where(o => !o.Retry).ToList();
        var errors = outcomes.Where(o => o.Error != null).Select(o => o.Error!).ToList();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"  ✔ {L.Count(outcomes.Count, "replay analyzed", "replays analyzed", "повтор проанализирован", "повтора проанализировано", "повторов проанализировано")}");
        Console.ResetColor();
        Console.Write(L.T(": ", ": "));
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{L.Count(matches.Count, "match", "matches", "матч", "матча", "матчей")}, {L.Count(matches.Sum(m => m.ChestsOpened), "chest", "chests", "сундук", "сундука", "сундуков")}");
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        if (treasures > 0)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write($", ★ {L.Count(treasures, "treasure", "treasures", "сокровище", "сокровища", "сокровищ")}");
        }
        Console.ResetColor();
        Console.WriteLine();
        if (skipped > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(L.T($"    {skipped} skipped: modes other than the season's Battle Royale (OG, Creative, limited-time modes…), matches already counted or incomplete replays.", $"    Пропущено: {skipped} (режимы, кроме «Королевской битвы» текущего сезона: OG, творческий, временные режимы…, уже посчитанные матчи или неполные повторы)."));
            Console.ResetColor();
        }
        if (errors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(L.T($"    {errors.Count} unreadable replay(s):", $"    Нечитаемых повторов: {errors.Count}:"));
            foreach (var e in errors.Take(5))
            {
                Console.WriteLine($"      {e}");
            }
            Console.ResetColor();
            PrintErrorLogHint();
        }
    }

    /// <summary>Reads the file (even while Fortnite writes it) and parses it on a lowest-priority background thread.</summary>
    private MatchRecord Analyze(FileInfo file, TimeSpan timeout)
    {
        if (_orphan is { IsAlive: true })
        {
            throw new ParserBusyException();
        }
        _orphan = null;
        Unreal.Core.ReplayReader<FortniteReplayReader.Models.FortniteReplay>.Cancel = false;

        byte[] bytes;
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }

        MatchRecord? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                // The replays saved on a PC are those of its player: the replay owner is tracked (one profile per account).
                result = new ReplayAnalyzer().Analyze(ms, file.Name, "");
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "ReplayAnalysis",
        };
        worker.Start();
        if (!worker.Join(timeout))
        {
            // Ask the parse to stop, and let nothing else be parsed until it has.
            Unreal.Core.ReplayReader<FortniteReplayReader.Models.FortniteReplay>.Cancel = true;
            _orphan = worker;
            throw new TimeoutException(L.T("analysis took too long", "анализ занял слишком много времени"));
        }
        bytes = Array.Empty<byte>();
        ReleaseMemory();
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); // keep the original stack
        }
        return result!;
    }

    // ---- Live match ---------------------------------------------------------------------------
    private string? _liveFile;
    private long _liveSize = -1;
    private DateTime _liveParsedUtc = DateTime.MinValue;

    /// <summary>Estimate of the match being played (replay still recorded), or null. Not saved.</summary>
    public MatchRecord? LiveMatch { get; private set; }

    /// <summary>True while a replay is being recorded (a match is in progress).</summary>
    public bool IsLive => _liveFile != null && !_liveNotCounted;

    /// <summary>The replay being recorded is not on the season's map (Creative, OG...): not shown as a match in progress.</summary>
    private bool _liveNotCounted;
    private bool _liveMapKnown;

    private void ClearLive()
    {
        _liveNotCounted = false;
        _liveMapKnown = false;
        _liveFile = null;
        _liveSize = -1;
        LiveMatch = null;
    }

    /// <summary>
    /// Re-reads the replay being recorded when Fortnite has appended data to it (about every 25 s), at most once
    /// every <see cref="Config.LiveIntervalSeconds"/>. Returns true when the live figures changed.
    /// </summary>
    public bool UpdateLive()
    {
        // The newest replay still flagged "live" and written recently (an old one left by a crash is ignored).
        var file = UnprocessedFiles()
            .Where(f => DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(3))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault(f => ReplayHeaderProbe.IsLive(f.FullName) == true);

        if (file == null)
        {
            var had = _liveFile != null;
            // Match over: keep the estimate on screen until the final analysis replaces it, unless it went stale.
            if (had && (!File.Exists(_liveFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(_liveFile!) > TimeSpan.FromMinutes(5)))
            {
                ClearLive();
                return true;
            }
            return false;
        }

        var started = false;
        if (file.FullName != _liveFile)
        {
            ClearLive();
            _liveFile = file.FullName;
            started = true;
        }

        // The map is in the replay header, readable from the first seconds: a match elsewhere than on the season's map
        // (Creative, OG...) is not a match in progress for the counter.
        if (!_liveMapKnown)
        {
            var map = ReplayHeaderProbe.Map(file.FullName);
            if (map == null && !started)
            {
                return false; // header not written yet: try again at the next check
            }
            _liveMapKnown = map != null;
            _liveNotCounted = map != null && !GameModes.IsSeasonMap(map);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(_liveNotCounted
                ? L.T($"{L.Stamp} Match in progress in another mode (map {map}): it will not be counted.", $"{L.Stamp} Идёт матч в другом режиме (карта {map}): он не будет посчитан.")
                : L.T($"{L.Stamp} Match in progress… (its chests will be counted at the end)", $"{L.Stamp} Идёт матч… (сундуки будут подсчитаны в конце)"));
            Console.ResetColor();
            if (_liveNotCounted)
            {
                return true;
            }
        }
        if (_liveNotCounted)
        {
            return started;
        }

        // Fortnite writes the encryption key only at the end of the match: until then the data cannot be read,
        // so we only know that a match is in progress (no useless re-reading).
        if (!_config.LiveUpdate || ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            return started;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _config.LiveIntervalSeconds));
        if (file.Length == _liveSize || DateTime.UtcNow - _liveParsedUtc < interval)
        {
            return started;
        }

        MatchRecord record;
        try
        {
            record = Analyze(file, TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            _liveParsedUtc = DateTime.UtcNow; // try again at the next interval
            if (Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") is { Length: > 0 })
            {
                Console.Error.WriteLine($"DBG live parse failed: {ex}");
            }
            return started;
        }
        _liveSize = file.Length;
        _liveParsedUtc = DateTime.UtcNow;

        var before = LiveMatch;
        LiveMatch = GameModes.Counts(record) ? record : null; // a match in another mode adds nothing
        var changed = before?.ChestsOpened != LiveMatch?.ChestsOpened || before?.TeamChestsOpened != LiveMatch?.TeamChestsOpened || before?.TreasureFinds.Count != LiveMatch?.TreasureFinds.Count || (before == null) != (LiveMatch == null);
        if (changed && LiveMatch != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"{L.Stamp} {L.T("Match in progress", "Идёт матч")}: {L.Count(LiveMatch.ChestsOpened, "chest", "chests", "сундук", "сундука", "сундуков")}"
                + (LiveMatch.TreasureFinds.Count > 0 ? $", {L.Count(LiveMatch.TreasureFinds.Count, "treasure", "treasures", "сокровище", "сокровища", "сокровищ")}" : ""));
            Console.ResetColor();
        }
        return changed || started;
    }

    /// <summary>
    /// When the analysis logic changed, re-analyze the replays still on disk (records are replaced, not duplicated).
    /// Must run before any scan. Returns true when existing matches will be re-analyzed (worth telling the player).
    /// </summary>
    public bool UpgradeIfNeeded()
    {
        if (_data.AnalyzerVersion >= ReplayAnalyzer.CurrentVersion)
        {
            return false;
        }

        var hadData = _data.Matches.Count > 0;
        foreach (var key in _data.ProcessedFiles.Where(kv => kv.Value != "skipped").Select(kv => kv.Key).ToList())
        {
            _data.ProcessedFiles.Remove(key);
        }
        _data.AnalyzerVersion = ReplayAnalyzer.CurrentVersion;
        _store.SaveData(_data);
        return hadData;
    }

    private void PrintMatch(MatchRecord m, bool updated)
    {
        var total = Math.Max(0, Profiles.Counted(_data).Where(x => Profiles.Key(x) == Profiles.Key(m)).Sum(x => x.ChestsOpened) + Profiles.Manual(_data, Profiles.Key(m)));
        var when = L.Shown(m.MatchStartUtc ?? m.AnalyzedAtUtc);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"   {(updated ? "↻" : "✔")} {L.T("Match of", "Матч от")} {L.DateTimeShort(when)}");
        Console.ResetColor();
        Console.Write($" ({Pretty.Playlist(m.Playlist)}, {Pretty.Placement(m)}){L.T(": ", ": ")}");
        Console.ForegroundColor = ConsoleColor.Yellow;
        var destroyed = m.Chests.Count(c => c.Destroyed);
        Console.Write(L.Count(m.ChestsOpened, "chest", "chests", "сундук", "сундука", "сундуков")
            + (destroyed > 0 ? L.T($" ({destroyed} destroyed)", $" (разрушено: {destroyed})") : ""));
        Console.ResetColor();
        Console.WriteLine($"  →  total{L.T(": ", ": ")}{L.N(total)}");

        if (m.TeamChestsOpened > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            var mates = string.Join(", ", m.TeamChests.Where(x => x.Chests > 0).OrderByDescending(x => x.Chests).Select(x => $"{x.Name} {L.N(x.Chests)}"));
            Console.WriteLine($"   + {L.T("Squad", "Отряд")}{L.T(": ", ": ")}{L.Count(m.TeamChestsOpened, "chest", "chests", "сундук", "сундука", "сундуков")} ({mates})");
            Console.ResetColor();
        }
        if (m.VehicleChestsMissing)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(L.T($"   ⚠ Fortnite did not record what your vehicle destroyed in this replay (loot of {m.UnrecordedVehicleLoot} chests with no chest recorded): they cannot be counted. See the help tab.",
                $"   ⚠ Fortnite не записал, что разрушил твой транспорт в этом повторе (добыча из {m.UnrecordedVehicleLoot} сундуков без записанного сундука): их нельзя посчитать. См. вкладку «Справка»."));
            Console.ResetColor();
        }

        foreach (var find in m.TreasureFinds)
        {
            Console.ForegroundColor = find.FromGoldenChest ? ConsoleColor.Yellow : ConsoleColor.Magenta;
            Console.WriteLine($"   {(find.FromGoldenChest ? "★★" : "★")} {L.T("Treasure obtained: ", "Сокровище получено: ")}{find.DisplayName} ({Pretty.Obtained(find)})");
            if (!find.SourceIsChest)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(L.T($"   ⚑ Obtained other than from a chest ({find.SourceLabel}): low reliability, probably an analysis mistake",
                    $"   ⚑ Получено не из сундука ({find.SourceLabel}): низкая достоверность, вероятно, ошибка анализа"));
            }
            Console.ResetColor();
        }
        if (m.TeamTreasureFinds.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine(L.T($"   + {L.Count(m.TeamTreasureFinds.Count, "treasure", "treasures", "сокровище", "сокровища", "сокровищ")} found by the team (estimate, see the report)",
                $"   + {L.Count(m.TeamTreasureFinds.Count, "treasure", "treasures", "сокровище", "сокровища", "сокровищ")} у команды (оценка, см. отчёт)"));
            Console.ResetColor();
        }
    }

    /// <summary>Totals of the profile shown.</summary>
    public void PrintSummary()
    {
        var profile = Profiles.Current(_data, _config);
        var view = Profiles.View(_data, _config);
        var matches = view.Matches;
        var chests = view.CountedChests;
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        var sb = new StringBuilder();
        if (profile != null)
        {
            sb.Append($"{profile.Name}   |   ");
        }
        sb.Append(L.T($"Matches: {L.N(matches.Count)}   |   Chests: {L.N(view.TotalChests)}   |   Treasures: {L.N(treasures)}",
            $"Матчи: {L.N(matches.Count)}   |   Сундуки: {L.N(view.TotalChests)}   |   Сокровища: {L.N(treasures)}"));
        if (view.TeamChests > 0)
        {
            sb.Append($"   |   {L.T("Squad", "Отряд")}{L.T(": ", ": ")}{L.N(view.SquadChests)}");
        }
        if (matches.Count > 0)
        {
            var avg = ((double)chests / matches.Count).ToString("F1", L.Culture);
            sb.Append(L.T($"   |   Average: {avg} / match", $"   |   В среднем: {avg} / матч"));
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  " + sb);
        Console.ResetColor();
    }
}

/// <summary>Reads just enough of the replay file header to know whether the match is still being recorded.</summary>
public static class ReplayHeaderProbe
{
    /// <summary>
    /// The map of the match (e.g. /Delilah/Maps/Delilah_Terrain), from the header chunk: written at the start of the
    /// recording, never encrypted. Null when it cannot be read (yet).
    /// </summary>
    public static string? Map(string path)
    {
        try
        {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // The header is the first chunk: a few kilobytes are enough.
                bytes = new byte[Math.Min(fs.Length, 256 * 1024)];
                fs.ReadExactly(bytes);
            }
            using var ms = new MemoryStream(bytes, writable: false);
            var reader = new FortniteReplayReader.ReplayReader(null, Unreal.Core.Models.Enums.ParseMode.EventsOnly);
            var replay = reader.ReadReplay(ms);
            return replay.Header?.LevelNamesAndTimes?.Select(l => l.Item1).FirstOrDefault(l => !string.IsNullOrEmpty(l));
        }
        catch
        {
            return null;
        }
    }

    private const uint FileMagic = 0x1CA2E27F;

    // Each scan asks several times about the same file: the header is read again only when the file changed.
    private static readonly Dictionary<string, (long Length, long Ticks, bool? Live, bool KeyMissing)> Cache = new();

    /// <summary>
    /// True when the replay is encrypted and its key is not written yet (or the header cannot be read). Fortnite
    /// writes the real key only when the match ends (all zeros before): the data of a match in progress cannot be read.
    /// </summary>
    public static bool KeyMissing(string path) => Read(path).KeyMissing;

    /// <summary>True while the match is being recorded, null when the header cannot be read.</summary>
    public static bool? IsLive(string path) => Read(path).Live;

    private static (bool? Live, bool KeyMissing) Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var (length, ticks) = (info.Length, info.LastWriteTimeUtc.Ticks);
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var c) && c.Length == length && c.Ticks == ticks)
                {
                    return (c.Live, c.KeyMissing);
                }
            }
            var result = ReadHeader(path);
            lock (Cache)
            {
                if (Cache.Count > 256) Cache.Clear();
                Cache[path] = (length, ticks, result.Live, result.KeyMissing);
            }
            return result;
        }
        catch
        {
            return (null, true);
        }
    }

    private static (bool? Live, bool KeyMissing) ReadHeader(string path)
    {
        bool? live = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != FileMagic) return (null, true);
            var fileVersion = br.ReadUInt32();
            if (fileVersion >= 7)
            {
                var count = br.ReadInt32();
                if (count < 0 || count > 64) return (null, true);
                fs.Seek(count * 20L, SeekOrigin.Current);
            }
            fs.Seek(12, SeekOrigin.Current); // LengthInMS, NetworkVersion, Changelist
            var len = br.ReadInt32(); // FriendlyName (FString, UTF-16 when negative)
            fs.Seek(len < 0 ? -2L * len : len, SeekOrigin.Current);
            live = br.ReadUInt32() != 0; // bIsLive
            br.ReadInt64(); // timestamp
            br.ReadUInt32(); // compressed
            if (br.ReadUInt32() == 0) return (live, false); // not encrypted
            var keyLen = br.ReadInt32();
            if (keyLen <= 0 || keyLen > 256) return (live, true);
            return (live, br.ReadBytes(keyLen).All(b => b == 0));
        }
        catch
        {
            return (live, true);
        }
    }
}
