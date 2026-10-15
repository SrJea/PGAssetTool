using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Assets;

/// Opens the game's serialized files.
///
/// AssetBundles carry their own TypeTree, so they need nothing extra. The files under *_Data
/// (resources.assets, globalgamemanagers, ...) are built without one, so reading them requires
/// Unity's engine class database — that is what classdata.tpk supplies. It describes stock engine
/// classes only and is unrelated to the game's own script types.
public sealed class AssetsContext : IDisposable
{
    private readonly AssetsManager _manager = new();
    private readonly string? _classPackagePath;
    private bool _classDatabaseLoaded;

    public AssetsContext(string? classPackagePath = null)
    {
        // Both off unless asked for. Without the lookup every GetAssetInfo is a walk along the file's
        // whole table, and without the cache every read of an object rebuilds its class's template
        // from the type tree first.
        _manager.UseQuickLookup = true;
        _manager.UseTemplateFieldCache = true;

        _classPackagePath = classPackagePath ?? ClassPackage.Locate();
        if (_classPackagePath is not null)
            _manager.LoadClassPackage(_classPackagePath);
    }

    public bool HasClassDatabase => _classPackagePath is not null;

    public BundleFileInstance OpenBundle(string path) => _manager.LoadBundleFile(path, true);

    /// A bundle already in memory, filed under the path it came from.
    public BundleFileInstance OpenBundle(Stream stream, string path) => _manager.LoadBundleFile(stream, path, false);

    public AssetsFileInstance OpenBundleEntry(BundleFileInstance bundle, int index)
        => _manager.LoadAssetsFileFromBundle(bundle, index, false);

    /// Serialized files outside the bundle cache. Requires the class database.
    public AssetsFileInstance OpenSerializedFile(string path)
    {
        if (_classPackagePath is null)
            throw new InvalidOperationException(
                "Reading files under *_Data requires classdata.tpk. Place it next to the executable "
                + "or pass its path explicitly.");

        var inst = _manager.LoadAssetsFile(path, false);
        if (!_classDatabaseLoaded)
        {
            _manager.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
            _classDatabaseLoaded = true;
        }
        return inst;
    }

    public AssetTypeValueField? Deserialize(AssetsFileInstance file, AssetFileInfo info)
        => _manager.GetBaseField(file, info);

    /// The shape an object of this class has, without reading one.
    public AssetTypeTemplateField GetTemplate(AssetsFileInstance file, AssetFileInfo info)
        => _manager.GetTemplateBaseField(file, info);

    /// What an object is called, as `AssetNaming.NameOf` would read it off the whole object.
    ///
    /// Most classes keep `m_Name` as their first field, and for those the name is read straight off
    /// the file. Finding a texture by name meant deserializing every object in its bundle — pixels,
    /// vertex buffers and all — and that was most of what extracting a weapon cost: #16 allocated
    /// 17GB to write fifteen files. A Shader, which keeps its name elsewhere, and anything whose
    /// name sits behind a field of a size only reading it would tell, are read whole, as before.
    public string NameOf(AssetsFileInstance file, AssetFileInfo info)
    {
        var cls = (AssetClassID)info.TypeId;
        if (cls != AssetClassID.Shader && _manager.GetTemplateBaseField(file, info) is { } template)
        {
            lock (file.LockReader)
            {
                var reader = file.file.Reader;
                var start = info.GetAbsoluteByteOffset(file.file);
                if (NameOffset(template, reader, start, info.ByteSize) is { } at)
                {
                    reader.Position = start + at;
                    var length = reader.ReadInt32();
                    if (length >= 0 && at + 4 + length <= info.ByteSize)
                        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
                }
            }
        }

        return AssetNaming.NameOf(Deserialize(file, info), cls);
    }

    /// Where `m_Name` starts, when everything ahead of it can be stepped over without reading it: at
    /// the very top for most classes, behind two pointers and a flag for a MonoBehaviour, and behind
    /// a list of component pointers for a GameObject — an array of fixed-size elements is its count
    /// times their size. Null when something ahead of it is a string or anything else of a size only
    /// reading it would tell, or when there is no `m_Name` at the top at all.
    private static long? NameOffset(AssetTypeTemplateField template, AssetsFileReader reader, long start, long size)
    {
        long at = 0;
        foreach (var child in template.Children)
        {
            if (child.Name == "m_Name")
                return child.ValueType == AssetValueType.String ? at : null;
            if (!Advance(child, reader, start, size, ref at)) return null;
        }
        return null;
    }

    private static bool Advance(AssetTypeTemplateField field, AssetsFileReader? reader, long start, long size, ref long at)
    {
        if (field.IsArray)
        {
            // An array's two children are its count and one element. Only the count is read.
            if (reader is null || field.Children.Count != 2) return false;
            long element = 0;
            if (!Advance(field.Children[1], null, 0, 0, ref element) || element == 0 || element % 4 != 0) return false;
            if (at + 4 > size) return false;

            reader.Position = start + at;
            var count = reader.ReadInt32();
            if (count < 0 || at + 4 + count * element > size) return false;
            at += 4 + count * element;
            if (field.IsAligned) at = (at + 3) & ~3L;
            return true;
        }

        // Inside an element, alignment is relative to where that element starts, which only agrees
        // with where it lands in the file when every element is a whole number of words.
        switch (field.ValueType)
        {
            case AssetValueType.Bool or AssetValueType.Int8 or AssetValueType.UInt8: at += 1; break;
            case AssetValueType.Int16 or AssetValueType.UInt16: at += 2; break;
            case AssetValueType.Int32 or AssetValueType.UInt32 or AssetValueType.Float: at += 4; break;
            case AssetValueType.Int64 or AssetValueType.UInt64 or AssetValueType.Double: at += 8; break;
            case AssetValueType.None:
                foreach (var child in field.Children)
                    if (!Advance(child, reader, start, size, ref at)) return false;
                break;
            default: return false;
        }

        if (field.IsAligned) at = (at + 3) & ~3L;
        return true;
    }

