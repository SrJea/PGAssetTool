using System.Runtime.InteropServices;
using PGAssetTool.Core.Mods;

namespace PGAssetTool.Core.Settings;

/// What went wrong, written down where somebody can find it and send it.
///
/// The window says what failed in one line — the message and what it was doing — and that is
/// enough for the person in front of it and nothing like enough for anybody fixing it from a
/// report: the stack is gone, and so is which version said it. A crash says nothing at all — the
/// window simply goes. So both go into `PGAssetTool-data/logs`, beside everything else the tool
/// keeps: every reported error appended to one file, and every crash in a file of its own, which
/// is what to ask somebody to send.
public static class ErrorLog
{
    public const string FolderName = "logs";
    public const string ErrorsFileName = "errors.log";

    /// Past this the errors file is started again, keeping the one before it: a log is for the
    /// latest trouble, and one that grows without end is a file nobody will send.
    private const long MostErrors = 1024 * 1024;

    private static readonly object Writing = new();

    /// Where the data folder is. The self-test points this at its own, as it does its settings.
    public static string? Home { get; set; }

    public static string Folder => Path.Combine(Home ?? ModStore.DefaultHome(), FolderName);

    /// Records an error the window is about to report, and answers with the line to report it in.
    public static string Said(Exception error, string what)
    {
        Record(error, what);
        return $"{error.Message}  (while {what})";
    }

    /// Appends an error to the errors file. Never throws: a log that could not be written is not a
    /// reason to lose the message it was going to hold.
    public static void Record(Exception error, string what)
    {
        try
        {
            lock (Writing)
            {
                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, ErrorsFileName);
                if (new FileInfo(path) is { Exists: true, Length: > MostErrors })
                    File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);

                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {Pack.ForumPost.ToolVersion}  while {what}\n{error}\n\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// Writes a crash report of its own and answers where it went, or null if it could not be
    /// written. Called on the way out, so it keeps to what cannot fail for want of the tool.
    public static string? Crash(Exception error, string where)
    {
        try
        {
            lock (Writing)
            {
                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(path, string.Join("\n",
                    $"PGAssetTool {Pack.ForumPost.ToolVersion} stopped unexpectedly ({where}).",
                    $"When:     {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}",
                    $"Windows:  {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}",
                    $".NET:     {RuntimeInformation.FrameworkDescription}",
                    $"Memory:   {Environment.WorkingSet / (1024 * 1024)}MB in use",
                    "",
                    error.ToString(),
                    ""));
                return path;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// The newest crash report nobody has been told about yet, and marks it told. One at a time and
    /// only once: the point is that the next start says so, not that every start does.
    public static string? UntoldCrash()
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;

            var newest = Directory.EnumerateFiles(Folder, "crash-*.txt")
                .OrderByDescending(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (newest is null) return null;

            var told = Path.Combine(Folder, "told.txt");
            if (File.Exists(told) && File.ReadAllText(told).Trim() == Path.GetFileName(newest)) return null;

            File.WriteAllText(told, Path.GetFileName(newest));
            return newest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
