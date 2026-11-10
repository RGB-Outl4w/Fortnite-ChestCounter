using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ChestCounter;

// --standby: the invisible process that waits for Fortnite to start ("open with Fortnite"). No console, nothing else.
if (args.Contains("--standby"))
{
    var dataIndex = Array.IndexOf(args, "--data");
    return OperatingSystem.IsWindows()
        ? FortniteWatch.RunStandby(dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : Store.DefaultFolder())
        : 1;
}

// Windows app: open a console window (or write into the terminal for command line options).
if (OperatingSystem.IsWindows())
{
    NativeConsole.Ensure(attachToParent: args.Any(a => a is "--once" or "--help" or "-h" or "--overlay-preview"));
}
Console.OutputEncoding = Encoding.UTF8;

// A crash must not close the window before the message can be read (the exe is usually double-clicked).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    ErrorLog.Write("Crash", e.ExceptionObject);
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine();
    Console.WriteLine(L.T("Unexpected error: ", "Непредвиденная ошибка: ") + e.ExceptionObject);
    Console.ResetColor();
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine(L.T("Press any key to close.", "Нажми любую клавишу, чтобы закрыть."));
        try { Console.ReadKey(intercept: true); } catch { }
    }
};

// ---- Arguments ---------------------------------------------------------------------------
string? dataDir = null, demosArg = null, langArg = null;
var once = false;
var reset = false;
var auto = false;          // opened by the standby when Fortnite started: closes itself after Fortnite
var afterUpdate = false;   // restarted by the updater: waits for the previous instance to quit
string? previewPath = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data": dataDir = Arg(ref i); break;
        case "--demos": demosArg = Arg(ref i); break;
        case "--lang": langArg = Arg(ref i); break;
        case "--once": once = true; break;
        case "--reset-config": reset = true; break;
        case "--overlay-preview": previewPath = Arg(ref i); break;
        case "--auto": auto = true; break;
        case "--after-update": afterUpdate = true; break;
        case "-h":
        case "--help":
            var langIndex = Array.IndexOf(args, "--lang");
            L.Set(langIndex >= 0 && langIndex + 1 < args.Length ? L.Normalize(args[langIndex + 1]) ?? L.SystemDefault : L.SystemDefault);
            Console.WriteLine(L.T("""
                Fortnite Chest Counter

                Options:
                  --lang <en|ru>      language of the tool (English or Russian)
                  --demos <folder>    replays folder (default %LOCALAPPDATA%\FortniteGame\Saved\Demos)
                  --data <folder>     data folder (default %APPDATA%\ChestCounter)
                  --once              analyze the finished replays, then quit
                  --reset-config      ask the setup questions again
                  --overlay-preview <file.png>   save previews of the overlay, then quit
                """, """
                Счётчик сундуков Fortnite

                Параметры:
                  --lang <en|ru>      язык программы (английский или русский)
                  --demos <папка>     папка с повторами (по умолчанию %LOCALAPPDATA%\FortniteGame\Saved\Demos)
                  --data <папка>      папка с данными (по умолчанию %APPDATA%\ChestCounter)
                  --once              проанализировать завершённые повторы и выйти
                  --reset-config      заново задать вопросы настройки
                  --overlay-preview <файл.png>   сохранить изображения оверлея и выйти
                """));
            return 0;
    }
}

dataDir ??= Store.DefaultFolder();
var store = new Store(dataDir);
ErrorLog.FilePath = store.ErrorLogPath;
var interactive = ConsoleUi.Interactive;
if (interactive && !once && previewPath == null)
{
    ConsoleUi.EnsureSize(110, 40);
}

// One instance at a time: two would count the same matches and fight over the shortcuts.
using var instance = new Mutex(false, AppInfo.InstanceMutexFor(dataDir));
if (previewPath == null && !AcquireInstance(instance, afterUpdate ? TimeSpan.FromSeconds(20) : TimeSpan.Zero))
{
    if (auto)
    {
        return 0; // opened with Fortnite while the player had already opened it: nothing to do
    }
    L.Set(store.LoadConfig()?.Language ?? L.SystemDefault);
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(L.T("  The chest counter is already open: look in the taskbar.",
        "  Счётчик сундуков уже открыт: посмотри на панели задач."));
    Console.ResetColor();
    if (interactive)
    {
        Console.WriteLine(L.T("  (this window closes in 10 seconds)", "  (это окно закроется через 10 секунд)"));
        Thread.Sleep(10_000);
    }
    return 0;
}

// ---- Configuration -----------------------------------------------------------------------
var config = reset ? null : store.LoadConfig();
var firstRun = config == null;
var previousLanguage = reset ? store.LoadConfig()?.Language : null;
config ??= new Config();

// Language: --lang, else the saved choice, else asked once (Windows language when nobody can answer).
if (langArg != null)
{
    config.Language = L.Normalize(langArg) ?? config.Language;
}
if (L.Normalize(config.Language) == null)
{
    var suggested = L.Normalize(previousLanguage) ?? L.SystemDefault;
    if (interactive)
    {
        ConsoleUi.Screen("Fortnite Chest Counter  ·  Счётчик сундуков Fortnite", "1/5");
        ConsoleUi.Text("Choose your language  /  Выбери язык");
        var choice = ConsoleUi.Menu(L.Codes.Select(L.NativeName).ToList(), Math.Max(0, Array.IndexOf(L.Codes, suggested)), "↑ ↓  +  Enter");
        config.Language = L.Codes[choice];
    }
    else
    {
        config.Language = suggested;
    }
}
L.Set(config.Language);
var appName = L.T("Fortnite Chest Counter", "Счётчик сундуков Fortnite");
ConsoleUi.SetTitle($"{appName} {AppInfo.VersionText}");

if (demosArg != null) config.DemosFolder = demosArg;
var defaultDemos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");
if (string.IsNullOrWhiteSpace(config.DemosFolder))
{
    config.DemosFolder = defaultDemos;
}

if (previewPath != null)
{
    // Development: draws the overlay off-screen and saves it as PNG (idle + match in progress), then quits.
    var previewData = Profiles.View(store.LoadData(), config);
    // In memory only: the 3 latest matches count as "this session", to show a realistic session line.
    foreach (var m in previewData.Matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).Take(3))
    {
        m.MatchStartUtc = DateTime.UtcNow;
    }
    if (!OperatingSystem.IsWindows())
    {
        return 1;
    }
    NativeOverlay.SetAppearance(config.OverlaySize, config.OverlayOpacity);
    // Locked, as in game. Each style in its three states: idle, match in progress, live count.
    NativeOverlay.Start(-20000, -20000, true, (_, _) => { }, _ => { });
    var fakeLive = new MatchRecord { ChestsOpened = 12, TreasureFinds = { new TreasureFind { Item = "Trésor" /* stored key, see L.Name */ } } };
    foreach (var style in NativeOverlay.Styles)
    {
        NativeOverlay.SetStyle(style);
        foreach (var (suffix, live, inProgress) in new[] { ("-idle", (MatchRecord?)null, false), ("-inprogress", null, true), ("-live", fakeLive, true) })
        {
            NativeOverlay.Update(OverlayModel.Build(previewData, live, inProgress));
            Thread.Sleep(600);
            var name = Path.GetFileNameWithoutExtension(previewPath) + (style == "classic" ? "" : "-" + style) + suffix + ".png";
            var file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(previewPath)) ?? ".", name);
            Console.WriteLine(NativeOverlay.Snapshot(file) ? L.T($"Preview: {file}", $"Предпросмотр: {file}") : L.T($"Capture failed: {file}", $"Не удалось сохранить снимок: {file}"));
        }
    }
    return 0;
}

