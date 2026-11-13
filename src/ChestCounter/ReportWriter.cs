using System.Globalization;
using System.Net;
using System.Text;

namespace ChestCounter;

public static class Pretty
{
    public static string Playlist(string? playlist)
    {
        if (string.IsNullOrEmpty(playlist))
        {
            return L.T("unknown mode", "неизвестный режим");
        }

        var p = playlist.ToLowerInvariant();
        var parts = new List<string>();
        if (p.Contains("habanero") || p.Contains("ranked")) parts.Add(L.T("Ranked", "Рейтинг"));
        if (p.Contains("nobuild")) parts.Add(L.T("Zero Build", "Без строительства"));
        if (p.Contains("solo")) parts.Add(L.T("Solo", "Одиночный"));
        else if (p.Contains("duo")) parts.Add(L.T("Duos", "Пары"));
        else if (p.Contains("trio")) parts.Add(L.T("Trios", "Трио"));
        else if (p.Contains("squad")) parts.Add(L.T("Squads", "Отряды"));
        return parts.Count > 0 ? string.Join(" · ", parts) : playlist.Replace("Playlist_", "");
    }

    public static string Placement(MatchRecord m)
    {
        if (m.Placement is not int place || place <= 0)
        {
            return L.T("unknown placement", "место неизвестно");
        }
        if (place == 1)
        {
            return L.T("Victory Royale!", "Королевская победа!");
        }
        return m.TotalPlayers is int total && total > 0 ? $"{L.Ordinal(place)} / {total}" : L.Ordinal(place);
    }

    public static string Clock(float seconds)
    {
        var s = (int)seconds;
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>"à 7:16 · Coffre rare" (certain) or "vers 5:04 · Coffre rare (probable)".</summary>
    public static string Obtained(TreasureFind f)
    {
        if (f.ObtainedAtSeconds is not float t)
        {
            return L.T("time and source unknown", "время и источник неизвестны");
        }
        var when = f.TimeExact ? $"{L.T("at", "в")} {Clock(t)}" : $"{L.T("around", "около")} {Clock(t)}";
        return f.SourceCertain ? $"{when} · {f.SourceLabel}" : $"{when} · {f.SourceLabel} ({Likely})";
    }

    public static string Likely => L.T("likely", "вероятно");

    public static string ItemName(string item)
    {
        var name = item;
        foreach (var prefix in new[] { "WID_", "Athena", "AmmoData" })
        {
            name = name.Replace(prefix, "");
        }
        return name.Replace("_", " ").Trim();
    }
}

/// <summary>Replays analyzed so far / to analyze, shown as a loading card in the report.</summary>
public sealed record ScanProgress(int Done, int Total);

public static class ReportWriter
{
    public static readonly DateTime SessionStartUtc = DateTime.UtcNow;

    /// <summary>Set while several replays are being analyzed: the report shows a loading card and refreshes faster.</summary>
    public static volatile ScanProgress? Progress;

    /// <summary>A match belongs to this session if it ended after the tool was started (backlog replays do not count).</summary>
    public static bool IsThisSession(MatchRecord m)
    {
        var start = m.MatchStartUtc ?? m.AnalyzedAtUtc;
        if (start.Kind == DateTimeKind.Unspecified)
        {
            start = DateTime.SpecifyKind(start, DateTimeKind.Utc);
        }
        var end = start.ToUniversalTime().AddSeconds(m.DurationSeconds ?? 0);
        return end >= SessionStartUtc.AddMinutes(-2);
    }
    private static CultureInfo Cu => L.Culture;

