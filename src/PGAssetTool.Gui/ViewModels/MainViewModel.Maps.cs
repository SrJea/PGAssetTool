using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PGAssetTool.Core.Export;

namespace PGAssetTool.Gui.ViewModels;

/// The game's maps, for writing one out as a glTF. See MapExporter.
public sealed partial class MainViewModel
{
    private IReadOnlyList<MapExporter.Map> _allMaps = [];

    /// The maps whose name holds what is typed in the filter.
    public ObservableCollection<MapExporter.Map> Maps { get; } = [];

    [ObservableProperty] private string _mapFilter = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportMapCommand))]
    private MapExporter.Map? _selectedMap;

    /// What happened to the last map written, or why it was not.
    [ObservableProperty] private string _mapSays = "";

    /// Shows the window: the list read afresh, since the game may have been updated or swapped.
    public void LoadMaps()
    {
        _allMaps = _bundles is null ? [] : MapExporter.List(_bundles);
        MapSays = _bundles is null ? "The game is not open." : $"{_allMaps.Count} maps.";
        ShowMaps();
    }

    partial void OnMapFilterChanged(string value) => ShowMaps();

    private void ShowMaps()
    {
        var chosen = SelectedMap;
        Maps.Clear();
        foreach (var map in _allMaps.Where(m => m.Name.Contains(MapFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            Maps.Add(map);
        SelectedMap = Maps.Contains(chosen!) ? chosen : Maps.FirstOrDefault();
    }

    /// Where a map goes: a folder of its own beside the workspaces, so it is found where everything
    /// else this tool writes is found, and never taken for a workspace.
    public string MapsFolder => Path.Combine(WorkspaceRoot, "maps");

    private bool CanExportMap() => SelectedMap is not null;

    [RelayCommand(CanExecute = nameof(CanExportMap))]
    private async Task ExportMap()
    {
        if (SelectedMap is not { } map || _bundles is null) return;

        await RunExclusively($"writing out the map {map.Name}", async () =>
        {
            var path = Path.Combine(MapsFolder, map.Name + ".glb");
            // Made here, so what it reports arrives on the window's thread.
            IProgress<string> report = new Progress<string>(said => MapSays = said);
            MapSays = $"Writing {map.Name}…";

            var exported = await Task.Run(() => MapExporter.Export(_bundles, map, path,
                (at, of) => { if (at % 50 == 0 || at == of) report.Report($"Writing {map.Name}: {at} of {of} objects looked at…"); }));

            MapSays = $"{map.Name}: {exported.Objects} objects, {exported.Materials} materials, {exported.Textures} pictures, "
                + $"{new FileInfo(exported.Path).Length / 1048576.0:0.0} MB -> {exported.Path}. It opens in Blender (File › Import › glTF 2.0); "
                + "the sky, and what the game parks out of sight, are nodes of their own, to hide."
                + (exported.Skipped.Count > 0 ? " Left out: " + string.Join("; ", exported.Skipped.Take(3)) + "." : "");
            Status = MapSays;

            Editor.ShowFile(exported.Path);
        });
        // What stopped it, where something did: said where the person is looking, in this window.
        if (!MapSays.Contains("->", StringComparison.Ordinal)) MapSays = Status;
    }
}