LowerPriority();
var data = store.LoadData();
var tracker = new Tracker(store, config, data);
// Before any scan: otherwise replays analyzed during the setup would be analyzed a second time right after.
var reanalyzing = tracker.UpgradeIfNeeded();
var reportOpened = false;

// ---- First launch: setup screens ----------------------------------------------------------
if (firstRun && interactive)
{
    // Step 2: the replays folder.
    ConsoleUi.Screen(appName, "2/5");
    ConsoleUi.Text(L.T("Your replays", "Твои повторы"), ConsoleColor.White);
    ConsoleUi.Blank();
    if (!Directory.Exists(config.DemosFolder))
    {
        ConsoleUi.Text(L.T("✖ Replays folder not found:", "✖ Папка с повторами не найдена:"), ConsoleColor.Red);
        ConsoleUi.Hint("  " + config.DemosFolder);
        ConsoleUi.Blank();
        ConsoleUi.Text(L.T("Fortnite creates it with the first recorded match. If you just turned replays on, that is expected.",
            "Fortnite создаёт её при первом записанном матче. Если ты только что включил запись повторов, это нормально."));
        var where = ConsoleUi.Menu(new[]
        {
            L.T("Keep this folder", "Оставить эту папку"),
            L.T("Enter another folder", "Указать другую папку"),
        });
        if (where == 1)
        {
            config.DemosFolder = ConsoleUi.Input(L.T("Path of the replays folder: ", "Путь к папке с повторами: "), config.DemosFolder);
            ConsoleUi.Blank();
        }
    }
    else
    {
        ConsoleUi.Text(L.T("✔ Replays folder found:", "✔ Папка с повторами найдена:"), ConsoleColor.Green);
        ConsoleUi.Hint("  " + config.DemosFolder);
    }
    ConsoleUi.Blank();
    ConsoleUi.Text(L.T("Important: in Fortnite, turn on  Settings → Game → Replays → Record Replays.",
        "Важно: в Fortnite включи  Настройки → Игра → Повторы → Записывать повторы."), ConsoleColor.Yellow);
    ConsoleUi.Hint(L.T("Without it, Fortnite saves no replay and the tool has nothing to count.",
        "Без этого Fortnite не сохраняет повторы, и программе нечего считать."));
    ConsoleUi.Blank();

    // Replays already on the PC.
    var existing = tracker.UnprocessedFiles().Where(f => ReplayHeaderProbe.IsLive(f.FullName) != true).ToList();
    var analyzeNow = false;
    if (existing.Count > 0)
    {
        var present = L.Count(existing.Count, "replay is already on your PC", "replays are already on your PC", "повтор уже есть на твоём ПК", "повтора уже есть на твоём ПК", "повторов уже есть на твоём ПК");
        ConsoleUi.Text(L.T($"{present}. Count them too?", $"{present}. Посчитать и их?"));
        analyzeNow = ConsoleUi.Menu(new[]
        {
            L.T("Yes, analyze them now", "Да, проанализировать сейчас"),
            L.T("No, only my next matches", "Нет, только следующие матчи"),
        }) == 0;
        if (!analyzeNow)
        {
            tracker.MarkSkipped(existing);
        }
    }
    else
    {
        ConsoleUi.WaitEnter(L.T("Enter to continue", "Enter — продолжить"));
    }
    store.SaveConfig(config);

    if (analyzeNow)
    {
        ConsoleUi.Screen(appName, L.T("analysis", "анализ"));
        ConsoleUi.Text(L.T("Analyzing your replays…", "Анализирую твои повторы…"), ConsoleColor.White);
        ConsoleUi.Hint(L.T("The report opens in your browser and fills in as the replays are read.",
            "Отчёт открывается в браузере и заполняется по мере чтения повторов."));
        ConsoleUi.Blank();
        ReportWriter.Progress = new ScanProgress(0, existing.Count);
        ReportWriter.WriteAll(store, config, data);
        if (config.OpenReportOnStart)
        {
            Open(store.ReportPath);
            reportOpened = true;
        }
        tracker.Scan();
        tracker.NewProfilesFound = false;
        ConsoleUi.Blank();
        if (Profiles.List(data).Count < 2)
        {
            ConsoleUi.WaitEnter(L.T("Enter to continue", "Enter — продолжить"));
        }
    }

    if (Profiles.List(data).Count >= 2)
    {
        ConsoleUi.Screen(appName, L.T("profile", "профиль"));
        ChooseProfile(data, config, store);
    }

    // Step 3: the overlay (some players only want the report).
    if (OperatingSystem.IsWindows())
    {
        ConsoleUi.Screen(appName, "3/5");
        ConsoleUi.Text(L.T("Show an overlay in game?", "Показывать оверлей в игре?"), ConsoleColor.White);
        ConsoleUi.Blank();
        ConsoleUi.Text(L.T("A small always-on-top window shows your chests while you play.",
            "Небольшое окно поверх всех окон показывает твои сундуки, пока ты играешь."));
        ConsoleUi.Hint(L.T("Without it, your statistics are in the report. Can be changed at any time in the settings.",
            "Без него статистика остаётся в отчёте. Это можно изменить в любой момент в настройках."));
        config.ShowOverlay = ConsoleUi.Menu(new[]
        {
            L.T("Yes, show the overlay", "Да, показывать оверлей"),
            L.T("No, only the report", "Нет, только отчёт"),
        }) == 0;
        store.SaveConfig(config);
    }

    // Step 4: open with Fortnite. Nothing is recommended: most players simply start it themselves.
    if (OperatingSystem.IsWindows() && AppInfo.ExePath != null)
    {
        ConsoleUi.Screen(appName, "4/5");
        ConsoleUi.Text(L.T("Open the counter automatically when Fortnite starts?", "Открывать счётчик автоматически при запуске Fortnite?"), ConsoleColor.White);
        ConsoleUi.Blank();
        ConsoleUi.Text(L.T("A tiny invisible process waits for Fortnite to start, then opens the counter.\nThe counter closes by itself after Fortnite.",
            "Крошечный невидимый процесс ждёт запуска Fortnite и открывает счётчик.\nСчётчик закрывается сам после Fortnite."));
        ConsoleUi.Hint(L.T("Can be changed at any time in the settings. Turn it off before deleting ChestCounter.exe.",
            "Это можно изменить в любой момент в настройках. Отключи перед удалением ChestCounter.exe."));
        var withFortnite = ConsoleUi.Menu(new[]
        {
            L.T("No, I will start it myself", "Нет, буду запускать сам"),
            L.T("Yes, open it with Fortnite", "Да, открывать вместе с Fortnite"),
        }) == 1;
        config.StartWithFortnite = withFortnite && FortniteWatch.Apply(true, dataDir);
        store.SaveConfig(config);
    }

    // Step 5: ready.
    ConsoleUi.Screen(appName, "5/5");
    ConsoleUi.Text(L.T("All set!", "Всё готово!"), ConsoleColor.Green);
    ConsoleUi.Blank();
    ConsoleUi.Text(L.T("Keep this window open (you can minimize it) while you play:\nthe chests of each match are counted a few seconds after it ends.",
        "Оставь это окно открытым (его можно свернуть), пока играешь:\nсундуки каждого матча подсчитываются через несколько секунд после его окончания."));
    ConsoleUi.Blank();
    ConsoleUi.Text(L.T("Everything works with the arrow keys and Enter. Settings and help are in the tabs at the top of the screen.",
        "Всё управляется стрелками и Enter. Настройки и справка — во вкладках вверху экрана."));
    if (config.ShowOverlay)
    {
        ConsoleUi.Blank();
        ConsoleUi.Text(L.T("Overlay: Ctrl+Shift+O to show or hide it, Ctrl+Shift+L to lock it (even in game).",
            "Оверлей: Ctrl+Shift+O — показать или скрыть, Ctrl+Shift+L — закрепить (даже в игре)."));
        ConsoleUi.Hint(L.T("To see it in game, set Fortnite to \"Windowed Fullscreen\" (Settings → Video → Window Mode).",
            "Чтобы видеть его в игре, переключи Fortnite в режим «Оконный полноэкранный» (Настройки → Изображение → Режим окна)."));
    }
    ConsoleUi.WaitEnter(L.T("Press Enter to start", "Нажми Enter, чтобы начать"));
}
else
{
    store.SaveConfig(config);
    if (interactive && !once && Profiles.List(data).Count >= 2)
    {
        ConsoleUi.Screen(appName, L.T("profile", "профиль"));
        ChooseProfile(data, config, store);
    }
}