    public static void WriteAll(Store store, Config config, TrackerData data)
    {
        try
        {
            var view = Profiles.View(data, config);
            WriteAtomic(store.ReportPath, Report(config, view, Profiles.List(data)));
            // The OBS page keeps what the overlay shows (a match in progress stays "live"), in the chosen style.
            OverlayStyle = config.OverlayStyle;
            WriteAtomic(store.OverlayPath, Overlay(LastOverlay ?? OverlayModel.Build(view, null, false, config.ShowSquadTotal)));
            WriteAtomic(store.CsvPath, Csv(data));
        }
        catch (Exception ex)
        {
            Console.WriteLine(L.T($"Cannot write the report: {ex.Message}", $"Не удалось записать отчёт: {ex.Message}"));
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static DateTime When(MatchRecord m) => (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime();

    // "Logbook" look (direction A): parchment cards on a night-sea background (a day version follows Windows' light
    // mode). Titles use the embedded Pirata One font; figures use Bahnschrift, installed with Windows.
    private const string Style = """
        :root{--bg:#0f1b24;--ink:#efe2c4;--ink2:#f7ecd2;--muted:#b8b0a0;--title:#f0c057;--card:#f2e6c9;--cink:#2a1c0f;--cmuted:#6b5a44;--line:#d8c59c;--track:#e4d3ad;--gold:#c9a24f;--star:#6c3fb5;--team:#1f7f7a;--warn:#a3361f;--warnbg:rgba(200,65,47,.10)}
        @media (prefers-color-scheme:light){:root:not([data-theme="dark"]){--bg:#efe4c9;--ink:#2a1c0f;--ink2:#2a1c0f;--muted:#6b5a44;--title:#7a4a1e;--card:#fffaf0;--line:#e2d2ae;--track:#efe2c4}}
        :root[data-theme="light"]{--bg:#efe4c9;--ink:#2a1c0f;--ink2:#2a1c0f;--muted:#6b5a44;--title:#7a4a1e;--card:#fffaf0;--line:#e2d2ae;--track:#efe2c4}
        *{box-sizing:border-box}
        body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.5 "Segoe UI",system-ui,-apple-system,sans-serif}
        main{max-width:1120px;margin:0 auto;padding:36px 16px 56px;display:flex;flex-direction:column;gap:28px}
        .pirate{font-family:"Pirata One","Segoe UI",serif;font-weight:400}
        .num{font-family:Bahnschrift,"Segoe UI",sans-serif;font-stretch:75%;font-variant-numeric:tabular-nums}
        header{display:flex;flex-wrap:wrap;align-items:flex-end;justify-content:space-between;gap:12px;border-bottom:2px solid var(--gold);padding-bottom:16px}
        .brand{display:flex;align-items:center;gap:14px}
        h1{margin:0;font-size:44px;line-height:1;color:var(--title)}
        .who{font-size:15px;color:var(--muted);margin-top:4px}.who b{color:var(--ink2)}
        .muted{color:var(--muted)}
        h2{margin:0 0 10px;font-size:26px;line-height:1.2;color:var(--title)}
        .tiles{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:12px}
        .tile{background:var(--card);color:var(--cink);border:1px solid var(--line);border-radius:8px;padding:14px 16px;display:flex;flex-direction:column;gap:2px}
        .tile .v{font-size:36px;font-weight:700;line-height:1.1}
        .tile .l{font-size:13px;color:var(--cmuted)}
        .tile .s{font-size:12px;color:var(--cmuted);margin-top:4px}
        .tile.hero{grid-column:span 2;background:#7a4a1e;color:#fbecc4;border:2px solid #e7b440;justify-content:space-between}
        .tile.hero{position:relative}
        .editbtn{display:none;position:absolute;top:10px;right:10px;width:36px;height:36px;padding:0;border-radius:8px;border:1px solid #e7b440;background:transparent;color:#ffd36a;cursor:pointer;place-items:center}
        .editbtn:hover{background:rgba(231,180,64,.18)}.editbtn:focus-visible{outline:3px solid #ffd36a;outline-offset:2px}
        html.incounter .editbtn{display:grid}
        .tile.hero .k{font-size:12px;letter-spacing:.14em;text-transform:uppercase;color:#f3cf7a}
        .tile.hero .v{font-size:60px;color:#ffd36a;line-height:1}
        .tile.hero .l{color:#fbecc4}
        @media (max-width:820px){.tiles{grid-template-columns:repeat(2,minmax(0,1fr))}.tile.hero{grid-column:span 2}}
        .card{background:var(--card);color:var(--cink);border:1px solid var(--line);border-radius:8px;padding:6px 16px;overflow-x:auto}
        .two{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:20px}
        @media (max-width:820px){.two{grid-template-columns:1fr}}
        table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
        th,td{text-align:left;padding:9px 8px;border-bottom:1px solid var(--line);vertical-align:middle}
        th{white-space:nowrap;font-size:12px;text-transform:uppercase;letter-spacing:.08em;color:var(--cmuted);font-weight:600}
        tr:last-child td{border-bottom:none}
        td.n{text-align:right;font-weight:700}
        td.nw{white-space:nowrap}
        .big{font-size:20px}
        .items{color:var(--cmuted);font-size:13px}
        .star{color:var(--star);font-weight:600}
        .gold{color:#b9821c}
        .sw{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:10px;vertical-align:-1px}
        .meter{display:block;width:140px;height:6px;background:var(--track);border-radius:3px;overflow:hidden}
        .meter i{display:block;height:6px}
        .flag{display:flex;gap:12px;align-items:flex-start;background:#3a1f12;color:#f6dfb0;border:1px solid #c8412f;border-radius:8px;padding:12px 14px;font-size:14px;margin-top:12px}
        tr.unusual td{background:var(--warnbg)}
        .warn{color:var(--warn)}
        .chart{padding:18px 18px 12px}
        .bars{display:flex;align-items:stretch;gap:4px;height:150px;padding-top:26px}
        .col{flex:1;min-width:5px;display:flex;align-items:flex-end;position:relative;cursor:default}
        .bar{width:100%;background:var(--gold);border-radius:3px 3px 0 0;transition:filter .12s}
        .bar.top{background:#c8412f}
        .bar.zero{background:var(--line);height:3px!important}
        .col:hover .bar{filter:brightness(1.15)}
        .tip{position:absolute;left:50%;transform:translateX(-50%);white-space:nowrap;background:var(--cink);color:var(--card);
             padding:3px 8px;border-radius:6px;font-size:12px;font-weight:600;opacity:0;pointer-events:none;transition:opacity .12s;z-index:2}
        .col:nth-child(-n+3) .tip{left:0;transform:none}
        .col:nth-last-child(-n+3) .tip{left:auto;right:0;transform:none}
        .col:hover .tip{opacity:1}
        .axis{display:flex;justify-content:space-between;gap:8px;color:var(--cmuted);font-size:12px;margin-top:8px;border-top:1px dashed var(--line);padding-top:6px}
        .head{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:12px;margin-bottom:10px}
        .head h2{margin:0}
        .tsw{position:relative;display:inline-flex;align-items:center;gap:10px;min-height:36px;font-size:14px;color:var(--ink2);cursor:pointer;user-select:none}
        .tsw input{position:absolute;opacity:0;width:1px;height:1px;margin:0}
        .tsw .trk{position:relative;width:38px;height:22px;border-radius:11px;background:#8f8572;flex:none;transition:background .15s}
        .tsw .trk:after{content:"";position:absolute;top:3px;left:3px;width:16px;height:16px;border-radius:50%;background:#fff;transition:transform .15s}
        .tsw input:checked+.trk{background:var(--team)}.tsw input:checked+.trk:after{transform:translateX(16px)}
        .tsw input:focus-visible+.trk{outline:3px solid var(--gold);outline-offset:2px}
        .tsw .badge{display:inline-block;margin-left:2px;padding:1px 8px;border-radius:999px;background:#f6d8c9;color:var(--warn);font-size:12px;font-weight:700}
        .statsbar{display:flex;justify-content:flex-end;margin:-22px 0 -10px}
        .squadtile,.sq{display:none}
        body.showsquad .squadtile{display:flex}body.showsquad .sq{display:table-cell}
        body.showsquad .tiles.hassquad{grid-template-columns:repeat(7,minmax(0,1fr))}
        .fold .head h2{display:flex;align-items:center;gap:4px}
        .foldbtn{display:inline-flex;align-items:center;gap:10px;background:none;border:0;padding:4px 0;margin:0;font:inherit;color:inherit;cursor:pointer;text-align:left}
        .foldbtn:focus-visible{outline:3px solid var(--gold);outline-offset:4px;border-radius:4px}
        .chev{width:10px;height:10px;border-right:3px solid currentColor;border-bottom:3px solid currentColor;transform:rotate(45deg);margin:0 4px 6px 2px;transition:transform .15s}
        .fold.closed .chev{transform:rotate(-45deg);margin-bottom:0}
        .fold.closed .foldbody{display:none}
        .fold.closed .head{margin-bottom:0}
        .fold.closed .head>:not(h2){display:none}
        .team{display:none}
        body.showteam tr.team{display:table-row}
        body.showteam span.team{display:inline}
        tr.team td{opacity:.8}
        .by{white-space:nowrap}
        .by svg{vertical-align:-3px;margin-right:6px}
        footer{font-size:13px;color:var(--muted);border-top:1px dashed var(--gold);padding-top:14px}
        .loading{display:flex;gap:16px;align-items:center;background:var(--card);color:var(--cink);border:2px solid var(--gold);border-radius:8px;padding:16px 18px}
        .loading .muted{color:var(--cmuted)}
        .spin{width:30px;height:30px;flex:none;border:3px solid var(--line);border-top-color:#b9821c;border-radius:50%;animation:spin .8s linear infinite}
        @keyframes spin{to{transform:rotate(360deg)}}
        .loading .grow{flex:1;min-width:0}
        .pbar{height:8px;background:var(--line);border-radius:4px;overflow:hidden;margin:8px 0 6px}
        .pbar i{display:block;height:100%;background:#b9821c}
        .profiles{margin-top:-16px;font-size:13px}
        .embed,.embed body{background:transparent;overflow:hidden}.embed main{padding:0;max-width:none}.embed header,.embed footer,.embed .profiles,.embed .notice{display:none}
        .info{position:relative;display:inline-flex;align-items:center;justify-content:center;width:17px;height:17px;margin-left:6px;border-radius:50%;border:1.5px solid var(--cmuted);color:var(--cmuted);font:700 11px/1 Georgia,serif;font-style:italic;cursor:help;vertical-align:1px;outline:none}
        .info:hover,.info:focus{border-color:#b9821c;color:#b9821c}
        .infotip{display:none;position:absolute;z-index:5;top:24px;right:-12px;width:300px;max-width:80vw;background:var(--cink);color:var(--card);padding:10px 12px;border-radius:8px;font:400 13px/1.45 "Segoe UI",system-ui,sans-serif;font-style:normal;text-align:left;box-shadow:0 6px 18px rgba(0,0,0,.35)}
        .info:hover .infotip,.info:focus .infotip{display:block}
        h2 .info{border-color:var(--title);color:var(--title);vertical-align:6px}
        h2 .infotip{left:-12px;right:auto}
        tr.pg{display:none!important}
        .pager{display:flex;flex-wrap:wrap;align-items:center;gap:8px;padding:10px 0 8px;border-top:1px solid var(--line)}
        .pager[hidden],.pager button[hidden]{display:none}
        .pager .count{color:var(--cmuted);font-size:13px;margin-right:auto}
        .pager button{min-height:36px;padding:6px 14px;border-radius:6px;border:1px solid var(--gold);background:transparent;color:var(--cink);font:600 13px "Segoe UI",system-ui,sans-serif;cursor:pointer}
        .pager button:hover{background:var(--track)}
        .notice{background:var(--card);color:var(--cink);border:1px solid var(--line);border-left:6px solid var(--gold);border-radius:8px;padding:12px 16px;white-space:pre-line}
        .notice.warning{border-left-color:#c8412f;background:#fbe9d0}
        .exper{display:inline-block;font:700 12px "Segoe UI",system-ui,sans-serif;letter-spacing:.04em;text-transform:uppercase;padding:2px 8px;border-radius:999px;background:#f6d8c9;color:var(--warn);vertical-align:6px}
        .thour{background:var(--card);color:var(--cink);border:1px solid var(--line);border-left:6px solid var(--gold);border-radius:8px;padding:10px 14px;margin-bottom:12px}
        .thour.live{border-left-color:#c8412f;background:#fbe9d0}
        .thour .tt{font-weight:700}.thour .exp{font-weight:400;color:var(--cmuted)}
        /* Treasure Hour live: the boosted odds, in orange like the banner */
        .boost{display:inline-block;margin-left:8px;padding:1px 8px;border-radius:999px;background:#fbdcc6;color:#b4410f;font:700 12px "Segoe UI",system-ui,sans-serif;letter-spacing:0;text-transform:none;vertical-align:1px}
        .boost[hidden]{display:none}
        .odds.boosted{border-color:#e2702f;box-shadow:inset 6px 0 0 #e2702f;padding-left:22px}
        tr.boosted td:first-child{box-shadow:inset 4px 0 0 #e2702f;padding-left:14px}
        .boostv{color:#b4410f;font-weight:700}
        .thour .tb{display:flex;flex-wrap:wrap;gap:6px 18px;font-size:14px;margin-top:4px}
        .chances{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}
        @media (max-width:820px){.chances{grid-template-columns:1fr}}
        .odds{background:var(--card);color:var(--cink);border:1px solid var(--line);border-radius:8px;padding:14px 16px;display:flex;flex-direction:column;gap:4px}
        .odds .k{font-size:12px;letter-spacing:.12em;text-transform:uppercase;color:var(--cmuted);display:flex;justify-content:space-between;gap:8px}
        .odds .v{font-size:40px;font-weight:700;line-height:1.1}
        .odds .l,.odds .s{font-size:13px;color:var(--cmuted)}
        .meter.wide{width:100%;margin:6px 0 2px}.meter.wide i{background:#b9821c}
        .gotbadge{display:none;color:var(--team);letter-spacing:0;text-transform:none;font-weight:700}
        .odds.got{border-color:var(--team)}.odds.got .gotbadge{display:inline}
        .collection{padding:10px 16px 14px}
        .wk{padding:8px 0}.wk+.wk{border-top:1px solid var(--line)}
        .wt{font-weight:600;margin-bottom:8px}.wk.now .wt{color:#b9821c}
        .prizes{display:grid;grid-template-columns:repeat(auto-fill,minmax(140px,1fr));gap:10px}
        .prize{position:relative;display:flex;flex-direction:column;gap:2px;padding:8px;border:1px solid var(--line);border-radius:8px;cursor:pointer;background:var(--card)}
        .prize input{position:absolute;top:10px;left:10px;width:20px;height:20px;accent-color:var(--team);cursor:pointer;z-index:1}
        .prize .pimg{display:block;aspect-ratio:1;border-radius:6px;background:var(--track);overflow:hidden;margin-bottom:4px}
        .prize img{width:100%;height:100%;object-fit:cover;display:block;filter:grayscale(.65);opacity:.75}
        .prize .pn{font-size:13px;font-weight:600;line-height:1.3}
        .prize .pk{font-size:12px;color:var(--cmuted)}
        .prize.got{border:2px solid var(--team);padding:7px}
        .prize.got img{filter:none;opacity:1}
        """;

    /// <summary>@font-face of the embedded headings font (empty if the resource is missing: the page falls back to Segoe UI).</summary>
    private static readonly string FontFace = LoadFontFace();

    /// <summary>The headings font, for the pages of the graphical interface too.</summary>
    public static string FontFaceCss => FontFace;

    private static string LoadFontFace()
    {
        try
        {
            using var s = typeof(ReportWriter).Assembly.GetManifestResourceStream("PirataOne.woff2");
            if (s == null)
            {
                return "";
            }
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return "@font-face{font-family:'Pirata One';src:url(data:font/woff2;base64," + Convert.ToBase64String(ms.ToArray()) + ") format('woff2');font-display:swap}";
        }
        catch
        {
            return "";
        }
    }

    private const string ChestIcon = """<svg width="52" height="52" viewBox="0 0 56 56" aria-hidden="true"><rect x="6" y="20" width="44" height="28" rx="4" fill="#7a4a1e" stroke="#e7b440" stroke-width="3"/><path d="M6 26 Q28 4 50 26" fill="#965d27" stroke="#e7b440" stroke-width="3"/><rect x="24" y="24" width="8" height="10" rx="2" fill="#e7b440"/><line x1="6" y1="34" x2="50" y2="34" stroke="#e7b440" stroke-width="2"/></svg>""";
    private const string TeamIcon = """<svg width="16" height="16" viewBox="0 0 24 24" aria-hidden="true"><circle cx="9" cy="8" r="3.2" fill="none" stroke="#1f7f7a" stroke-width="2"/><path d="M3 20c0-3.5 2.7-6 6-6s6 2.5 6 6" fill="none" stroke="#1f7f7a" stroke-width="2"/><circle cx="17" cy="9" r="2.6" fill="none" stroke="#1f7f7a" stroke-width="2"/><path d="M16 14c3 0 5 2.2 5 5" fill="none" stroke="#1f7f7a" stroke-width="2"/></svg>""";
    private const string FlagIcon = """<svg width="18" height="18" viewBox="0 0 24 24" aria-hidden="true" style="flex:none;margin-top:2px"><path d="M5 21V4M5 4h11l-2 4 2 4H5" fill="none" stroke="#ff8a6a" stroke-width="2" stroke-linejoin="round"/></svg>""";

    private static readonly Dictionary<string, string> KindColor = new()
    {
        [ChestKinds.Normal] = "#c9a24f",
        [ChestKinds.Rare] = "#2c7fb8",
        [ChestKinds.Buried] = "#8a5a2b",
        [ChestKinds.Extraction] = "#1f7f7a",
        [ChestKinds.PosterChest] = "#7a4fc2",
        [ChestKinds.Other] = "#9a8f7a",
    };

    public static string Report(Config config, TrackerData data, IReadOnlyList<Profile> profiles)
    {
        var progress = Progress;
        var matches = data.Matches.OrderByDescending(When).ToList();
        var totalChests = matches.Sum(m => m.ChestsOpened);
        // Shown as the total: counted chests + those added by hand. Averages and odds stay on the counted ones.
        var grandTotal = data.TotalChests;
        // The squad total is shown only when asked, with the switch above the tiles (it is inaccurate: teammates far from
        // the player are not in the replays).
        var teamChests = data.TeamChests;
        var avg = matches.Count > 0 ? (double)totalChests / matches.Count : 0;
        var best = matches.Count > 0 ? matches.Max(m => m.ChestsOpened) : 0;
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        var teamTreasures = matches.Sum(m => m.TeamTreasureFinds.Count);
        var session = matches.Where(IsThisSession).Sum(m => m.ChestsOpened);
        var player = matches.Select(m => m.PlayerName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";
        // Own treasures only: the team's are found with chests that are not counted here.
        var every = treasures > 0 ? (int)Math.Round((double)totalChests / treasures) : 0;

        var sb = new StringBuilder();
        var title = L.T("Chest Counter", "Счётчик сундуков");
        var updated = L.T(
            $"Updated {DateTime.Now.ToString("d MMM yyyy 'at' HH:mm:ss", Cu)} · auto refresh",
            $"Обновлено {DateTime.Now.ToString("d MMM yyyy 'в' HH:mm:ss", Cu)} · обновляется автоматически");
        string Tile(string value, string label, string? sub = null) =>
            $"""<div class="tile"><div class="v num">{value}</div><div class="l">{label}</div>{(sub != null ? $"<div class=\"s\">{sub}</div>" : "")}</div>""";

        sb.Append($"""
            <!doctype html><html lang="{L.Code}"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="refresh" content="{(progress != null ? 2 : 15)}">
            <title>{title}</title><style>{FontFace}{Style}</style></head><body data-showsquad="{(config.ShowSquadInReport ? 1 : 0)}" data-showteam="{(config.ShowTeamTreasuresInReport ? 1 : 0)}"><main>
            <header><div class="brand">{ChestIcon}<div><h1 class="pirate">{title}</h1>{(player.Length > 0 ? $"<div class=\"who\"><b>{H(player)}</b></div>" : "")}</div></div>
            <span class="muted" style="font-size:13px">{updated}</span></header>
            {LoadingCard(progress)}{NoticeCards()}{ProfilesLine(profiles, player)}
            <section class="tiles{(teamChests > 0 ? " hassquad" : "")}">
              <div class="tile hero"><button type="button" class="editbtn" id="editchests" title="{H(L.T("Edit my number of chests", "Изменить число сундуков"))}" aria-label="{H(L.T("Edit my number of chests", "Изменить число сундуков"))}">{PencilIcon}</button><div class="k">{L.T("Chests in total", "Всего сундуков")}</div><div class="v num">{grandTotal.ToString("N0", Cu)}</div><div class="l">{L.T($"opened or destroyed by you · {session.ToString("N0", Cu)} since the tool started", $"открыто или разрушено тобой · {session.ToString("N0", Cu)} с момента запуска программы")}{(data.ManualAdjust != 0 ? "<br>" + L.T($"{data.ManualAdjust.ToString("+#,0;-#,0", Cu)} of them added by hand", $"из них добавлено вручную: {data.ManualAdjust.ToString("+#,0;-#,0", Cu)}") : "")}</div></div>
              {(teamChests > 0 ? Tile((grandTotal + teamChests).ToString("N0", Cu), L.T("squad total", "итог отряда") + SquadInfo, L.T($"you + {teamChests.ToString("N0", Cu)} by your teammates", $"ты + {teamChests.ToString("N0", Cu)} у союзников")).Replace("<div class=\"tile\">", "<div class=\"tile squadtile\">") : "")}
              {Tile(matches.Count.ToString("N0", Cu), L.T("matches analyzed", "матчей проанализировано"))}
              {Tile(avg.ToString("0.0", Cu), L.T("chests per match", "сундуков за матч"))}
              {Tile(best.ToString("N0", Cu), L.T("best match", "лучший матч"))}
              {Tile(treasures.ToString("N0", Cu), L.T("treasures found", "сокровищ найдено"),
                  (every > 0 ? L.T($"1 every {every.ToString("N0", Cu)} chests", $"1 на {every.ToString("N0", Cu)} сунд.") : null)
                  + (teamTreasures > 0 ? (every > 0 ? "<br>" : "") + L.T($"+{teamTreasures} by the team (estimate)", $"+{teamTreasures} у команды (оценка)") : ""))}
            </section>
            {(teamChests > 0 ? $"<div class=\"statsbar\">{Switch("showsquad", L.T("Show the squad total", "Показывать итог отряда") + $" <span class=\"badge\">{L.T("inaccurate", "неточно")}</span>")}</div>" : "")}
            """);

        // Last 30 days, one bar per day (the best day stands out).
        var today = DateTime.Now.Date;
        var days = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).ToList();
        var perDay = days.Select(d => matches.Where(m => When(m).Date == d).Sum(m => m.ChestsOpened)).ToList();
        var max = Math.Max(1, perDay.Max());
        sb.Append($"<section><h2 class=\"pirate\">{L.T("Chests per day", "Сундуки по дням")}</h2><div class=\"card chart\"><div class=\"bars\">");
        for (var i = 0; i < days.Count; i++)
        {
            var v = perDay[i];
            var h = (int)Math.Round(100.0 * v / max);
            var tip = $"{days[i].ToString(L.T("ddd d MMM", "ddd d MMM"), Cu)}{L.T(": ", ": ")}{L.Count(v, "chest", "chests", "сундук", "сундука", "сундуков")}";
            var cls = v == 0 ? " zero" : v == max ? " top" : "";
            sb.Append($"<div class=\"col\" aria-label=\"{H(tip)}\"><span class=\"tip\" style=\"bottom:calc({Math.Max(h, 3)}% + 6px)\">{H(tip)}</span><div class=\"bar{cls}\" style=\"height:{h}%\"></div></div>");
        }
        var bestDay = perDay[perDay.IndexOf(perDay.Max())];
        var bestDate = days[perDay.IndexOf(perDay.Max())];
        sb.Append($"</div><div class=\"axis\"><span>{days[0].ToString(L.T("d MMM", "d MMM"), Cu)}</span>"
            + (bestDay > 0 ? $"<span>{L.T("best day", "лучший день")} : {bestDate.ToString(L.T("d MMM", "d MMM"), Cu)} · {L.Count(bestDay, "chest", "chests", "сундук", "сундука", "сундуков")}</span>" : "")
            + $"<span>{L.T("today", "сегодня")}</span></div></div></section>");

        // By chest type (with the other interactions, not in the total) | where the player's treasures come from.
        var byKind = matches.SelectMany(m => m.Chests).GroupBy(c => ChestKinds.Canonical(c.Kind)).OrderByDescending(g => g.Count()).ToList();
        var kindMax = Math.Max(1, byKind.Count > 0 ? byKind.Max(g => g.Count()) : 1);
        var otherKinds = matches
            .SelectMany(m => m.OtherInteractions.Count > 0
                ? m.OtherInteractions
                : new Dictionary<string, int> { [ChestKinds.AmmoBox] = m.AmmoBoxesOpened, [ChestKinds.OtherContainer] = m.OtherContainersOpened })
            .GroupBy(kv => kv.Key)
            .Select(g => (Kind: g.Key, Count: g.Sum(kv => kv.Value)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ToList();
        sb.Append($"<div class=\"two\"><section><h2 class=\"pirate\">{L.T("By chest type", "По типам сундуков")}</h2><div class=\"card\"><table>");
        foreach (var g in byKind)
        {
            var color = KindColor.GetValueOrDefault(g.Key, "#9a8f7a");
            var w = Math.Max(2, (int)Math.Round(100.0 * g.Count() / kindMax));
            sb.Append($"<tr><td><span class=\"sw\" style=\"background:{color}\"></span>{H(L.Name(g.Key))}</td><td style=\"width:150px\"><span class=\"meter\"><i style=\"width:{w}%;background:{color}\"></i></span></td><td class=\"n num big\">{g.Count().ToString("N0", Cu)}</td></tr>");
        }
        var destroyedTotal = matches.Sum(m => m.Chests.Count(c => c.Destroyed));
        var notes = new List<string>();
        if (destroyedTotal > 0)
        {
            notes.Add(L.T($"of which {destroyedTotal.ToString("N0", Cu)} destroyed (broken instead of opened)", $"из них разрушено (сломано, а не открыто): {destroyedTotal.ToString("N0", Cu)}"));
        }
        foreach (var (kind, count) in otherKinds)
        {
            sb.Append($"<tr class=\"items\"><td>{H(L.Name(kind))} {L.T("(not in the total)", "(не входит в итог)")}</td><td></td><td class=\"n num\">{count.ToString("N0", Cu)}</td></tr>");
        }
        sb.Append("</table>");
        if (notes.Count > 0)
        {
            sb.Append($"<div class=\"items\" style=\"padding:10px 0 8px\">{string.Join(" · ", notes)}</div>");
        }
        sb.Append("</div></section>");

        var treasureRows = matches.SelectMany(m => m.TreasureFinds.Select(f => (m, f, Team: false))).ToList();
        var teamRows = matches.SelectMany(m => m.TeamTreasureFinds.Select(f => (m, f, Team: true))).ToList();
        sb.Append($"<section><h2 class=\"pirate\">{L.T("Where your treasures come from", "Откуда твои сокровища")}</h2><div class=\"card\"><table>");
        if (treasureRows.Count == 0)
        {
            sb.Append($"<tr><td class=\"items\">{L.T("No treasure yet.", "Сокровищ пока нет.")}</td></tr>");
        }
        foreach (var g in treasureRows.GroupBy(r => r.f.SourceLabel).OrderByDescending(g => g.Count()))
        {
            var isChest = g.First().f.SourceIsChest;
            sb.Append(isChest
                ? $"<tr><td>{H(g.Key)}</td><td class=\"n num big\">{g.Count()}</td></tr>"
                : $"<tr class=\"unusual\"><td class=\"warn\">{H(g.Key)} <span style=\"font-size:12px\">· {L.T("low reliability", "низкая достоверность")}</span></td><td class=\"n num big warn\">{g.Count()}</td></tr>");
        }
        sb.Append("</table></div>");
        var unusual = treasureRows.Where(r => !r.f.SourceIsChest).ToList();
        if (unusual.Count > 0)
        {
            var count = L.Count(unusual.Count, "treasure", "treasures", "сокровище", "сокровища", "сокровищ");
            sb.Append($"<div class=\"flag\">{FlagIcon}<span>" + L.T(
                $"<b>{count} obtained other than from a chest.</b> Low reliability: most likely a mistake in the way the replays are analyzed.",
                $"<b>Получено не из сундука: {count}.</b> Низкая достоверность: скорее всего, ошибка анализа повторов.") + "</span></div>");
        }
        sb.Append("</section></div>");

        sb.Append(ChancesSection(grandTotal, matches));
        sb.Append(SeenSection(matches));
        sb.Append(CollectionSection(matches, data.ObtainedIds));

        // Treasures: the player's own, then (hidden unless asked) those found by teammates, shared with the whole team.
        if (treasureRows.Count > 0 || teamRows.Count > 0)
        {
            var namesInfo = L.T(
                "Why do some treasures have a name and others not? A major treasure (outfit, pickaxe, track, loading screen, vehicle, Pirate Shop Access) comes out of a golden chest that carries its exact id: it is named for sure (★★), as long as that chest is in your replay, that is within about 150 m of you. A gizmo is named when it shows up in your hands. Otherwise the replay only says that a treasure was received, not which one: \"Treasure (unknown type)\". That is always the case for a treasure found by a teammate far from you.",
                "Почему у одних сокровищ есть название, а у других нет? Крупное сокровище (наряд, кирка, трек, экран загрузки, транспорт, доступ в пиратскую лавку) выпадает из золотого сундука, который несёт его точный идентификатор: оно названо точно (★★), если этот сундук есть в твоём повторе, то есть примерно в 150 м от тебя. Устройство называется, когда оказывается у тебя в руках. В остальных случаях повтор сообщает только, что сокровище получено, но не какое: «Сокровище (тип неизвестен)». Так всегда бывает с сокровищем, которое союзник нашёл далеко от тебя.");
            var teamSwitch = teamRows.Count > 0
                ? Switch("showteam", L.T($"Also show the team (+{teamRows.Count}, estimate)", $"Показывать и команду (+{teamRows.Count}, оценка)"))
                : "";
            sb.Append(FoldStart("treasures", $"{L.T("Treasures found", "Найденные сокровища")} · <span class=\"num\">{treasures}</span>", open: false, Info(namesInfo), teamSwitch));
            sb.Append($"<div class=\"card\"><table id=\"t-treasures\"><tr><th>{L.T("Match", "Матч")}</th><th>{L.T("Treasure", "Сокровище")}</th><th>{L.T("Obtained at", "Получено")}</th><th>{L.T("Source", "Источник")}</th><th>{L.T("By", "Кем")}</th></tr>");
            foreach (var (m, f, team) in treasureRows.Concat(teamRows).OrderByDescending(r => When(r.m)).ThenBy(r => r.f.ObtainedAtSeconds ?? 0))
            {
                var when = f.ObtainedAtSeconds is float t ? (f.TimeExact ? Pretty.Clock(t) : $"{L.T("around", "около")} {Pretty.Clock(t)}") : "?";
                string source;
                if (team && string.IsNullOrEmpty(f.Teammate))
                {
                    source = $"<span class=\"items\">{L.T("chest out of your sight", "сундук вне поля зрения")}</span>";
                }
                else if (!f.SourceIsChest)
                {
                    source = $"<span class=\"warn\">{H(f.SourceLabel)} · {L.T("low reliability", "низкая достоверность")}</span>";
                }
                else
                {
                    source = H(f.SourceLabel) + (f.SourceCertain ? "" : $" <span class=\"items\">({Pretty.Likely})</span>");
                }
                var who = !team ? L.T("you", "ты")
                    : TeamIcon + (string.IsNullOrEmpty(f.Teammate) ? L.T("a teammate", "союзник") : H(f.Teammate));
                var cls = team ? "r team" : f.SourceIsChest ? "r" : "r unusual";
                var stars = f.FromGoldenChest ? "<span class=\"gold\">★★</span>" : "<span class=\"star\">★</span>";
                sb.Append($"<tr class=\"{cls}\"><td class=\"nw num items\">{L.DateTimeShort(When(m))}</td><td>{stars} <b>{H(f.DisplayName)}</b></td><td class=\"nw num\">{when}</td><td>{source}</td><td class=\"by\">{who}</td></tr>");
            }
            sb.Append($"</table>{Pager("t-treasures")}</div>{FoldEnd}");
        }

        // Latest matches.
        sb.Append(FoldStart("matches", $"{L.T("Latest matches", "Последние матчи")} · <span class=\"num\">{matches.Count.ToString("N0", Cu)}</span>", open: false));
        sb.Append($"<div class=\"card\"><table id=\"t-matches\"><tr><th>{L.T("Date", "Дата")}</th><th>{L.T("Mode", "Режим")}</th><th>{L.T("Placement", "Место")}</th><th style=\"text-align:right\">{L.T("Chests", "Сундуки")}</th>{(teamChests > 0 ? $"<th class=\"sq\" style=\"text-align:right\">{H(OverlayModel.SquadLabel)}</th>" : "")}<th>{L.T("Treasure", "Сокровище")}</th></tr>");
        foreach (var m in matches)
        {
            var treasure = string.Join("<br>", m.TreasureFinds.Select(f => $"<span class=\"{(f.FromGoldenChest ? "gold" : "star")}\">{(f.FromGoldenChest ? "★★" : "★")} {H(f.DisplayName)}</span>"))
                + string.Concat(m.TeamTreasureFinds.Select((f, i) => $"<span class=\"team\">{(i == 0 && m.TreasureFinds.Count == 0 ? "" : "<br>")}{TeamIcon} ★ {H(f.DisplayName)}</span>"));
            var win = m.Placement == 1;
            sb.Append($"<tr class=\"r\"><td class=\"nw num items\">{L.DateTimeShort(When(m))}</td><td>{H(Pretty.Playlist(m.Playlist))}</td><td class=\"nw num\"{(win ? " style=\"color:#b9821c;font-weight:700\"" : "")}>{H(Pretty.Placement(m))}</td><td class=\"n num big\">{m.ChestsOpened}{(m.VehicleChestsMissing ? $" <span class=\"warn\" title=\"{H(VehicleWarning)}\">⚠</span>" : "")}</td>{(teamChests > 0 ? $"<td class=\"n num sq\" title=\"{H(string.Join(", ", m.TeamChests.Where(x => x.Chests > 0).Select(x => $"{x.Name} {x.Chests}")))}\">{(m.TeamChests.Count > 0 ? "+" + m.TeamChestsOpened : "")}</td>" : "")}<td class=\"items\">{treasure}</td></tr>");
        }
        sb.Append($"</table>{Pager("t-matches")}</div>");
        var blind = matches.Count(m => m.VehicleChestsMissing);
        if (blind > 0)
        {
            sb.Append($"<div class=\"flag\">{FlagIcon}<span><b>⚠ {L.Count(blind, "match", "matches", "матч", "матча", "матчей")}</b> · {H(VehicleWarning)}</span></div>");
        }
        sb.Append(FoldEnd);

        // Page script: team rows shown or not, long tables folded ("show more"), treasures ticked as obtained.
        // Everything is remembered in this browser only (the page is rewritten every few seconds by the counter).
        const string script = """
            <script>(function(){
            function get(k){try{return localStorage.getItem(k)}catch(e){return null}}
            function put(k,v){try{localStorage.setItem(k,v)}catch(e){}}
            var pagers=[];
            [].forEach.call(document.querySelectorAll('.pager'),function(box){
              var t=document.getElementById(box.getAttribute('data-for'));if(!t)return;
              var step=+box.getAttribute('data-step')||10,key='chestcounter-rows-'+t.id,n=parseInt(get(key),10);if(!(n>=step))n=step;
              var more=box.querySelector('.more'),all=box.querySelector('.all'),less=box.querySelector('.less'),info=box.querySelector('.count');
              function rows(){return [].filter.call(t.querySelectorAll('tr.r'),function(r){return !r.classList.contains('team')||document.body.classList.contains('showteam')})}
              function apply(){
                [].forEach.call(t.querySelectorAll('tr.r'),function(r){r.classList.remove('pg')});
                var v=rows();v.forEach(function(r,i){if(i>=n)r.classList.add('pg')});
                var shown=Math.min(n,v.length);
                info.textContent=info.getAttribute('data-fmt').replace('{0}',shown).replace('{1}',v.length);
                more.hidden=all.hidden=shown>=v.length;less.hidden=n<=step;box.hidden=v.length<=step;
              }
              more.onclick=function(){n+=step*2;put(key,n);apply()};
              all.onclick=function(){n=1e9;put(key,n);apply()};
              less.onclick=function(){n=step;put(key,n);apply()};
              apply();pagers.push(apply);
            });
            if(window.self!==window.top)document.documentElement.classList.add('embed');
            // Opened in the counter's window (?t=...): choices are saved by the counter itself (config, data.json).
            var tok=new URLSearchParams(location.search).get('t');
            // In the counter's window, the pencil of "chests in total" opens the window's edit box.
            if(tok&&window.self!==window.top){document.documentElement.classList.add('incounter');var eb=document.getElementById('editchests');if(eb)eb.onclick=function(){try{window.parent.postMessage({cc:'editChests'},location.origin)}catch(e){}}}
            function post(action,value){if(!tok)return;try{fetch('/api/action',{method:'POST',headers:{'X-Token':tok,'Content-Type':'application/json'},body:JSON.stringify({action:action,value:value})})}catch(e){}}
            // Switches: the counter's setting, or (report opened as a file) what this browser remembers.
            function toggle(id,cls,action){
              var b=document.getElementById(id),key='chestcounter-'+cls,v=document.body.getAttribute('data-'+cls)==='1';
              if(!tok){var saved=get(key);if(saved!==null)v=saved==='1'}
              function set(x){document.body.classList.toggle(cls,x);if(b)b.checked=x;pagers.forEach(function(p){p()})}
              set(v);
              if(b)b.addEventListener('change',function(){set(b.checked);put(key,b.checked?'1':'0');post(action,b.checked)});
            }
            toggle('showteam','showteam','showTeamInReport');
            toggle('showsquad','showsquad','showSquadInReport');
            // Folding sections: the title opens or closes the whole section.
            [].forEach.call(document.querySelectorAll('section.fold'),function(s){
              var key='chestcounter-fold-'+s.getAttribute('data-fold'),btn=s.querySelector('.foldbtn'),open=s.getAttribute('data-open')==='1';
              var saved=get(key);if(saved!==null)open=saved==='1';
              function set(o){s.classList.toggle('closed',!o);btn.setAttribute('aria-expanded',o?'true':'false')}
              set(open);
              btn.addEventListener('click',function(){var o=s.classList.contains('closed');set(o);put(key,o?'1':'0')});
            });
            var ok={};try{ok=JSON.parse(get('chestcounter-obtained')||'{}')||{}}catch(e){}
            function send(id,on){post('obtained',{id:id,on:on})}
            var boxes=document.querySelectorAll('input[data-prize]');
            function refresh(){
              var n=0;[].forEach.call(boxes,function(c){c.parentNode.classList.toggle('got',c.checked);if(c.checked)n++});
              var cc=document.getElementById('colcount');if(cc)cc.textContent=n+'/'+boxes.length;
              [].forEach.call(document.querySelectorAll('[data-items]'),function(card){
                var got=card.getAttribute('data-items').split(' ').some(function(id){var c=document.querySelector('input[data-prize="'+id+'"]');return c&&c.checked});
                card.classList.toggle('got',got);
              });
            }
            [].forEach.call(boxes,function(c){
              if(!c.disabled&&!c.hasAttribute('data-saved'))c.checked=!!ok[c.getAttribute('data-prize')];
              c.addEventListener('change',function(){var id=c.getAttribute('data-prize');if(c.checked)ok[id]=1;else delete ok[id];put('chestcounter-obtained',JSON.stringify(ok));send(id,c.checked);refresh()});
            });
            refresh();
            })();</script>
            """;
        sb.Append(L.T(
            "<footer>Counts the chests you opened or destroyed yourself, from your replays, analyzed after each match. Ammo boxes and food boxes are not counted. ★★ = golden treasure hunt chest, detected for sure. ★ = treasure received from one of your own chests (exact moment), named when the item can be identified. The team icon marks a treasure found by a teammate (estimate: deduced from the treasures you receive without opening a chest, as the treasure hunt shares them with the whole team); it is not in your totals. The squad total adds the chests opened or destroyed by your teammates, counted the same way in your replays; a chest opened more than 150 m away from you can be missing, as the replay does not record it.</footer>",
            "<footer>Считаются сундуки, которые ты сам открыл или разрушил, по твоим повторам, после каждого матча. Ящики с боеприпасами и ящики с едой не считаются. ★★ = золотой сундук охоты за сокровищами, определён точно. ★ = сокровище, полученное из твоего собственного сундука (точный момент); название указано, когда предмет удаётся определить. Значок команды отмечает сокровище, найденное союзником (оценка: выводится из сокровищ, которые ты получаешь, не открывая сундук, так как охота за сокровищами делит их на всю команду); в твои итоги оно не входит. Итог отряда добавляет сундуки, открытые или разрушенные союзниками, посчитанные так же по твоим повторам; сундук, открытый дальше 150 м от тебя, может отсутствовать, так как повтор его не записывает.</footer>"));
        sb.Append(script).Append("</main></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// Start of a section the player can fold: its title is a button (with a chevron), <paramref name="right"/> sits at the
    /// right of the title. The page remembers the state in the browser. Ends with <see cref="FoldEnd"/>.
    /// </summary>
    private static string FoldStart(string key, string title, bool open, string info = "", string right = "") =>
        $"""<section class="fold{(open ? "" : " closed")}" data-fold="{key}" data-open="{(open ? 1 : 0)}"><div class="head"><h2 class="pirate"><button type="button" class="foldbtn" aria-expanded="{(open ? "true" : "false")}"><span class="chev" aria-hidden="true"></span>{title}</button>{info}</h2>{right}</div><div class="foldbody">""";

    private const string FoldEnd = "</div></section>";

    /// <summary>A small pencil (edit), drawn with the current text color.</summary>
    public const string PencilIcon = """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4 20h4L19 9l-4-4L4 16z"/><path d="M14 6l4 4"/></svg>""";

    /// <summary>A small on/off switch (same look as the settings of the counter's window).</summary>
    private static string Switch(string id, string label) =>
        $"""<label class="tsw"><input type="checkbox" id="{id}"><span class="trk" aria-hidden="true"></span><span>{label}</span></label>""";

    /// <summary>Folded table controls: the page shows the first rows, "show more" adds some, "show all" / "fold" (script).</summary>
    private static string Pager(string tableId) =>
        $"""<div class="pager" data-for="{tableId}" data-step="10" hidden><span class="count" data-fmt="{H(L.T("{0} of {1}", "{0} из {1}"))}"></span><button type="button" class="more">{L.T("Show 20 more", "Показать ещё 20")}</button><button type="button" class="all">{L.T("Show all", "Показать все")}</button><button type="button" class="less" hidden>{L.T("Show less", "Свернуть")}</button></div>""";

    /// <summary>A small percentage with enough decimals to be meaningful (0.0064 %, 2.8 %, 63 %).</summary>
    private static string Percent(double p)
    {
        var v = p * 100;
        var decimals = v >= 10 ? 1 : v <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(v)) + 1, 1, 8);
        var text = v.ToString("F" + decimals, Cu);
        return L.Code == "en" ? text + "%" : text + " %";
    }

