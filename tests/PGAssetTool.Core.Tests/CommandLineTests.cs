using PGAssetTool.Cli;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

public class CommandLineTests
{
    [Fact]
    public void OptionsMayComeBeforeTheCommand()
    {
        var line = CommandLine.Parse(["--game", @"D:\PG", "info"]);

        Assert.Equal("info", line.Command);
        Assert.Equal(@"D:\PG", line.Option("game"));
        Assert.Empty(line.Positional);
    }

    [Fact]
    public void PositionalArgumentsMayComeAfterOptions()
    {
        // Read in place, this extracted nothing: the item was after the first option and so was
        // never looked at.
        var line = CommandLine.Parse(["extract", "--skin", "Neon", "819", "--workspace"]);

        Assert.Equal("extract", line.Command);
        Assert.Equal(["819"], line.Positional);
        Assert.Equal("Neon", line.Option("skin"));
        Assert.True(line.Has("workspace"));
    }

    [Fact]
    public void AValueCanBeJoinedToItsOption()
    {
        var line = CommandLine.Parse(["show", "819", "--language=l_ja"]);
        Assert.Equal("l_ja", line.Option("language"));
    }

    [Theory]
    [InlineData("--workpace")]
    [InlineData("--frobnicate")]
    public void AnUnknownOptionIsRefusedByName(string option)
    {
        // Ignored, a misspelt --workspace turned a request for a workspace into a plain extract.
        var wrong = Assert.Throws<CommandLineException>(() => CommandLine.Parse(["extract", "819", option]));
        Assert.Contains(option, wrong.Message);
    }

    [Theory]
    [InlineData("show --game")]
    [InlineData("show --game --force")]
    public void AnOptionMissingItsValueIsRefused(string typed)
    {
        var wrong = Assert.Throws<CommandLineException>(() => CommandLine.Parse(typed.Split(' ')));
        Assert.Contains("--game", wrong.Message);
    }

    [Fact]
    public void AFlagGivenAValueIsRefused()
    {
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["apply", "x.pgmod", "--force=yes"]));
    }

    [Fact]
    public void EverythingAfterTheSeparatorIsAnArgument()
    {
        var line = CommandLine.Parse(["remove", "--", "--odd-id"]);
        Assert.Equal(["--odd-id"], line.Positional);
    }

    [Theory]
    [InlineData("")]
    [InlineData("help")]
    [InlineData("-h")]
    [InlineData("extract --help")]
    public void HelpIsAskedForEveryUsualWay(string typed)
    {
        Assert.True(CommandLine.Parse(typed.Split(' ', StringSplitOptions.RemoveEmptyEntries)).WantsHelp);
    }

    [Fact]
    public void TheCommandIsReadWithoutMindingCase()
    {
        Assert.Equal("show", CommandLine.Parse(["SHOW", "819"]).Command);
    }

    [Fact]
    public void ANumberThatIsNotOneIsRefused()
    {
        var line = CommandLine.Parse(["show", "819", "--read-memory", "lots"]);
        Assert.Throws<CommandLineException>(() => line.Number("read-memory", 1024));
        Assert.Equal(1024, CommandLine.Parse(["show", "819"]).Number("read-memory", 1024));
        Assert.Equal(0, CommandLine.Parse(["show", "819", "--read-memory", "0"]).Number("read-memory", 1024));
    }

    [Fact]
    public void TwoWordsWhereOneIsWantedAreRefusedRatherThanHalved()
    {
        // The game has an avatar whose id is `avatar_ programmer`. Unquoted, taking the first word
        // would have answered for some other item entirely.
        var line = CommandLine.Parse(["show", "avatar_", "programmer"]);
        var wrong = Assert.Throws<CommandLineException>(() => line.Single("an item"));
        Assert.Contains("Quote", wrong.Message);

        Assert.Equal("avatar_ programmer", CommandLine.Parse(["show", "avatar_ programmer"]).Single("an item"));
    }

    [Fact]
    public void TheToolsOwnRefusalsAreExpectedAndAnEmptySequenceIsNot()
    {
        // Both are InvalidOperationException. One says what to do; the other is a bug, and is
        // what an extract of a pet with no dressed mesh used to report as though it were advice.
        Exception empty;
        try { _ = Array.Empty<(int, int)>().MaxBy(t => t.Item1); empty = null!; }
        catch (InvalidOperationException e) { empty = e; }

        Exception refused;
        try { PackBuilder.Inside(Path.GetTempPath(), "../outside.png", "the file"); refused = null!; }
        catch (InvalidOperationException e) { refused = e; }

        Assert.False(CommandLine.IsExpected(empty));
        Assert.True(CommandLine.IsExpected(refused));
        Assert.True(CommandLine.IsExpected(new FileNotFoundException("gone")));
        Assert.False(CommandLine.IsExpected(new NullReferenceException()));
    }
}