// ---- Command line: analyze once and quit ---------------------------------------------------
if (once)
{
    ConsoleUi.Screen(appName, $"v{AppInfo.VersionText}");
    tracker.Scan(ignoreQuietPeriod: true);
    ReportWriter.WriteAll(store, config, data);
    tracker.PrintSummary();
    Console.WriteLine(L.T($"  Report: {store.ReportPath}", $"  Отчёт: {store.ReportPath}"));
    return 0;
}

// ---- Main screen ---------------------------------------------------------------------------
// One screen, redrawn in place: tabs (home / settings / help), a menu driven by the arrow keys, and an activity
// area that shows what the counter prints (captured from Console.Out).
var quit = false;
var restart = false;
var wake = new AutoResetEvent(false);
var keys = new BlockingCollection<ConsoleKeyInfo>();
var log = new ActivityLog();
var mainThread = Environment.CurrentManagedThreadId;
var dashboard = false;
var lastDraw = DateTime.MinValue;
var tab = 0;
var selection = new int[3];
UpdateInfo? update = null;
var updateAnnounced = false;
var notesHidden = false;
var showWhatsNew = !firstRun && config.LastVersion != AppInfo.VersionText;
if (!showWhatsNew && config.LastVersion != AppInfo.VersionText)
{
    config.LastVersion = AppInfo.VersionText;
    store.SaveConfig(config);
}
DateTime? autoCloseAt = null;
DateTime? fortniteClosedSince = null;
FileSystemWatcher? watcher = null;
var watcherFired = false;
var nextScan = DateTime.UtcNow.AddSeconds(2);

if (interactive)
{
    log.Changed += () =>
    {
        if (!dashboard)
        {
            return;
        }
        if (Environment.CurrentManagedThreadId == mainThread)
        {
            // While a scan runs (progress bar), redraw at most ~12 times a second.
            if ((DateTime.UtcNow - lastDraw).TotalMilliseconds >= 80) Draw();
        }
        else
        {
            wake.Set(); // written by another thread (overlay): the main loop redraws
        }
    };
    Console.SetOut(log);
    dashboard = true;
    try { Console.Clear(); } catch { }
    Frames.Invalidate();
    Draw();
}

if (reanalyzing)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(L.T("New analysis version: the replays still on disk will be analyzed again (no duplicates).",
        "Новая версия анализа: повторы, оставшиеся на диске, будут проанализированы заново (без дублей)."));
    Console.ResetColor();
}

if (config.ShowOverlay)
{
    StartOverlay();
}

if (OperatingSystem.IsWindows())
{
    Updater.CleanupOld();
    if (config.StartWithFortnite)
    {
        FortniteWatch.Apply(true, dataDir); // follows the exe if it was moved or updated, and restarts the standby
    }
    if (Process.GetProcessesByName("ChestTracker") is { Length: > 0 } oldTool)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(L.T("The old version (ChestTracker.exe) is still open: close it, otherwise both count at the same time.",
            "Старая версия (ChestTracker.exe) всё ещё открыта: закрой её, иначе обе считают одновременно."));
        Console.ResetColor();
        foreach (var p in oldTool) p.Dispose();
    }
}

// Nothing to read yet: explain why, so that a new user is not left wondering.
if (!tracker.ReplayFiles().Any())
{
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.WriteLine(Directory.Exists(config.DemosFolder)
        ? L.T("No replay yet: that is expected if you have not played since replays were turned on.",
              "Повторов пока нет: это нормально, если ты ещё не играл после включения записи повторов.")
        : L.T("The replays folder does not exist yet: Fortnite creates it with the first recorded match.",
              "Папки с повторами ещё нет: Fortnite создаёт её при первом записанном матче."));
    Console.WriteLine(L.T("Check in Fortnite: Settings → Game → Replays → Record Replays.",
        "Проверь в Fortnite: Настройки → Игра → Повторы → Записывать повторы."));
    Console.ResetColor();
}

// Replays left from earlier: the report opens with a loading card, the screen shows a progress bar.
var backlog = tracker.BacklogCount();
if (backlog > 1)
{
    ReportWriter.Progress = new ScanProgress(0, backlog);
}
ReportWriter.WriteAll(store, config, data);
if (config.OpenReportOnStart && !reportOpened && !auto)
{
    Open(store.ReportPath);
}
if (backlog > 0)
{
    tracker.Scan();
    ReportWriter.Progress = null;
    ReportWriter.WriteAll(store, config, data);
}
AnnounceNewProfiles();
UpdateOverlay(store, config, data, tracker.LiveMatch, tracker.IsLive);

if (auto && OperatingSystem.IsWindows() && !(config.MinimizeToTray && interactive))
{
    NativeConsole.Minimize(); // opened with Fortnite: the game keeps the screen
}

// New version on GitHub (checked in the background; the only network access of the tool).
if (config.UpdateMode != "off")
{
    _ = Task.Run(async () =>
    {
        var found = await Updater.CheckAsync();
        if (found != null)
        {
            Volatile.Write(ref update, found);
            wake.Set();
        }
    });
}

Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit = true; wake.Set(); };

// Minimized: into the notification area (classic console window only).
if (interactive && OperatingSystem.IsWindows())
{
    Tray.Enabled = config.MinimizeToTray;
    Tray.OpenReport = () => Open(store.ReportPath);
    Tray.Quit = () => { quit = true; wake.Set(); };
    Tray.Start();
    if (auto && config.MinimizeToTray && Tray.Available)
    {
        Tray.Hide(notify: false); // opened with Fortnite: straight to the tray, the game keeps the screen
    }
    else if (auto && config.MinimizeToTray)
    {
        NativeConsole.Minimize(); // no tray for this window (terminal): minimized as before
    }
}

// Keyboard: one thread blocked on ReadKey (no polling). Menus opened from the main loop read the same queue.
if (interactive)
{
    new Thread(() =>
    {
        while (true)
        {
            try { keys.Add(Console.ReadKey(intercept: true)); wake.Set(); }
            catch { return; }
        }
    }) { IsBackground = true, Name = "Keyboard" }.Start();
    ConsoleUi.ReadKey = () => keys.Take();
}

WatchReplays();