    private static string Info(string text) =>
        $"""<span class="info" tabindex="0" role="note" aria-label="{H(text)}">i<span class="infotip">{H(text)}</span></span>""";

    /// <summary>
    /// "Your chances": for each kind of treasure, the chance of having obtained it at least once with the chests opened
    /// so far (1 − (1 − p)^n).
    /// </summary>
    private static string ChancesSection(int chests, IReadOnlyList<MatchRecord> matches)
    {
        var now = DateTime.UtcNow;
        if (now >= TreasureCatalog.Weeks[^1].To.AddDays(14))
        {
            return ""; // the hunt is over: odds and prizes no longer apply
        }

        var sb = new StringBuilder();
        var outfits = string.Join(" ", TreasureCatalog.Weeks.SelectMany(w => w.Prizes).Where(p => p.Kind == TreasureCatalog.GoldOutfit).Select(p => p.Id));
        // When each counted chest was opened (UTC): a chest of a Treasure Hour counts with the boosted odds.
        var chestTimes = matches.SelectMany(m => m.Chests.Select(c => DateTime.SpecifyKind(m.MatchStartUtc ?? m.AnalyzedAtUtc, DateTimeKind.Utc).ToUniversalTime().AddSeconds(c.TimeSeconds))).ToList();
        var plain = Math.Max(0, chests - chestTimes.Count); // added by hand: time unknown, normal odds
        var boosted = chestTimes.Count(t => TreasureCatalog.Boost(TreasureCatalog.GoldOutfit, t) > 1);
        double Chance(TreasureOdds odds) => chests < chestTimes.Count
            ? TreasureCatalog.AtLeastOnce(odds.OneIn, chests)
            : TreasureCatalog.AtLeastOnce(odds, chestTimes, plain);
        var chancesInfo = L.T(
            $"Every chest always has the same odds (for example 1 in 1,562,500 for the gold outfit): they do not grow from one chest to the next. What grows is the chance of having obtained the item at least once over all your {chests.ToString("N0", Cu)} chests (those added by hand included): 1 − (1 − 1/X)^N. Chests opened during a Treasure Hour (Saturday 19:00-21:00 and Sunday 02:00-04:00 UTC) count with their boosted odds ({boosted.ToString("N0", Cu)} of yours). Chests opened before the treasure hunt did not give these treasures. Odds published by Epic Games, times from fortnite.bot.",
            $"У каждого сундука всегда одни и те же шансы (например, 1 из 1 562 500 для золотого наряда): от сундука к сундуку они не растут. Растёт вероятность получить предмет хотя бы раз за все твои {chests.ToString("N0", Cu)} сундуков (включая добавленные вручную): 1 − (1 − 1/X)^N. Сундуки, открытые во время Часа сокровищ (суббота 19:00–21:00 и воскресенье 02:00–04:00 UTC), считаются с повышенными шансами (таких у тебя: {boosted.ToString("N0", Cu)}). Сундуки, открытые до начала охоты за сокровищами, этих сокровищ не давали. Шансы опубликованы Epic Games, время — с fortnite.bot.");
        sb.Append(FoldStart("chances", L.T("Your chances", "Твои шансы"), open: true, Info(chancesInfo)));
        sb.Append($"{TreasureHourBanner()}<div class=\"chances\">");

        string Big(TreasureOdds odds, string items)
        {
            var p = Chance(odds);
            var half = TreasureCatalog.ChestsFor(odds.OneIn, 0.5);
            var left = Math.Max(0, half - chests);
            var width = Math.Clamp(p * 100, 0.6, 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            return $"""
                <div class="odds" data-items="{items}" data-kind="{odds.Kind}" data-p="{p.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"><div class="k"><span>{H(TreasureCatalog.KindName(odds.Kind))}<span class="boost" hidden></span></span><span class="gotbadge">✓ {L.T("obtained", "получено")}</span></div>
                <div class="v num">{Percent(p)}</div>
                <div class="l">{L.T($"chance of having had it at least once with your {chests.ToString("N0", Cu)} chests", $"вероятность получить хотя бы раз за твои {chests.ToString("N0", Cu)} сундуков")}</div>
                <span class="meter wide"><i style="width:{width}%"></i></span>
                <div class="s sline">{L.T($"1 in {odds.OneIn.ToString("N0", Cu)} per chest · one chance in two at about {half.ToString("N0", Cu)} chests", $"1 из {odds.OneIn.ToString("N0", Cu)} на сундук · один шанс из двух примерно за {half.ToString("N0", Cu)} сундуков")}{(left > 0 ? L.T($" ({left.ToString("N0", Cu)} to go)", $" (осталось {left.ToString("N0", Cu)})") : "")}</div></div>
                """;
        }
        sb.Append(Big(TreasureCatalog.Odds.First(o => o.Kind == TreasureCatalog.GoldOutfit), outfits));
        sb.Append(Big(TreasureCatalog.Odds.First(o => o.Kind == TreasureCatalog.PirateShop), TreasureCatalog.PirateShop));
        sb.Append("</div>");

        // The other kinds: chance of at least one, and how many of them on average with that many chests.
        sb.Append($"<div class=\"card\" style=\"margin-top:12px\"><table><tr><th>{L.T("Treasure", "Сокровище")}</th><th style=\"text-align:right\">{L.T("Per chest", "На сундук")}</th><th style=\"text-align:right\">{L.T("At least one", "Хотя бы раз")}</th><th style=\"text-align:right\">{L.T("On average", "В среднем")}</th></tr>");
        foreach (var odds in TreasureCatalog.Odds.Where(o => o.Kind is not (TreasureCatalog.GoldOutfit or TreasureCatalog.PirateShop)))
        {
            var average = (double)chests / odds.OneIn;
            sb.Append($"<tr data-kind=\"{odds.Kind}\"><td>{H(TreasureCatalog.KindName(odds.Kind))}<span class=\"boost\" hidden></span>{(odds.Weekly ? $" <span class=\"items\">· {L.T("changes every week", "меняется каждую неделю")}</span>" : "")}</td><td class=\"n num pc\">1 / {odds.OneIn.ToString("N0", Cu)}</td><td class=\"n num\">{Percent(Chance(odds))}</td><td class=\"n num\">{average.ToString(average >= 10 ? "N0" : "N1", Cu)}</td></tr>");
        }
        sb.Append($"</table></div>{FoldEnd}");
        return sb.ToString();
    }

