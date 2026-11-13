using System.Text;

namespace ChestCounter;

/// <summary>Whether the process writes into a real console (command line options), where a progress bar can be redrawn.</summary>
public static class ConsoleUi
{
    /// <summary>True when keys can be read and the cursor moved.</summary>
    public static bool Interactive { get; } = !Console.IsInputRedirected && !Console.IsOutputRedirected;
}

/// <summary>A piece of text with its colors.</summary>
public readonly record struct Seg(string Text, ConsoleColor Fg = ConsoleColor.Gray, ConsoleColor? Bg = null);

/// <summary>
/// Replaces Console.Out: what the counter prints (match counted, errors…) is kept as colored lines, shown in the
/// "Activity" of the window's home tab.
/// A "\r" rewrites the current line (progress bar).
/// </summary>
public sealed class ActivityLog : TextWriter
{
    private const int Max = 300;
    private readonly object _gate = new();
    private readonly List<Seg[]> _lines = new();
    private readonly List<Seg> _current = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        ConsoleColor fg;
        try { fg = Console.ForegroundColor; } catch { fg = ConsoleColor.Gray; }
        lock (_gate)
        {
            var start = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c is '\n' or '\r')
                {
                    if (i > start) _current.Add(new Seg(value[start..i], fg));
                    if (c == '\n')
                    {
                        Commit();
                    }
                    else
                    {
                        _current.Clear(); // progress bar: the line is drawn again
                    }
                    start = i + 1;
                }
            }
            if (start < value.Length)
            {
                _current.Add(new Seg(value[start..], fg));
            }
        }
    }

    public override void WriteLine(string? value)
    {
        Write((value ?? "") + "\n");
    }

    public override void WriteLine() => Write("\n");

    private void Commit()
    {
        _lines.Add(_current.ToArray());
        _current.Clear();
        if (_lines.Count > Max)
        {
            _lines.RemoveRange(0, _lines.Count - Max);
        }
    }

    /// <summary>The last <paramref name="count"/> lines (the line being written, e.g. a progress bar, comes last).</summary>
    public List<Seg[]> Last(int count)
    {
        lock (_gate)
        {
            var all = new List<Seg[]>(_lines);
            if (_current.Any(s => s.Text.Trim().Length > 0))
            {
                all.Add(_current.ToArray());
            }
            // Blank lines are only spacing in the old scrolling layout.
            all = all.Where(l => l.Any(s => s.Text.Trim().Length > 0)).ToList();
            return all.Skip(Math.Max(0, all.Count - count)).ToList();
        }
    }
}

/// <summary>One line progress bar, redrawn in place: "  Analyse  [██████░░░░]  12 / 31  file".</summary>
public sealed class ProgressBar
{
    private readonly string _label;
    private readonly int _total;
    private int _lastLength;

    public ProgressBar(string label, int total)
    {
        _label = label;
        _total = Math.Max(1, total);
    }

    public void Update(int done, string? detail = null)
    {
        const int barWidth = 28;
        var filled = (int)Math.Round(barWidth * Math.Clamp(done, 0, _total) / (double)_total);
        var bar = new string('█', filled) + new string('░', barWidth - filled);
        var count = $"{done} / {_total}";
        if (!ConsoleUi.Interactive)
        {
            return; // no redraw possible: the summary line at the end is enough
        }

        int width;
        try { width = Console.WindowWidth; } catch { width = 80; }
        var text = $"  {_label}  {bar}  {count}";
        if (!string.IsNullOrEmpty(detail))
        {
            var room = width - text.Length - 4;
            if (room > 8)
            {
                text += "  " + (detail.Length > room ? "…" + detail[^(room - 1)..] : detail);
            }
        }
        Console.Write("\r");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"  {_label}  ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write(bar);
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.Write(text[(4 + _label.Length + bar.Length)..]);
        Console.ResetColor();
        if (text.Length < _lastLength)
        {
            Console.Write(new string(' ', _lastLength - text.Length));
        }
        _lastLength = text.Length;
    }

    /// <summary>Erases the bar so the summary can be written on the same line.</summary>
    public void Clear()
    {
        if (!ConsoleUi.Interactive || _lastLength == 0)
        {
            return;
        }
        Console.Write("\r" + new string(' ', _lastLength) + "\r");
        _lastLength = 0;
    }
}
