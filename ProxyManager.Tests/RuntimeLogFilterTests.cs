using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class RuntimeLogFilterTests
{
    [Theory]
    [InlineData("INFO[0000] sing-box started", RuntimeLogLevel.Info)]
    [InlineData("ERROR[0003] bad", RuntimeLogLevel.Error)]
    [InlineData("WARN", RuntimeLogLevel.Warn)]
    [InlineData("2026-09-01 12:00:00 DEBUG msg", RuntimeLogLevel.Debug)]
    [InlineData("2026-09-01T12:00:00 INFO msg", RuntimeLogLevel.Info)]
    [InlineData("2026-09-01 12:00:00.123 TRACE detail", RuntimeLogLevel.Trace)]
    [InlineData("PANIC: boom", RuntimeLogLevel.Fatal)]
    [InlineData("FATAL x", RuntimeLogLevel.Fatal)]
    public void TryParseLevel_RecognizesSingBoxConsoleTokens(string line, RuntimeLogLevel expected)
    {
        Assert.True(RuntimeLogFilter.TryParseLevel(line, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("")]
    public void TryParseLevel_UnrecognizedFallsBackToInfo(string line)
    {
        Assert.False(RuntimeLogFilter.TryParseLevel(line, out var level));
        Assert.Equal(RuntimeLogLevel.Info, level);
    }

    [Theory]
    [InlineData("info x")]
    [InlineData("Information about the route")]
    public void TryParseLevel_RejectsNonUppercaseOrOverlongTokens(string line)
    {
        Assert.False(RuntimeLogFilter.TryParseLevel(line, out var level));
        Assert.Equal(RuntimeLogLevel.Info, level);
    }

    [Fact]
    public void TryParseLevel_NullLineIsNotALevel()
    {
        Assert.False(RuntimeLogFilter.TryParseLevel(null, out var level));
        Assert.Equal(RuntimeLogLevel.Info, level);
    }

    [Fact]
    public void Matches_PassesLineAtOrAboveMinimumLevel()
    {
        Assert.True(RuntimeLogFilter.Matches("WARN outbound degraded", RuntimeLogLevel.Info, null));
        Assert.False(RuntimeLogFilter.Matches("DEBUG dialing 127.0.0.1:1080", RuntimeLogLevel.Info, null));
    }

    [Fact]
    public void Matches_TreatsUnrecognizedLinesAsInfo()
    {
        Assert.False(RuntimeLogFilter.Matches("started", RuntimeLogLevel.Warn, null));
        Assert.True(RuntimeLogFilter.Matches("started", RuntimeLogLevel.Info, null));
    }

    [Fact]
    public void Matches_SearchIsCaseInsensitive()
    {
        Assert.True(RuntimeLogFilter.Matches(
            "INFO[0000] sing-box started",
            RuntimeLogLevel.Trace,
            "SING-BOX"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_BlankSearchMatchesEverything(string searchText)
    {
        Assert.True(RuntimeLogFilter.Matches("INFO[0000] sing-box started", RuntimeLogLevel.Info, searchText));
    }

    [Fact]
    public void Matches_CombinesLevelAndSearch()
    {
        Assert.True(RuntimeLogFilter.Matches(
            "ERROR[0001] inbound timeout on 127.0.0.1",
            RuntimeLogLevel.Warn,
            "timeout"));
        Assert.False(RuntimeLogFilter.Matches(
            "DEBUG dialing 127.0.0.1:1080",
            RuntimeLogLevel.Info,
            "1080"));
    }

    [Fact]
    public void BuildExportText_JoinsSnapshotsWithEnvironmentNewline()
    {
        var text = RuntimeLogFilter.BuildExportText(new[]
        {
            new RuntimeLogLineSnapshot("t1", "m1"),
            new RuntimeLogLineSnapshot("t2", "m2")
        });

        Assert.Equal($"[t1] m1{Environment.NewLine}[t2] m2", text);
    }

    [Fact]
    public void BuildExportText_EmptyInputYieldsEmptyText()
    {
        Assert.Equal(string.Empty, RuntimeLogFilter.BuildExportText([]));
    }

    [Fact]
    public void BuildExportText_PreservesNonSecretMessages()
    {
        var text = RuntimeLogFilter.BuildExportText(
            [new RuntimeLogLineSnapshot("12:00:00", "INFO[0000] 无敏感内容")]);

        Assert.Equal("[12:00:00] INFO[0000] 无敏感内容", text);
    }

    [Fact]
    public void BuildExportText_RedactsJsonPasswordsAsSecondLineOfDefense()
    {
        var text = RuntimeLogFilter.BuildExportText(
            [new RuntimeLogLineSnapshot("12:00:01", "ERROR[0001] config {\"password\": \"hunter2\"}")]);

        Assert.Contains("\"password\": \"***\"", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void BuildExportText_RedactsKeyValueSecrets()
    {
        var text = RuntimeLogFilter.BuildExportText(
            [new RuntimeLogLineSnapshot("12:00:02", "INFO[0002] server password=hunter2 rejected")]);

        Assert.Contains("password=***", text);
        Assert.DoesNotContain("hunter2", text);
    }
}