    /// <summary>The collection of this season's treasures, to tick what was obtained (folded by default).</summary>
    private static string CollectionSection(IReadOnlyList<MatchRecord> matches, IReadOnlyList<string> obtained)
    {
        var now = DateTime.UtcNow;
        if (now >= TreasureCatalog.Weeks[^1].To.AddDays(14))
        {
            return ""; // the hunt is over
        }
        var sb = new StringBuilder();
        // Golden chests seen in the replays tick their prize by themselves.
        var ticked = obtained.ToHashSet();
        // A teammate's treasure is the player's too (the hunt gives it to the whole team).
        var seen = matches.SelectMany(m => m.TreasureFinds.Concat(m.TeamTreasureFinds)).Select(f => f.PrizeId).Where(id => id != null).Select(id => id!.ToLowerInvariant()).ToHashSet();
        string Prize(TreasurePrize p)
        {
            var auto = p.PrizeIds.Any(id => seen.Contains(id.ToLowerInvariant()));
            var saved = !auto && ticked.Contains(p.Id);
            var src = TreasureImages.Src(p.Image);
            var img = src != null ? $"""<img src="{H(src)}" alt="" loading="lazy" onerror="this.remove()">""" : "";
            return $"""<label class="prize{(auto ? " got" : "")}"><input type="checkbox" data-prize="{p.Id}"{(auto ? " checked disabled" : saved ? " checked data-saved=\"1\"" : "")}><span class="pimg">{img}</span><span class="pn">{H(p.Name)}</span><span class="pk">{H(TreasureCatalog.KindName(p.Kind))}{(auto ? " · " + L.T("seen in your replays", "замечено в твоих повторах") : "")}</span></label>""";
        }
        var collectionInfo = L.T(
            "Tick what you already obtained: it is saved in this browser only. A treasure from a golden chest is ticked by itself when the counter recognizes it. Apart from gizmos, the same treasure cannot be obtained twice. Images: fortnite.bot, downloaded once by the counter and kept in its data folder.",
            "Отмечай, что уже получил: это сохраняется только в этом браузере. Сокровище из золотого сундука отмечается само, когда счётчик его распознаёт. Кроме устройств, одно и то же сокровище нельзя получить дважды. Изображения: fortnite.bot, скачиваются счётчиком один раз и хранятся в его папке с данными.");
        sb.Append(FoldStart("collection", $"{L.T("Your collection", "Твоя коллекция")} · <span class=\"num\" id=\"colcount\"></span>", open: false, Info(collectionInfo)));
        sb.Append("<div class=\"card collection\">");
        sb.Append($"<div class=\"wk\"><div class=\"wt\">{L.T("All season", "Весь сезон")}</div><div class=\"prizes\">{Prize(TreasureCatalog.PirateShopAccess)}</div></div>");
        foreach (var week in TreasureCatalog.Weeks)
        {
            var current = now >= week.From && now < week.To;
            var dates = $"{week.From.ToLocalTime().ToString(L.T("MMM d HH:mm", "d MMM HH:mm"), Cu)} – {week.To.ToLocalTime().ToString(L.T("MMM d HH:mm", "d MMM HH:mm"), Cu)}";
            sb.Append($"<div class=\"wk{(current ? " now" : "")}\"><div class=\"wt\">{L.T("Week", "Неделя")} {week.Number} · {H(week.Title)} <span class=\"items\">{dates}{(current ? " · " + L.T("this week", "эта неделя") : "")}</span></div>");
            sb.Append(week.Prizes.Count > 0
                ? $"<div class=\"prizes\">{string.Concat(week.Prizes.Select(Prize))}</div></div>"
                : $"<div class=\"items\" style=\"padding:4px 0 8px\">{L.T("Prizes not revealed yet: they will come with an update of the counter.", "Призы ещё не раскрыты: они появятся с обновлением счётчика.")}</div></div>");
        }
        sb.Append("</div>").Append(FoldEnd);
        return sb.ToString();
    }