nextScan = DateTime.UtcNow.AddSeconds(2);
while (!quit)
{
    if (DateTime.UtcNow >= nextScan)
    {
        try
        {
            tracker.Scan();
            AnnounceNewProfiles();
            // Match in progress: re-read the replay being recorded (throttled inside).
            tracker.UpdateLive();
            UpdateOverlay(store, config, data, tracker.LiveMatch, tracker.IsLive);
        }
        catch (Exception ex)
        {
            Console.WriteLine(L.T($"Error while watching: {ex.Message}", $"Ошибка при наблюдении: {ex.Message}"));
        }
        // A replay being written: check again soon. Otherwise only a safety scan once a minute.
        nextScan = DateTime.UtcNow.AddSeconds(tracker.IsFinishing ? 2 : tracker.HasPending ? 15 : (watcher != null && !auto ? 60 : 10));
    }

    if (auto && OperatingSystem.IsWindows())
    {
        CheckAutoClose();
    }

    var pendingUpdate = Volatile.Read(ref update);
    if (pendingUpdate != null && !updateAnnounced)
    {
        var idle = !tracker.IsLive && !tracker.HasPending && !tracker.IsFinishing;
        if (config.UpdateMode == "auto")
        {
            if (idle && AppInfo.ExePath != null)
            {
                updateAnnounced = true;
                InstallUpdate(pendingUpdate);
            }
        }
        else
        {
            updateAnnounced = true;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(L.T($"New version {pendingUpdate.Version.ToString(3)} available: what's new is shown on the home tab.",
                $"Доступна новая версия {pendingUpdate.Version.ToString(3)}: что нового — на главной вкладке."));
            Console.ResetColor();
        }
    }

    if (quit)
    {
        break;
    }
    Draw();

    // Sleep until something happens (file change, key press) or the next scheduled scan. At most one second, so that
    // a resized window or the auto-close countdown are redrawn.
    var wait = nextScan - DateTime.UtcNow;
    if (interactive && wait > TimeSpan.FromSeconds(1))
    {
        wait = TimeSpan.FromSeconds(1);
    }
    if (wait > TimeSpan.Zero && wake.WaitOne(wait))
    {
        // A file change: check again within 2 s (the readiness checks decide when a replay is complete).
        if (keys.Count == 0 && !quit && watcherFired)
        {
            watcherFired = false;
            nextScan = DateTime.UtcNow.AddSeconds(2) < nextScan ? DateTime.UtcNow.AddSeconds(2) : nextScan;
        }
    }

    while (!quit && keys.TryTake(out var key))
    {
        if (autoCloseAt != null)
        {
            // Any key keeps the counter open after Fortnite.
            autoCloseAt = null;
            auto = false;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(L.T("The counter stays open.", "Счётчик остаётся открытым."));
            Console.ResetColor();
            continue;
        }
        HandleKey(key);
    }
}

dashboard = false;
watcher?.Dispose();
if (OperatingSystem.IsWindows())
{
    NativeOverlay.Stop();
    Tray.Stop();
}
Console.SetOut(ConsoleUi.Out);
if (interactive)
{
    try { Console.Clear(); } catch { }
    ConsoleUi.RestoreCursor(true);
}

if (restart && AppInfo.ExePath is { } newExe)
{
    try { instance.ReleaseMutex(); } catch { }
    var restartArgs = string.Join(" ", args.Where(a => a is not ("--after-update" or "--reset-config")).Select(a => a.Contains(' ') ? $"\"{a}\"" : a).Append("--after-update"));
    Process.Start(new ProcessStartInfo(newExe, restartArgs) { UseShellExecute = true })?.Dispose();
    return 0;
}
if (restart)
{
    Console.WriteLine(L.T("  Start the counter again to continue.", "  Запусти счётчик снова, чтобы продолжить."));
}

Console.WriteLine(L.T("  See you soon!", "  До встречи!"));
return 0;

// ---- Main screen: drawing ---------------------------------------------------------------------

// A menu entry of the main screen: its label, what Enter does, and a line that explains it.
static (string Label, Action Run, string? About) Item(string label, Action run, string? about = null) => (label, run, about);