    /// Non-scene entries in a bundle. The .resS/.resource siblings hold raw texture and audio
    /// payloads rather than serialized objects.
    public static IEnumerable<(int Index, string Name)> SerializedEntries(BundleFileInstance bundle)
        => bundle.file.BlockAndDirInfo.DirectoryInfos
            .Select((d, i) => (Index: i, d.Name))
            .Where(e => !e.Name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                     && !e.Name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));

    /// Puts everything down.
    ///
    /// The library unloads by walking its own list of open bundles, so anything still opening one
    /// on another thread makes that walk throw. Callers are expected not to do that — the window
    /// waits for its readers before disposing — and a shutdown is no place to turn somebody else's
    /// race into a crash on the way out.
    public void Dispose()
    {
        try { _manager.UnloadAll(true); }
        catch (InvalidOperationException) { }
    }
}

public static class ClassPackage
{
    public const string FileName = "classdata.tpk";

    public static string? Locate()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate)) return candidate;
        }
        return Unpack();
    }

    /// Writes out the copy carried inside the assembly.
    ///
    /// A single-file build is one executable with no folder to keep the database beside, and asking
    /// people to fetch it separately would mean the newest weapons' icons silently fail to load.
    /// AssetsTools wants a path rather than a stream, so it goes to a fixed place in the temp
    /// directory and is reused from there.
    private static string? Unpack()
    {
        using var embedded = typeof(ClassPackage).Assembly.GetManifestResourceStream(FileName);
        if (embedded is null) return null;

        var path = Path.Combine(Path.GetTempPath(), "PGAssetTool", FileName);
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > 0 } existing && existing.Length == embedded.Length)
                return path;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            embedded.CopyTo(file);
            return path;
        }
        catch (IOException)
        {
            // Another process writing the same file, or nowhere to write at all. Reading .assets is
            // optional everywhere it is used, so this degrades rather than fails.
            return File.Exists(path) ? path : null;
        }
    }
}

public static class GameVersion
{
    /// PlayerSettings.bundleVersion, e.g. "26.11.0". The game never downgrades, so a change in this
    /// value is a reliable update signal.
    /// `PlayerSettings` is read as far as this field and no further, and the field after it is
    /// looked at as a string.
    ///
    /// The class is one the stock engine class database does not describe as a built game writes
    /// it. Read whole against the game on Unity 6000.3.14f1, it runs off the end of its 908 bytes
    /// at field 150 of 175 — so nothing in `globalgamemanagers` could be read at all, though 400 of
    /// 400 objects in `globalgamemanagers.assets` can. The fields up to `bundleVersion`, field 136,
    /// do line up: `companyName` and `productName` come out right.
    ///
    /// The build carries one string there that the database does not list, so what the database
    /// calls `bundleVersion` is the field before the real one — '1.0' where the game shows
    /// '26.13.0'. The extra string is taken only when it reads as a version and the one the
    /// template named does not look later than it, which is what makes this safe where the two do
    /// agree: the next field is then `preloadedAssets`, a vector whose count reads as a string
    /// length of nothing plausible.
    private const string VersionField = "bundleVersion";

    public static string Read(AssetsContext context, GameInstallation game)
    {
        var ggm = context.OpenSerializedFile(game.GlobalGameManagersPath);
        var info = ggm.file.AssetInfos.First(a => a.TypeId == (int)AssetClassID.PlayerSettings);

        var template = context.GetTemplate(ggm, info);
        var at = template.Children.FindIndex(c => c.Name == VersionField);
        if (at < 0) return context.Deserialize(ggm, info)![VersionField].AsString;

        template.Children = [.. template.Children.Take(at + 1)];

        var reader = ggm.file.Reader;
        reader.Position = info.GetAbsoluteByteOffset(ggm.file);
        var named = template.MakeValue(reader)[VersionField].AsString;

        return NextVersion(reader, info.GetAbsoluteByteOffset(ggm.file) + info.ByteSize) ?? named;
    }

    /// The next length-prefixed string, when it is a run of digits and dots and nothing longer than
    /// a version is. Null for anything else, which is how a field that is not a string says so.
    private static string? NextVersion(AssetsFileReader reader, long end)
    {
        if (reader.Position + 4 > end) return null;

        var length = reader.ReadInt32();
        if (length is < 3 or > 32 || reader.Position + length > end) return null;

        var text = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        return text.Length > 0 && text.All(c => char.IsAsciiDigit(c) || c == '.')
            && char.IsAsciiDigit(text[0]) && text.Contains('.')
                ? text
                : null;
    }

    /// The version if it can be read, and null if it cannot.
    ///
    /// Every caller asked the first of the two questions this answers — whether the engine class
    /// database is there to read the file with — and wrote the same line to do it. None asked the
    /// second: the file being read is the game's own, so it can be missing or damaged like anything
    /// else of theirs, and it is read while building a pack and while installing one, neither of
    /// which is worth stopping over a version string.
    public static string? Of(AssetsContext context, GameInstallation game)
    {
        if (!context.HasClassDatabase) return null;

        try
        {
            return Read(context, game);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    /// The version, or the word this tool writes into a pack when it does not know.
    public const string Unknown = "unknown";
}