    /// <summary>
    /// The Treasure Hour banner: drawn by the page script from the schedule, against the clock of the PC, so that it
    /// switches by itself when an hour starts or ends ("live now, until 22:00" / "next one: Saturday 20:00").
    /// </summary>
    private static string TreasureHourBanner()
    {
        var hours = TreasureCatalog.TreasureHours.Select(h => new
        {
            s = new DateTimeOffset(h.StartUtc).ToUnixTimeMilliseconds(),
            e = new DateTimeOffset(h.EndUtc).ToUnixTimeMilliseconds(),
            b = h.Boosts.ToDictionary(kv => kv.Key, kv => kv.Value),
            c = h.Confirmed,
        });
        var odds = TreasureCatalog.Odds.ToDictionary(o => o.Kind, o => o.OneIn);
        var names = new Dictionary<string, string>
        {
            [TreasureCatalog.GoldOutfit] = TreasureCatalog.KindName(TreasureCatalog.GoldOutfit),
            [TreasureCatalog.PirateShop] = TreasureCatalog.KindName(TreasureCatalog.PirateShop),
            [TreasureCatalog.VehicleDecal] = TreasureCatalog.KindName(TreasureCatalog.VehicleDecal),
        };
        var text = new
        {
            live = L.T("Treasure Hour live now, until {0}", "Час сокровищ идёт сейчас, до {0}"),
            next = L.T("Next Treasure Hour: {0}", "Следующий Час сокровищ: {0}"),
            expected = L.T("expected, not announced yet", "ожидается, ещё не объявлен"),
            perChest = L.T("1 in {0} per chest", "1 из {0} на сундук"),
            boostPerChest = L.T("1 in {0} per chest during the Treasure Hour", "1 из {0} на сундук во время Часа сокровищ"),
            boostHalf = L.T(" · one chance in two within {0} more chests at this rate", " · один шанс из двух ещё за {0} сундуков в таком темпе"),
            locale = L.Culture.Name,
        };
        // The default encoder escapes < > & as <...: the JSON cannot close the <script> element it sits in.
        var json = System.Text.Json.JsonSerializer.Serialize(new { hours, odds, names, text });
        return $$"""
            <div class="thour" id="thour" hidden></div>
            <script type="application/json" id="thdata">{{json}}</script>
            <script>(function(){
            var d=JSON.parse(document.getElementById('thdata').textContent),el=document.getElementById('thour');
            function f(n){return Math.round(n).toLocaleString(d.text.locale)}
            function t(ms,withDay){var o=withDay?{weekday:'long',day:'numeric',month:'short',hour:'2-digit',minute:'2-digit'}:{hour:'2-digit',minute:'2-digit'};return new Date(ms).toLocaleString(d.text.locale,o)}
            function boosts(h,live){return Object.keys(h.b).map(function(k){return '<span><b>'+d.names[k]+' ×'+h.b[k].toLocaleString(d.text.locale)+'</b>'+(live?' · '+d.text.perChest.replace('{0}',f(d.odds[k]/h.b[k])):'')+'</span>'}).join('')}
            function draw(){
              var now=Date.now(),cur=d.hours.filter(function(h){return now>=h.s&&now<h.e})[0],nxt=d.hours.filter(function(h){return h.s>now}).sort(function(a,b){return a.s-b.s})[0];
              if(cur){el.className='thour live';el.innerHTML='<div class="tt">⚡ '+d.text.live.replace('{0}',t(cur.e,false))+'</div><div class="tb">'+boosts(cur,true)+'</div>';el.hidden=false}
              else if(nxt){el.className='thour';el.innerHTML='<div class="tt">'+d.text.next.replace('{0}',t(nxt.s,true))+(nxt.c?'':' <span class="exp">('+d.text.expected+')</span>')+'</div><div class="tb">'+boosts(nxt,false)+'</div>';el.hidden=false}
              else el.hidden=true;
              boost(cur);
            }
            // During a Treasure Hour, the odds of the boosted kinds (cards and table) show the boosted rate, in orange.
            function boost(cur){
              [].forEach.call(document.querySelectorAll('[data-kind]'),function(x){
                var k=x.getAttribute('data-kind'),b=cur&&cur.b[k]>1?cur.b[k]:0,badge=x.querySelector('.boost'),line=x.querySelector('.sline'),pc=x.querySelector('.pc');
                x.classList.toggle('boosted',!!b);
                if(badge){badge.hidden=!b;badge.textContent=b?'⚡ ×'+b.toLocaleString(d.text.locale):''}
                if(line){
                  if(!line.hasAttribute('data-n'))line.setAttribute('data-n',line.innerHTML);
                  if(b){var one=d.odds[k]/b,p=+x.getAttribute('data-p'),s=d.text.boostPerChest.replace('{0}',f(one));
                    if(p<0.5)s+=d.text.boostHalf.replace('{0}',f(Math.ceil(Math.log(0.5/(1-p))/Math.log(1-1/one))));
                    line.innerHTML='<span class="boostv">'+s+'</span>'}
                  else line.innerHTML=line.getAttribute('data-n');
                }
                if(pc){if(!pc.hasAttribute('data-n'))pc.setAttribute('data-n',pc.innerHTML);pc.innerHTML=b?'<span class="boostv">1 / '+f(d.odds[k]/b)+'</span>':pc.getAttribute('data-n')}
              });
            }
            draw();document.addEventListener('DOMContentLoaded',draw);setInterval(draw,30000);
            })();</script>
            """;
    }