List<(string Label, Action Run, string? About)> Items()
{
    var list = new List<(string Label, Action Run, string? About)>();
    switch (tab)
    {
        case 0:
            list.Add(Item(L.T("Open the report (detailed statistics)", "Открыть отчёт (подробная статистика)"),
                () => Open(store.ReportPath)));
            if (OperatingSystem.IsWindows() && NativeOverlay.IsRunning)
            {
                list.Add(Item(NativeOverlay.IsVisible ? L.T("Hide the overlay", "Скрыть оверлей") : L.T("Show the overlay", "Показать оверлей"),
                    NativeOverlay.Toggle));
                list.Add(Item(NativeOverlay.IsLocked ? L.T("Unlock the overlay (to move it)", "Открепить оверлей (чтобы переместить)")
                        : L.T("Lock the overlay (the mouse goes through it)", "Закрепить оверлей (мышь проходит сквозь него)"),
                    NativeOverlay.ToggleLock));
            }
            if (Profiles.List(data).Count >= 2)
            {
                list.Add(Item(L.T("Switch profile", "Сменить профиль"), () =>
                {
                    ConsoleUi.Screen(appName, L.T("profile", "профиль"));
                    ChooseProfile(data, config, store);
                    ReportWriter.WriteAll(store, config, data);
                    UpdateOverlay(store, config, data, tracker.LiveMatch, tracker.IsLive);
                }));
            }
            if (Volatile.Read(ref update) is { } u)
            {
                list.Add(Item(L.T($"Install version {u.Version.ToString(3)}", $"Установить версию {u.Version.ToString(3)}"),
                    () => InstallUpdate(u)));
                list.Add(notesHidden
                    ? Item(L.T("See what's new", "Посмотреть, что нового"), () => notesHidden = false)
                    : Item(L.T("Later (hide what's new)", "Позже (скрыть список изменений)"), () => notesHidden = true));
            }
            else if (config.UpdateMode != "off")
            {
                list.Add(Item(L.T("Check for updates", "Проверить обновления"), CheckForUpdate));
            }
            if (showWhatsNew && Volatile.Read(ref update) == null)
            {
                list.Add(Item(L.T("OK, hide what's new", "ОК, скрыть список изменений"), () =>
                {
                    showWhatsNew = false;
                    config.LastVersion = AppInfo.VersionText;
                    store.SaveConfig(config);
                }));
            }
            list.Add(Item(L.T("Quit", "Выйти"), () => quit = true));
            break;

        case 1:
            string YesNo(bool b) => b ? L.T("yes", "да") : L.T("no", "нет");
            string Percent(int p) => L.T($"{p}%", $"{p} %");
            list.Add(Item($"{L.T("Language", "Язык")} : {L.NativeName(L.Code)}", CycleLanguage,
                L.T("Language of the counter, the report and the overlay. Enter for the next one.",
                    "Язык счётчика, отчёта и оверлея. Enter — следующий.")));
            if (OperatingSystem.IsWindows())
            {
                list.Add(Item($"{L.T("In-game overlay", "Оверлей в игре")} : {(config.ShowOverlay ? L.T("on", "включён") : L.T("off", "выключен"))}",
                    ToggleOverlay,
                    L.T("Small always-on-top window that shows your chests in game. Off: no window and no shortcut, everything stays in the report.",
                        "Небольшое окно поверх всех окон, которое показывает твои сундуки в игре. Выключен: ни окна, ни горячих клавиш, всё остаётся в отчёте.")));
                if (config.ShowOverlay)
                {
                    string StyleName(string s) => s == "classic"
                        ? L.T("classic (dark card)", "классический (тёмная карточка)")
                        : L.T("HUD (blends into the game)", "HUD (сливается с игрой)");
                    list.Add(Item($"{L.T("Overlay style", "Стиль оверлея")} : {StyleName(config.OverlayStyle)}", () =>
                    {
                        var styles = NativeOverlay.Styles;
                        config.OverlayStyle = styles[(Array.IndexOf(styles, config.OverlayStyle) + 1) % styles.Length];
                        NativeOverlay.SetStyle(config.OverlayStyle);
                    }, L.T("HUD: only the figures remain, with no background, like the game's (put it under the minimap; a background shows while it is unlocked, to move it). Classic: the dark card of earlier versions. Enter to switch, applied at once.",
                        "HUD: остаются только цифры, без фона, как в самой игре (поставь его под мини-картой; пока он откреплён, появляется фон, чтобы его можно было перетащить). Классический: тёмная карточка из прежних версий. Enter — переключить, применяется сразу.")));
                    list.Add(Item($"{L.T("Overlay size", "Размер оверлея")} : {Percent(config.OverlaySize)}", () =>
                    {
                        config.OverlaySize = OverlayAppearance.Next(OverlayAppearance.Sizes, config.OverlaySize);
                        ApplyOverlayAppearance();
                    }, L.T("Enter for the next size (applied at once).", "Enter — следующий размер (применяется сразу).")));
                    list.Add(Item($"{L.T("Overlay opacity", "Прозрачность оверлея")} : {Percent(config.OverlayOpacity)}", () =>
                    {
                        config.OverlayOpacity = OverlayAppearance.Next(OverlayAppearance.Opacities, config.OverlayOpacity);
                        ApplyOverlayAppearance();
                    }, L.T("Enter for the next opacity (applied at once).", "Enter — следующее значение (применяется сразу).")));
                }
            }
            if (OperatingSystem.IsWindows())
            {
                list.Add(Item($"{L.T("Minimize to the notification area", "Сворачивать в область уведомлений")} : {YesNo(config.MinimizeToTray)}",
                    () => Tray.Enabled = config.MinimizeToTray = !config.MinimizeToTray,
                    L.T("Yes: minimizing the window puts it next to the clock (chest icon) instead of the taskbar. A click on the icon opens it again.",
                        "Да: при сворачивании окно уходит к часам (значок сундука), а не на панель задач. Щелчок по значку открывает его снова.")));
            }
            list.Add(Item($"{L.T("Open the report at startup", "Открывать отчёт при запуске")} : {YesNo(config.OpenReportOnStart)}",
                () => config.OpenReportOnStart = !config.OpenReportOnStart,
                L.T("Opens the report in your browser each time the counter starts.", "Открывает отчёт в браузере при каждом запуске счётчика.")));
            if (OperatingSystem.IsWindows())
            {
                list.Add(Item($"{L.T("Open with Fortnite", "Открывать вместе с Fortnite")} : {YesNo(config.StartWithFortnite)}", ToggleStartWithFortnite,
                    L.T("A tiny invisible process waits for Fortnite and opens the counter, which closes afterwards. It keeps running when you quit the counter: set this to \"no\" before deleting or moving ChestCounter.exe.",
                        "Крошечный невидимый процесс ждёт Fortnite и открывает счётчик, который потом закрывается. Он продолжает работать после выхода из счётчика: поставь «нет» перед удалением или перемещением ChestCounter.exe.")));
            }
            var updates = config.UpdateMode switch
            {
                "auto" => L.T("installed automatically", "устанавливаются автоматически"),
                "off" => L.T("off", "отключены"),
                _ => L.T("offered", "предлагаются"),
            };
            list.Add(Item($"{L.T("Updates", "Обновления")} : {updates}",
                () => config.UpdateMode = config.UpdateMode switch { "prompt" => "auto", "auto" => "off", _ => "prompt" },
                L.T("Checked on GitHub at startup (the only network access of the tool). Offered: what's new is shown and you choose. Automatic: installed between two matches.",
                    "Проверяются на GitHub при запуске (единственное обращение программы к сети). Предлагаются: показывается список изменений, решаешь ты. Автоматически: устанавливаются между матчами.")));
            list.Add(Item($"{L.T("Replays folder", "Папка с повторами")} : {config.DemosFolder}", ChangeDemosFolder,
                L.T("Where Fortnite saves its replays. Only change it if you moved it. Enter to edit.",
                    "Куда Fortnite сохраняет повторы. Меняй, только если переносил её. Enter — изменить.")));
            list.Add(Item(L.T("Start the setup again", "Пройти настройку заново"), ResetConfiguration,
                L.T("Clears your settings and asks the first-launch questions again. Your match history is kept.",
                    "Сбрасывает настройки и заново задаёт вопросы первого запуска. История матчей сохраняется.")));
            break;

        case 2:
            list.Add(Item(L.T("Open the data folder", "Открыть папку с данными"), () => Open(store.Folder),
                L.T($"History, settings and report: {store.Folder}", $"История, настройки и отчёт: {store.Folder}")));
            if (File.Exists(store.ErrorLogPath))
            {
                list.Add(Item(L.T("Open the error log", "Открыть журнал ошибок"), () => Open(store.ErrorLogPath),
                    L.T("Attach it when you report a problem.", "Приложи его, когда сообщаешь о проблеме.")));
            }
            list.Add(Item(L.T("Report a problem (GitHub)", "Сообщить о проблеме (GitHub)"), () => Open(AppInfo.IssuesUrl), AppInfo.IssuesUrl));
            list.Add(Item(L.T("Project page (GitHub)", "Страница проекта (GitHub)"), () => Open(AppInfo.RepoUrl), AppInfo.RepoUrl));
            break;
    }
    return list;
}

string[] TabNames() => new[]
{
    L.T("Home", "Главная"),
    L.T("Settings", "Настройки"),
    L.T("Help", "Справка"),
};

