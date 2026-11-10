using ChestCounter;
using Xunit;

// L.Code is static: the tests that switch the language must not run side by side.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

public class LanguageTests
{
    [Theory]
    [InlineData(0, "0 сундуков")]
    [InlineData(1, "1 сундук")]
    [InlineData(2, "2 сундука")]
    [InlineData(4, "4 сундука")]
    [InlineData(5, "5 сундуков")]
    [InlineData(11, "11 сундуков")]
    [InlineData(14, "14 сундуков")]
    [InlineData(21, "21 сундук")]
    [InlineData(22, "22 сундука")]
    [InlineData(25, "25 сундуков")]
    [InlineData(111, "111 сундуков")]
    public void RussianPlurals(int n, string expected)
    {
        L.Set("ru");
        Assert.Equal(expected, L.Count(n, "chest", "chests", "сундук", "сундука", "сундуков"));
    }

    [Fact]
    public void EnglishPlurals()
    {
        L.Set("en");
        Assert.Equal("1 chest", L.Count(1, "chest", "chests", "сундук", "сундука", "сундуков"));
        Assert.Equal("0 chests", L.Count(0, "chest", "chests", "сундук", "сундука", "сундуков"));
    }

    [Fact]
    public void RemovedLanguagesAreAskedAgain()
    {
        Assert.Null(L.Normalize("fr")); // a config.json saved in French or Spanish: the language is asked again
        Assert.Null(L.Normalize("es"));
        Assert.Equal("ru", L.Normalize(" RU "));
    }

    [Fact]
    public void StoredKeysAreTranslated()
    {
        L.Set("en");
        Assert.Equal("Rare chest", L.Name("Coffre rare"));
        L.Set("ru");
        Assert.Equal("Редкий сундук", L.Name("Coffre rare"));
        Assert.Equal("something new", L.Name("something new"));
    }
}

public class ReleaseNotesTests
{
    private const string Notes = "**English** · intro\n\n**What's new**\n- **Bold** one\n- two\n\nfooter\n\n---\n\n**Русский** · вступление\n\n**Что нового**\n- раз\n- [два](http://x)\n";

    [Fact]
    public void PicksTheLanguageSection()
    {
        Assert.Equal(new[] { "Bold one", "two" }, ReleaseNotes.WhatsNew(Notes, "en"));
        Assert.Equal(new[] { "раз", "два" }, ReleaseNotes.WhatsNew(Notes, "ru"));
    }

    [Fact]
    public void FallsBackToEnglish() => Assert.Equal(new[] { "Bold one", "two" }, ReleaseNotes.WhatsNew(Notes, "de"));

    /// <summary>RELEASE_NOTES.md is shown in the counter after an update: both languages must be readable.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void EmbeddedNotesHaveBothLanguages(string language)
    {
        Assert.NotEmpty(ReleaseNotes.WhatsNew(ReleaseNotes.Embedded, language));
    }
}

public class ReplayHeaderProbeTests
{
    /// <summary>The start of a replay file, as Fortnite writes it (file version 6: no custom version list).</summary>
    private static string Header(bool live, byte[]? key)
    {
        var path = Path.Combine(Path.GetTempPath(), $"chestcounter-test-{Guid.NewGuid():N}.replay");
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x1CA2E27Fu); // magic
        w.Write(6u); // file version
        w.Write(0u); w.Write(0u); w.Write(0u); // length, network version, changelist
        w.Write(5); w.Write("name\0"u8); // friendly name
        w.Write(live ? 1u : 0u);
        w.Write(0L); // timestamp
        w.Write(1u); // compressed
        w.Write(key != null ? 1u : 0u); // encrypted
        if (key != null)
        {
            w.Write(key.Length);
            w.Write(key);
        }
        return path;
    }

    [Fact]
    public void MatchInProgressHasNoKey()
    {
        var path = Header(live: true, key: new byte[32]);
        Assert.True(ReplayHeaderProbe.IsLive(path));
        Assert.True(ReplayHeaderProbe.KeyMissing(path));
        File.Delete(path);
    }

    [Fact]
    public void FinishedMatchHasItsKey()
    {
        var path = Header(live: false, key: Enumerable.Repeat((byte)7, 32).ToArray());
        Assert.False(ReplayHeaderProbe.IsLive(path));
        Assert.False(ReplayHeaderProbe.KeyMissing(path));
        File.Delete(path);
    }

    [Fact]
    public void RewrittenHeaderIsReadAgain()
    {
        // Fortnite clears the live flag and writes the key in place when the match ends: the cache must not hide it.
        var path = Header(live: true, key: new byte[32]);
        Assert.True(ReplayHeaderProbe.KeyMissing(path));
        File.Delete(path);
        var done = Header(live: false, key: Enumerable.Repeat((byte)7, 32).ToArray());
        File.Move(done, path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.False(ReplayHeaderProbe.IsLive(path));
        Assert.False(ReplayHeaderProbe.KeyMissing(path));
        File.Delete(path);
    }

    [Fact]
    public void NotAReplay()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not a replay at all");
        Assert.Null(ReplayHeaderProbe.IsLive(path));
        Assert.True(ReplayHeaderProbe.KeyMissing(path));
        File.Delete(path);
        Assert.Null(ReplayHeaderProbe.IsLive(path)); // missing file
    }
}