    /// <summary>
    /// The rarest treasures (Pirate Shop Access, gold outfits) that other players got in the player's matches, from
    /// golden chests opened within reach of the replay. Experimental: always shown, even empty, to be checked with
    /// friends before it is trusted.
    /// </summary>
    private static string SeenSection(IReadOnlyList<MatchRecord> matches)
    {
        var rows = matches
            .SelectMany(m => m.SeenPrizes.Select(s => (m, s, Prize: TreasureCatalog.Find(s.PrizeId))))
            .Where(r => r.Prize?.Kind is TreasureCatalog.PirateShop or TreasureCatalog.GoldOutfit)
            .OrderByDescending(r => When(r.m)).ThenBy(r => r.s.TimeSeconds)
            .ToList();
        var info = L.T(
            "A golden chest is sent to every player within about 150 m, with the treasure and the player who opened it: these were seen in your replays. A player farther away from you is not in them.",
            "Золотой сундук отправляется каждому игроку примерно в 150 м, вместе с сокровищем и игроком, который его открыл: эти были замечены в твоих повторах. Игрока дальше от тебя в них нет.");
        var sb = new StringBuilder();
        sb.Append(FoldStart("seen", $"{L.T("They got it in your matches", "Получили в твоих матчах")} · <span class=\"num\">{rows.Count}</span>", open: false,
            $" <span class=\"exper\">{L.T("experimental", "экспериментально")}</span>{Info(info)}"));
        sb.Append("<div class=\"card\">");
        if (rows.Count == 0)
        {
            sb.Append($"<p class=\"items\" style=\"margin:8px 0\">{L.T("None yet: when a player within 150 m of you gets the Pirate Shop Access or a gold outfit, they show up here with the match, their name and the moment.", "Пока никого: когда игрок в пределах 150 м от тебя получит доступ в пиратскую лавку или золотой наряд, он появится здесь вместе с матчем, именем и моментом.")}</p></div>{FoldEnd}");
            return sb.ToString();
        }
        sb.Append($"<table id=\"t-seen\"><tr><th>{L.T("Match", "Матч")}</th><th>{L.T("Player", "Игрок")}</th><th>{L.T("Treasure", "Сокровище")}</th><th>{L.T("Time", "Время")}</th></tr>");
        foreach (var (m, s, prize) in rows)
        {
            var name = prize!.Kind == TreasureCatalog.PirateShop ? TreasureCatalog.KindName(prize.Kind) : prize.Name;
            sb.Append($"<tr class=\"r\"><td class=\"nw num items\">{L.DateTimeShort(When(m))}</td><td><b>{H(s.Player)}</b></td><td><span class=\"gold\">★★</span> {H(name)}</td><td class=\"nw num\">{Pretty.Clock(s.TimeSeconds)}</td></tr>");
        }
        sb.Append($"</table>{Pager("t-seen")}</div>{FoldEnd}");
        return sb.ToString();
    }