void Draw()
{
    if (!dashboard || !interactive)
    {
        return;
    }
    lastDraw = DateTime.UtcNow;
    var height = ConsoleUi.Height;
    var width = ConsoleUi.Width;
    var f = new Frame();
    f.Blank();
    f.Add(new Seg($"  {appName}", ConsoleColor.Yellow), new Seg($"   v{AppInfo.VersionText}", ConsoleColor.DarkGray));
    f.Add("  " + new string('─', Math.Min(width - 4, 76)), ConsoleColor.DarkGray);

    // Tabs.
    var names = TabNames();
    var tabLine = new List<Seg> { new("  ") };
    for (var i = 0; i < names.Length; i++)
    {
        tabLine.Add(i == tab ? new Seg($" {names[i]} ", ConsoleColor.Black, ConsoleColor.Yellow) : new Seg($" {names[i]} ", ConsoleColor.Gray));
        tabLine.Add(new Seg("  "));
    }
    f.Add(tabLine.ToArray());
    f.Blank();

    var items = Items();
    selection[tab] = Math.Clamp(selection[tab], 0, items.Count - 1);

    // Footer (kept at the bottom of the window).
    var footer = new List<Seg[]>();
    if (items[selection[tab]].About is { Length: > 0 } about)
    {
        footer.Add(Array.Empty<Seg>());
        footer.AddRange(ConsoleUi.Wrap(about, width - 6).Select(l => new[] { new Seg("  " + l, ConsoleColor.Gray) }));
    }
    footer.Add(Array.Empty<Seg>());
    footer.Add(new[] { new Seg("  " + L.T("↑ ↓ choose   Enter confirm   ← → tabs", "↑ ↓ выбор   Enter подтвердить   ← → вкладки"), ConsoleColor.DarkGray) });
    if (OperatingSystem.IsWindows() && NativeOverlay.IsRunning)
    {
        var o = L.T("Ctrl+Shift+O", "Ctrl+Shift+O") + (NativeOverlay.HotkeyRegistered ? "" : L.T(" (unavailable)", " (недоступно)"));
        var l = L.T("Ctrl+Shift+L", "Ctrl+Shift+L") + (NativeOverlay.LockHotkeyRegistered ? "" : L.T(" (unavailable)", " (недоступно)"));
        footer.Add(new[] { new Seg("  " + L.T($"Overlay, even in game: {o} show / hide   {l} lock", $"Оверлей, даже в игре: {o} показать / скрыть   {l} закрепить"), ConsoleColor.DarkGray) });
    }

    // What's new gets the room left once the menu, the footer and a few lines of activity are placed.
    var noteRoom = height - 1 - f.Lines.Count - 7 - items.Count - footer.Count - 4;

    switch (tab)
    {
        case 0: DrawHome(f, noteRoom); break;
        case 2: DrawHelp(f); break;
    }

    // Menu.
    for (var i = 0; i < items.Count; i++)
    {
        f.Add(i == selection[tab]
            ? new Seg($"  › {items[i].Label} ", ConsoleColor.Black, ConsoleColor.Yellow)
            : new Seg($"    {items[i].Label}", ConsoleColor.Gray));
    }

    // Activity (home tab): what the counter did lately, in the space left.
    var room = height - 1 - f.Lines.Count - footer.Count;
    if (tab == 0 && room >= 3)
    {
        f.Blank();
        f.Add("  " + L.T("Activity", "Активность"), ConsoleColor.DarkGray);
        foreach (var line in log.Last(room - 2))
        {
            f.Add(new[] { new Seg("  ") }.Concat(line).ToArray());
        }
    }
    while (f.Lines.Count < height - 1 - footer.Count)
    {
        f.Blank();
    }
    foreach (var line in footer)
    {
        f.Add(line);
    }
    Frames.Draw(f);
}

void DrawHome(Frame f, int noteRoom)
{
    var view = Profiles.View(data, config);
    var matches = view.Matches;
    var profile = Profiles.Current(data, config);
    var profiles = Profiles.List(data);
    var labelWidth = new[] { L.T("Profile", "Профиль"), L.T("Status", "Состояние"), L.T("Last match", "Последний матч") }.Max(x => x.Length) + 3;
    void Row(string label, params Seg[] value) => f.Add(new[] { new Seg("  " + label.PadRight(labelWidth), ConsoleColor.DarkGray) }.Concat(value).ToArray());

    Row(L.T("Profile", "Профиль"), new Seg(profile?.Name ?? L.T("created with your first match", "создаётся после первого матча"), ConsoleColor.White),
        new Seg(profiles.Count >= 2 ? L.T($"   ({profiles.Count} accounts on this PC)", $"   (аккаунтов на этом ПК: {profiles.Count})") : "", ConsoleColor.DarkGray));

    // State: what the counter is doing right now.
    if (autoCloseAt is { } closeAt)
    {
        var s = Math.Max(0, (int)Math.Ceiling((closeAt - DateTime.UtcNow).TotalSeconds));
        Row(L.T("Status", "Состояние"), new Seg(L.T($"Fortnite is closed: the counter closes in {s} s. Press any key to keep it open.",
            $"Fortnite закрыт: счётчик закроется через {s} с. Нажми любую клавишу, чтобы оставить его открытым."), ConsoleColor.Yellow));
    }
    else if (tracker.IsLive)
    {
        var live = tracker.LiveMatch;
        Row(L.T("Status", "Состояние"), new Seg("● " + L.T("Match in progress", "Идёт матч"), ConsoleColor.Red),
            new Seg(live != null
                ? L.T($": {L.Count(live.ChestsOpened, "chest", "chests", "сундук", "сундука", "сундуков")} so far", $": пока {L.Count(live.ChestsOpened, "chest", "chests", "сундук", "сундука", "сундуков")}")
                : L.T(": its chests will be counted at the end", ": сундуки будут подсчитаны в конце"), ConsoleColor.Gray));
    }
    else if (tracker.IsFinishing || tracker.HasPending)
    {
        Row(L.T("Status", "Состояние"), new Seg(L.T("Match over: counting the chests…", "Матч окончен: считаю сундуки…"), ConsoleColor.Cyan));
    }
    else
    {
        Row(L.T("Status", "Состояние"), Directory.Exists(config.DemosFolder)
            ? new Seg(L.T("Waiting for a match", "Жду матч"), ConsoleColor.Green)
            : new Seg(L.T("Replays folder not found (see help)", "Папка с повторами не найдена (см. справку)"), ConsoleColor.DarkYellow));
    }

    var chests = matches.Sum(m => m.ChestsOpened);
    var treasures = matches.Sum(m => m.TreasureFinds.Count);
    var avg = matches.Count > 0 ? ((double)chests / matches.Count).ToString("F1", L.Culture) : "0";
    Row(L.T("Total", "Всего"), new Seg(L.Count(chests, "chest", "chests", "сундук", "сундука", "сундуков"), ConsoleColor.Yellow),
        new Seg($"  ·  {L.Count(matches.Count, "match", "matches", "матч", "матча", "матчей")}  ·  {L.T($"{avg} per match", $"{avg} за матч")}", ConsoleColor.Gray),
        new Seg(treasures > 0 ? $"  ·  ★ {L.Count(treasures, "treasure", "treasures", "сокровище", "сокровища", "сокровищ")}" : "", ConsoleColor.Magenta),
        new Seg(treasures > 0 ? L.T($" (1 every {L.N((int)Math.Round((double)chests / treasures))} chests)", $" (1 на {L.N((int)Math.Round((double)chests / treasures))} сунд.)") : "", ConsoleColor.DarkGray));
    var session = matches.Where(ReportWriter.IsThisSession).ToList();
    Row(L.T("Session", "Сессия"), new Seg(L.Count(session.Sum(m => m.ChestsOpened), "chest", "chests", "сундук", "сундука", "сундуков"), ConsoleColor.Yellow),
        new Seg($"  ·  {L.Count(session.Count, "match", "matches", "матч", "матча", "матчей")}", ConsoleColor.Gray));
    if (matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).FirstOrDefault() is { } last)
    {
        Row(L.T("Last match", "Последний матч"), new Seg(L.Count(last.ChestsOpened, "chest", "chests", "сундук", "сундука", "сундуков"), ConsoleColor.Yellow),
            new Seg($"  ·  {L.DateTimeShort((last.MatchStartUtc ?? last.AnalyzedAtUtc).ToLocalTime())}  ·  {Pretty.Playlist(last.Playlist)}  ·  {Pretty.Placement(last)}", ConsoleColor.Gray));
    }
    f.Blank();

    // New version: what's new, in the chosen language.
    if (Volatile.Read(ref update) is { } u && !notesHidden)
    {
        NotesBox(f, noteRoom, L.T($"New version {u.Version.ToString(3)} available", $"Доступна новая версия {u.Version.ToString(3)}"),
            L.T($"  (you have {AppInfo.VersionText}, nothing is lost when installing it)", $"  (у тебя {AppInfo.VersionText}, при установке ничего не теряется)"),
            u.WhatsNew, u.PageUrl);
    }
    else if (showWhatsNew && ReleaseNotes.WhatsNew(ReleaseNotes.Embedded, L.Code) is { Count: > 0 } current)
    {
        // First launch after an update: what this version brings (the notes are built into the exe).
        NotesBox(f, noteRoom, L.T($"What's new in {AppInfo.VersionText}", $"Что нового в {AppInfo.VersionText}"),
            L.T("  (update installed)", "  (обновление установлено)"), current, null);
    }
}

