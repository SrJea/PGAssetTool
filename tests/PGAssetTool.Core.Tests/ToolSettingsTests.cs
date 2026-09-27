using PGAssetTool.Core.Settings;

namespace PGAssetTool.Core.Tests;

public class ToolSettingsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("pgassettool-settings").FullName;

    [Fact]
    public void NothingSavedYetMeansTheDefaults()
    {
        var settings = ToolSettings.Load(_home);

        Assert.Equal("l_en-gb", settings.Language);
        Assert.True(settings.ReplaceableOnly);
        Assert.False(settings.OpaqueTextures);

        // A pack that leaves the machine it was built on is what protection is for, and it cannot be
        // added afterwards. Both windows read this, and the editor's list names what it answers.
        Assert.True(settings.ProtectPacks);

        // What every size in the views is written in.
        Assert.Equal(100, settings.UiScale);
    }

    [Fact]
    public void WhatIsSavedComesBack()
    {
        new ToolSettings
        {
            Language = "l_ja", ReplaceableOnly = false, OpaqueTextures = true,
            ProtectPacks = false, UiScale = 125,
        }.Save(_home);

        var settings = ToolSettings.Load(_home);
        Assert.Equal("l_ja", settings.Language);
        Assert.False(settings.ReplaceableOnly);
        Assert.True(settings.OpaqueTextures);
        Assert.False(settings.ProtectPacks);
        Assert.Equal(125, settings.UiScale);
    }

    [Fact]
    public void SettingsSitBesideTheToolRatherThanInTheUserProfile()
    {
        // Copying the folder has to copy the setup with it, and nothing may be left behind in the
        // registry or under the profile when the tool is carried elsewhere.
        new ToolSettings().Save(_home);

        Assert.True(File.Exists(Path.Combine(_home, ToolSettings.FileName)));
        Assert.StartsWith(_home, ToolSettings.PathIn(_home), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatCannotBeReadFallsBackRatherThanStoppingTheTool()
    {
        // These are preferences. None of them is worth refusing to start over.
        File.WriteAllText(Path.Combine(_home, ToolSettings.FileName), "{ this is not json");

        Assert.Equal("l_en-gb", ToolSettings.Load(_home).Language);
    }

    [Fact]
    public void AFieldMissingFromAnOlderFileKeepsItsDefault()
    {
        File.WriteAllText(Path.Combine(_home, ToolSettings.FileName), """{ "language": "l_ko" }""");

        var settings = ToolSettings.Load(_home);
        Assert.Equal("l_ko", settings.Language);
        Assert.True(settings.ReplaceableOnly);
        Assert.False(settings.OpaqueTextures);
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