    /// <summary>"i" next to the squad total: why the teammates' count is lower than what they really opened.</summary>
    private static string SquadInfo
    {
        get
        {
            var text = L.T(
                "Why is this number low? It comes from YOUR replays. Fortnite only records what happens within about 150 m of you: a chest a teammate opens farther away is not in it and cannot be counted. Nothing either once you leave the match (eliminated, back to the lobby). The more you loot together, the closer it gets. The chests that are counted are counted with the same rules as yours.",
                "Почему это число маленькое? Оно берётся из ТВОИХ повторов. Fortnite записывает только то, что происходит примерно в 150 м от тебя: сундук, который союзник открыл дальше, туда не попадает и не может быть посчитан. Ничего не записывается и после твоего выхода из матча (выбыл, вернулся в лобби). Чем больше вы лутаетесь вместе, тем точнее. Посчитанные сундуки считаются по тем же правилам, что и твои.");
            return $"""<span class="info" tabindex="0" role="note" aria-label="{H(text)}">i<span class="infotip">{H(text)}</span></span>""";
        }
    }

    private static string VehicleWarning => L.T(
        "Fortnite did not record what your vehicle destroyed in this replay: chest loot was seen along your drive with no chest recorded, so those chests cannot be counted. Turn off \"Record High Quality Replays\" in Fortnite (Settings → Game → Replays), then restart the game.",
        "Fortnite не записал, что разрушил твой транспорт в этом повторе: вдоль пути видна добыча из сундуков, но сами сундуки не записаны, поэтому их нельзя посчитать. Выключи «Записывать повторы высокого качества» в Fortnite (Настройки → Игра → Повторы) и перезапусти игру.");