void NotesBox(Frame f, int noteRoom, string title, string subtitle, IReadOnlyList<string> notes, string? pageUrl)
{
    {
        f.Add(new Seg("  ┌ ", ConsoleColor.Green), new Seg(title, ConsoleColor.Green), new Seg(subtitle, ConsoleColor.DarkGray));
        var shown = 0;
        foreach (var note in notes)
        {
            var wrapped = ConsoleUi.Wrap(note, ConsoleUi.Width - 10).ToList();
            if (shown + wrapped.Count > Math.Clamp(noteRoom - 3, 1, 12))
            {
                f.Add(new Seg("  │ ", ConsoleColor.Green), new Seg(pageUrl != null ? L.T($"… and more: {pageUrl}", $"… и другое: {pageUrl}") : "…", ConsoleColor.DarkGray));
                break;
            }
            for (var i = 0; i < wrapped.Count; i++)
            {
                f.Add(new Seg("  │ ", ConsoleColor.Green), new Seg((i == 0 ? "• " : "  ") + wrapped[i], ConsoleColor.Gray));
            }
            shown += wrapped.Count;
        }
        if (notes.Count == 0 && pageUrl != null)
        {
            f.Add(new Seg("  │ ", ConsoleColor.Green), new Seg(L.T($"What's new: {pageUrl}", $"Что нового: {pageUrl}"), ConsoleColor.Gray));
        }
        f.Add("  └", ConsoleColor.Green);
        f.Blank();
    }
}

void DrawHelp(Frame f)
{
    void Para(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        foreach (var l in ConsoleUi.Wrap(text, ConsoleUi.Width - 6))
        {
            f.Add("  " + l, color);
        }
    }
    Para(L.T("The counter reads the replays Fortnite saves and counts your chests a few seconds after each match ends. Keep this window open while you play (you can minimize it).",
        "Счётчик читает повторы, которые сохраняет Fortnite, и подсчитывает твои сундуки через несколько секунд после конца каждого матча. Оставь это окно открытым, пока играешь (его можно свернуть)."));
    f.Blank();
    Para(L.T("Nothing is counted? In Fortnite, turn on  Settings → Game → Replays → Record Replays.",
        "Ничего не считается? В Fortnite включи  Настройки → Игра → Повторы → Записывать повторы."), ConsoleColor.Yellow);
    if (config.ShowOverlay && OperatingSystem.IsWindows())
    {
        f.Blank();
        f.Add("  " + L.T("Overlay", "Оверлей"), ConsoleColor.White);
        Para(L.T("Ctrl+Shift+O: show / hide.  Ctrl+Shift+L: lock / unlock. These shortcuts work anywhere, even in game.",
            "Ctrl+Shift+O: показать / скрыть.  Ctrl+Shift+L: закрепить / открепить. Эти сочетания работают везде, даже в игре."));
        Para(L.T("Locked, the mouse goes through it. Unlocked, hold the left button to move it, right-click to hide it.",
            "Когда оверлей закреплён, мышь проходит сквозь него. Откреплён: зажми левую кнопку, чтобы переместить, правая кнопка — скрыть."));
        Para(L.T("To see it in game, set Fortnite to \"Windowed Fullscreen\" (Settings → Video → Window Mode).",
            "Чтобы видеть его в игре, переключи Fortnite в режим «Оконный полноэкранный» (Настройки → Изображение → Режим окна)."), ConsoleColor.DarkGray);
    }
    f.Blank();
}

// ---- Main screen: keys and actions ----------------------------------------------------------

void HandleKey(ConsoleKeyInfo key)
{
    var items = Items();
    switch (key.Key)
    {
        case ConsoleKey.LeftArrow:
            tab = (tab + 2) % 3;
            break;
        case ConsoleKey.RightArrow:
        case ConsoleKey.Tab:
            tab = (tab + 1) % 3;
            break;
        case ConsoleKey.Escape:
            tab = 0;
            break;
        case ConsoleKey.UpArrow:
            selection[tab] = (selection[tab] + items.Count - 1) % items.Count;
            break;
        case ConsoleKey.DownArrow:
            selection[tab] = (selection[tab] + 1) % items.Count;
            break;
        case ConsoleKey.Home:
            selection[tab] = 0;
            break;
        case ConsoleKey.End:
            selection[tab] = items.Count - 1;
            break;
        case ConsoleKey.Enter:
            var index = Math.Clamp(selection[tab], 0, items.Count - 1);
            items[index].Run();
            if (tab == 1)
            {
                // A setting changed: save it and show it everywhere at once.
                store.SaveConfig(config);
                ReportWriter.WriteAll(store, config, data);
                UpdateOverlay(store, config, data, tracker.LiveMatch, tracker.IsLive);
            }
            break;
    }
}

void StartOverlay()
{
    if (!OperatingSystem.IsWindows() || NativeOverlay.IsRunning)
    {
        return;
    }
    NativeOverlay.SetAppearance(config.OverlaySize, config.OverlayOpacity);
    if (Array.IndexOf(NativeOverlay.Styles, config.OverlayStyle) < 0)
    {
        config.OverlayStyle = "hud"; // a style tried in a test version and removed since
    }
    NativeOverlay.SetStyle(config.OverlayStyle);
    NativeOverlay.Start(config.OverlayX, config.OverlayY, config.OverlayLocked,
        (x, y) =>
        {
            config.OverlayX = x;
            config.OverlayY = y;
            try { store.SaveConfig(config); } catch { /* ignore */ }
        },
        locked =>
        {
            config.OverlayLocked = locked;
            try { store.SaveConfig(config); } catch { /* ignore */ }
        });
    UpdateOverlay(store, config, data, tracker.LiveMatch, tracker.IsLive);
}

void ToggleOverlay()
{
    config.ShowOverlay = !config.ShowOverlay;
    if (config.ShowOverlay)
    {
        StartOverlay();
    }
    else if (OperatingSystem.IsWindows())
    {
        NativeOverlay.Stop();
    }
}

void CycleLanguage()
{
    config.Language = L.Codes[(Array.IndexOf(L.Codes, L.Code) + 1) % L.Codes.Length]; // en → ru → en
    L.Set(config.Language);
    appName = L.T("Fortnite Chest Counter", "Счётчик сундуков Fortnite");
    ConsoleUi.SetTitle($"{appName} {AppInfo.VersionText}");
}

void ToggleStartWithFortnite()
{
    var on = !config.StartWithFortnite;
    if (OperatingSystem.IsWindows() && FortniteWatch.Apply(on, dataDir))
    {
        config.StartWithFortnite = on;
        return;
    }
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(L.T("Open with Fortnite: not possible here, run the counter from ChestCounter.exe.",
        "Открывать вместе с Fortnite: здесь недоступно, запусти счётчик из ChestCounter.exe."));
    Console.ResetColor();
}