    /// <summary>Messages of content.json for these days (text only, escaped).</summary>
    private static string NoticeCards() => string.Concat(TreasureCatalog.ActiveNotices(DateTime.UtcNow)
        .Select(n => $"<div class=\"notice{(n.Level == "warning" ? " warning" : "")}\">{(n.TitleIn(L.Code) is { Length: > 0 } title ? $"<b>{H(title)}</b><br>" : "")}{H(n.TextIn(L.Code))}{(n.Link != null ? $"<br><a href=\"{H(n.Link)}\" target=\"_blank\" rel=\"noopener\">{L.T("More info", "Подробнее")}</a>" : "")}</div>"));

    private static string LoadingCard(ScanProgress? p)
    {
        if (p == null)
        {
            return "";
        }
        var pct = p.Total > 0 ? (int)Math.Round(100.0 * p.Done / p.Total) : 0;
        return L.T(
            $"""<div class="loading"><div class="spin"></div><div class="grow"><b>Analyzing your replays… {p.Done} / {p.Total}</b><div class="pbar"><i style="width:{pct}%"></i></div><span class="muted">This page updates by itself: the numbers below grow as the replays are read.</span></div></div>""",
            $"""<div class="loading"><div class="spin"></div><div class="grow"><b>Анализирую твои повторы… {p.Done} / {p.Total}</b><div class="pbar"><i style="width:{pct}%"></i></div><span class="muted">Страница обновляется сама: числа ниже растут по мере чтения повторов.</span></div></div>""");
    }

    private static string ProfilesLine(IReadOnlyList<Profile> profiles, string player)
    {
        var others = profiles.Where(p => p.Name != player).Select(p => H(p.Name)).ToList();
        if (others.Count == 0)
        {
            return "";
        }
        return $"""<div class="profiles muted">{L.T("Other profiles on this PC: ", "Другие профили на этом ПК: ")}{string.Join(", ", others)} · {L.T("\"Switch profile\" in the counter", "«Сменить профиль» в счётчике")}</div>""";
    }

    /// <summary>Style of the OBS page: the one chosen for the overlay ("hud" or "classic").</summary>
    public static volatile string OverlayStyle = "hud";

    /// <summary>What the overlay shows right now (also written by <see cref="WriteAll"/>, so that the OBS page never goes back).</summary>
    private static volatile OverlayView? LastOverlay;

    /// <summary>Rewrites only the OBS overlay page (called on every live update).</summary>
    public static void WriteOverlay(Store store, OverlayView view)
    {
        LastOverlay = view;
        try
        {
            WriteAtomic(store.OverlayPath, Overlay(view));
        }
        catch
        {
            // the page is a convenience: never stop the tracker for it
        }
    }

    public static string Overlay(OverlayView v) => OverlayStyle == "classic" ? OverlayClassic(v) : OverlayHud(v);

    /// <summary>
    /// The HUD overlay as a web page, for OBS ("Browser" source, local file): transparent, figures outlined like the
    /// game's, same rows as the overlay on screen. Refreshes itself every 5 seconds.
    /// </summary>
    private static string OverlayHud(OverlayView v)
    {
        string state;
        if (v.CurrentLive && v.Current is { } cur && cur != "…")
        {
            state = Row(L.T("Live", "Live"), cur, "gold", dot: true);
        }
        else if (v.CurrentLive)
        {
            state = Row(L.T("In a match", "В матче"), "", "", dot: true);
        }
        else
        {
            state = Row(L.T("Last", "Последний"), v.Last ?? "—", "");
        }
        var squad = v.Squad != null ? Row(OverlayModel.SquadLabel, v.Squad, "") : "";
        static string Row(string label, string value, string cls, bool dot = false) =>
            $"<div class=\"r {cls}\">{(dot ? "<i class=\"dot\"></i>" : "")}<span>{H(label)}</span>{(value.Length > 0 ? $"<span class=\"sep\">•</span><b>{H(value)}</b>" : "")}</div>";
        return $$"""
            <!doctype html><html lang="{{L.Code}}"><head><meta charset="utf-8"><meta http-equiv="refresh" content="5">
            <title>{{L.T("Chest overlay", "Оверлей сундуков")}}</title>
            <style>
            html,body{margin:0;background:transparent}
            body{padding:10px 14px;display:inline-block;color:#fff;font-family:Bahnschrift,"Segoe UI",sans-serif;font-style:italic;font-weight:600;text-align:right;
                 -webkit-text-stroke:4px #0b0f18;paint-order:stroke fill;letter-spacing:.01em}
            .total{display:flex;align-items:center;justify-content:flex-end;gap:10px;font-size:46px;font-weight:700;font-stretch:condensed;line-height:1.05}
            .total.live{color:#ffd23f}
            .total svg{width:30px;height:26px;flex:none}
            .r{display:flex;align-items:center;justify-content:flex-end;gap:8px;font-size:21px;font-stretch:semi-condensed;line-height:1.3}
            .r.gold b{color:#ffd23f}
            .sep{opacity:.9}
            .dot{width:10px;height:10px;border-radius:50%;background:#ff3b30;box-shadow:0 0 0 2px #0b0f18}
            .t{color:#d6beff}.t .star{color:#c49bff}
            </style></head><body>
            <div class="total{{(v.CurrentLive && v.Current is { } c && c != "…" ? " live" : "")}}"><svg viewBox="0 0 30 26" aria-hidden="true"><rect x="2" y="10" width="26" height="14" rx="2" fill="#ffd23f" stroke="#0b0f18" stroke-width="2.5"/><path d="M2 12 Q15 -2 28 12" fill="#e0a526" stroke="#0b0f18" stroke-width="2.5"/><rect x="12.5" y="12" width="5" height="6" rx="1" fill="#0b0f18"/></svg>{{H(v.Total)}}</div>
            {{state}}
            {{Row(L.T("Session", "Сессия"), v.Session, "")}}
            {{squad}}
            <div class="r t"><span class="star">★</span><b>{{H(v.Treasures)}}</b></div>
            </body></html>
            """;
    }

    /// <summary>The classic overlay (dark card) as a web page, for OBS.</summary>
    private static string OverlayClassic(OverlayView v)
    {
        var rows = new StringBuilder();
        foreach (var r in v.Rows)
        {
            rows.Append($"<div class=\"row{(r.Treasure ? " t" : "")}\"><span>{H(r.Label)}</span><b>{H(r.Value)}{(r.Detail != null ? $" <small>{H(r.Detail)}</small>" : "")}</b></div>");
        }
        return $$"""
            <!doctype html><html lang="{{L.Code}}"><head><meta charset="utf-8"><meta http-equiv="refresh" content="5">
            <title>{{L.T("Chest overlay", "Оверлей сундуков")}}</title>
            <style>
            html,body{margin:0;background:transparent;color:#e2e2e8;font:500 16px/1.3 "Segoe UI",system-ui,sans-serif}
            .box{display:inline-block;min-width:260px;padding:14px 18px 12px;border-radius:16px;background:rgba(18,18,22,.86);border:1px solid #3a3a42;text-shadow:0 1px 3px rgba(0,0,0,.6)}
            .head{display:flex;justify-content:space-between;gap:16px;font:700 12px "Segoe UI",sans-serif;letter-spacing:.08em;color:#9696a2}
            .live{color:#60d680}
            .counters{display:flex;align-items:stretch;gap:18px;margin:4px 0 10px}
            .counters>div{display:flex;flex-direction:column}
            .sep{width:1px;background:#2c2c34;margin:8px 0 2px}
            .big{font-size:40px;font-weight:800;line-height:1.1;font-variant-numeric:tabular-nums}
            .total{color:#f2b84b}.cur{color:#6ee096}.none{color:#50505c}
            .cap{font-size:11px;font-weight:600;color:#82828e}
            .rows{border-top:1px solid #2c2c34;padding-top:8px;display:grid;grid-template-columns:auto 1fr;gap:4px 12px}
            .row{display:contents}.row span{font-size:13px;font-weight:600;color:#82828e}.row b{font-weight:500}
            .row.t b{color:#c4a0ff}.row small{font-size:11px;font-weight:600;color:#82828e}
            </style></head><body><div class="box">
            <div class="head"><span>{{L.T("CHESTS", "СУНДУКИ")}}</span>{{(v.Badge != null ? $"<span class=\"live\">{H(v.Badge)}</span>" : "")}}</div>
            <div class="counters">
              <div><span class="big total">{{H(v.Total)}}</span><span class="cap">total</span></div>
              <div class="sep"></div>
              <div><span class="big {{(v.Current == null ? "none" : v.CurrentLive ? "cur" : "")}}">{{H(v.Current ?? "—")}}</span><span class="cap">{{H(v.CurrentCaption)}}</span></div>
            </div>
            <div class="rows">{{rows}}</div>
            </div></body></html>
            """;
    }

    public static string Csv(TrackerData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("date;mode;player;placement;players;chests;teammate_chests;ammo_boxes;treasures;file", "дата;режим;игрок;место;игроков;сундуки;сундуки_союзников;ящики_с_боеприпасами;сокровища;файл"));

        foreach (var m in Profiles.Counted(data).OrderBy(When))
        {
            var t = string.Join(" + ", m.TreasureFinds.Select(f => f.DisplayName));
            sb.AppendLine(string.Join(";",
                When(m).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Pretty.Playlist(m.Playlist), m.PlayerName, m.Placement, m.TotalPlayers,
                m.ChestsOpened, m.TeamChestsOpened, m.AmmoBoxesOpened, t, m.ReplayFile));
        }
        return sb.ToString();
    }
}