void ChangeDemosFolder()
{
    ConsoleUi.Screen(appName, L.T("replays folder", "папка с повторами"));
    ConsoleUi.Text(L.T("Current folder:", "Текущая папка:"));
    ConsoleUi.Hint("  " + config.DemosFolder);
    ConsoleUi.Blank();
    ConsoleUi.Hint(L.T("Type the new path, then Enter. Esc to cancel.", "Введи новый путь и нажми Enter. Esc — отмена."));
    ConsoleUi.Blank();
    var folder = ConsoleUi.Input(L.T("New folder: ", "Новая папка: "), config.DemosFolder);
    if (folder != config.DemosFolder)
    {
        config.DemosFolder = folder;
        WatchReplays();
        Console.WriteLine(L.T($"Replays folder: {folder}", $"Папка с повторами: {folder}"));
        nextScan = DateTime.UtcNow;
    }
}

void ResetConfiguration()
{
    ConsoleUi.Screen(appName, L.T("start the setup again", "пройти настройку заново"));
    ConsoleUi.Text(L.T("Your settings will be cleared and the counter will restart with the first-launch questions.",
        "Настройки будут сброшены, и счётчик перезапустится с вопросами первого запуска."));
    ConsoleUi.Hint(L.T("Your match history and statistics are kept.", "История матчей и статистика сохраняются."));
    if (!ConsoleUi.Confirm(L.T("Yes, start the setup again", "Да, пройти настройку заново"), L.T("Cancel", "Отмена")))
    {
        return;
    }
    if (OperatingSystem.IsWindows())
    {
        if (config.StartWithFortnite)
        {
            FortniteWatch.Apply(false, dataDir);
        }
        NativeOverlay.Stop();
    }
    try { File.Delete(store.ConfigPath); } catch { /* the setup will overwrite it */ }
    restart = true;
    quit = true;
}

// Manual check: asks GitHub now (a version may have come out since the counter was opened).
void CheckForUpdate()
{
    Console.WriteLine(L.T("Checking GitHub for a new version…", "Ищу новую версию на GitHub…"));
    Draw();
    var (found, reached) = Updater.CheckDetailedAsync().GetAwaiter().GetResult();
    if (found == null)
    {
        Console.WriteLine(reached
            ? L.T($"You already have the latest version ({AppInfo.VersionText}).", $"У тебя уже последняя версия ({AppInfo.VersionText}).")
            : L.T("GitHub cannot be reached right now (no connection?): try again later.",
                "GitHub сейчас недоступен (нет соединения?): попробуй позже."));
        return;
    }
    Volatile.Write(ref update, found);
    updateAnnounced = true;
    notesHidden = false;
}

void InstallUpdate(UpdateInfo u)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(L.T($"Downloading version {u.Version.ToString(3)}…", $"Скачиваю версию {u.Version.ToString(3)}…"));
    Console.ResetColor();
    Draw();
    var error = Updater.Install(u);
    if (error != null)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(L.T($"Update failed: {error}", $"Не удалось обновить: {error}"));
        Console.ResetColor();
        Console.WriteLine(L.T($"You can download it manually: {u.PageUrl}", $"Можно скачать вручную: {u.PageUrl}"));
        return;
    }
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(L.T("Update installed: restarting the counter…", "Обновление установлено: перезапускаю счётчик…"));
    Console.ResetColor();
    Draw();
    restart = true;
    quit = true;
}

void AnnounceNewProfiles()
{
    if (!tracker.NewProfilesFound)
    {
        return;
    }
    tracker.NewProfilesFound = false;
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.WriteLine(L.T("Several Fortnite accounts recorded replays on this PC: each one has its own profile. \"Switch profile\" on the home tab to choose yours.",
        "На этом ПК повторы записывали несколько аккаунтов Fortnite: у каждого свой профиль. «Сменить профиль» на главной вкладке, чтобы выбрать свой."));
    Console.ResetColor();
}

// Replays folder: Windows tells us when a file changes, instead of us re-reading the folder all the time.
void WatchReplays()
{
    watcher?.Dispose();
    watcher = null;
    try
    {
        if (Directory.Exists(config.DemosFolder))
        {
            var w = new FileSystemWatcher(config.DemosFolder, "*.replay")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            w.Created += (_, _) => { watcherFired = true; wake.Set(); };
            w.Changed += (_, _) => { watcherFired = true; wake.Set(); };
            w.Renamed += (_, _) => { watcherFired = true; wake.Set(); };
            watcher = w;
        }
    }
    catch
    {
        watcher = null; // fall back to the periodic scan
    }
}

void CheckAutoClose()
{
    var now = DateTime.UtcNow;
    if (FortniteWatch.IsFortniteRunning())
    {
        fortniteClosedSince = null;
        autoCloseAt = null; // Fortnite started again
        return;
    }
    fortniteClosedSince ??= now;
    // Wait for the last match to be counted (at most 5 minutes, in case a replay was left unfinished by a crash).
    var lastMatchDone = !tracker.IsLive && !tracker.HasPending && !tracker.IsFinishing;
    if (!lastMatchDone && now - fortniteClosedSince < TimeSpan.FromMinutes(5))
    {
        return;
    }
    if (autoCloseAt == null)
    {
        autoCloseAt = now.AddSeconds(30);
    }
    else if (now >= autoCloseAt)
    {
        quit = true;
    }
}

// Shows the new size / opacity on the overlay right away (it stays visible above the console).
void ApplyOverlayAppearance()
{
    if (OperatingSystem.IsWindows() && NativeOverlay.IsRunning)
    {
        NativeOverlay.SetAppearance(config.OverlaySize, config.OverlayOpacity);
    }
}

// The value that follows an option, or null when the command line ends there.
string? Arg(ref int i) => i + 1 < args.Length ? args[++i] : null;

static bool AcquireInstance(Mutex m, TimeSpan wait)
{
    try
    {
        return m.WaitOne(wait);
    }
    catch (AbandonedMutexException)
    {
        return true; // the previous instance was killed: it is ours now
    }
}

// Several accounts recorded replays on this PC: which one is played now (the overlay and the report show it).
static void ChooseProfile(TrackerData d, Config c, Store s)
{
    var list = Profiles.List(d);
    var current = Math.Max(0, list.FindIndex(p => p.Key == Profiles.Current(d, c)?.Key));
    ConsoleUi.Text(L.T("Several Fortnite accounts recorded replays on this PC.", "На этом ПК повторы записывали несколько аккаунтов Fortnite."));
    ConsoleUi.Text(L.T("Which account are you playing on?", "На каком аккаунте ты играешь?"), ConsoleColor.White);
    ConsoleUi.Hint(L.T("Each account keeps its own statistics. You can switch at any time from the home tab (\"Switch profile\").",
        "У каждого аккаунта своя статистика. Сменить можно в любой момент на главной вкладке («Сменить профиль»)."));
    var choice = ConsoleUi.Menu(list.Select(Profiles.Describe).ToList(), current, escapeIndex: current);
    c.Profile = list[choice].Key;
    try { s.SaveConfig(c); } catch { /* ignore */ }
}

static void UpdateOverlay(Store store, Config config, TrackerData d, MatchRecord? live, bool inProgress)
{
    var view = OverlayModel.Build(Profiles.View(d, config), live, inProgress);
    ReportWriter.WriteOverlay(store, view);
    if (OperatingSystem.IsWindows() && NativeOverlay.IsRunning)
    {
        NativeOverlay.Update(view);
    }
}

// Below-normal priority + Windows 11 "efficiency mode" (EcoQoS): the game always comes first.
static void LowerPriority()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }
    try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
    try { EcoQos.Enable(); } catch { }
}

static void Open(string path)
{
    try
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    catch
    {
        Console.WriteLine(L.T($"Open this file in your browser: {path}", $"Открой этот файл в браузере: {path}"));
    }
}
