namespace PGAssetTool.Cli;

/// Something wrong with what was typed, as opposed to something wrong with the game or a file.
/// Said with a pointer to --help and exit code 2, the way a usage error always was here.
public sealed class CommandLineException(string message) : Exception(message);

/// What was typed, taken apart once.
///
/// It used to be read in place: the command was whatever came first, the positional arguments
/// whatever came after it up to the first option, and an option's value whatever followed its name.
/// So `extract --skin Neon 819` extracted nothing, `--game D:\PG info` was an unknown command
/// called `--game`, and a misspelt option — `--workpace` — was ignored without a word, which turned
/// a request for a workspace into an extract with no manifest in it. Every one of those is a command
/// that quietly does something other than what was asked.
///
/// So every option is one of a known set, either taking a value or not; anything else beginning
/// with `--` is refused by name; and options and positional arguments may come in any order.
public sealed class CommandLine
{
    /// The options that take a value, as `--name value` or `--name=value`.
    public static readonly IReadOnlySet<string> Valued = new HashSet<string>(StringComparer.Ordinal)
    {
        "game", "language", "out", "author", "skin", "read-memory", "from", "clip", "glb", "take", "anim",
    };

    /// The options that are on by being there.
    public static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal)
    {
        "workspace", "force", "fast", "rebuild", "low-memory", "opaque", "whole", "protect", "apply",
        "help", "version", "reset", "export", "all-skins",
    };

    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _flags;

    private CommandLine(string command, IReadOnlyList<string> positional,
        Dictionary<string, string> options, HashSet<string> flags)
        => (Command, Positional, _options, _flags) = (command, positional, options, flags);

    /// What to do: `info`, `extract`, `apply`. Empty when nothing was asked, which is a request for
    /// the help.
    public string Command { get; }

    /// The arguments after the command that are not options or their values, in order.
    public IReadOnlyList<string> Positional { get; }

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public bool Has(string flag) => _flags.Contains(flag);

    /// Whether this is a request for the help, however it was put.
    public bool WantsHelp => Command is "" or "help" || Has("help");

    /// <exception cref="CommandLineException">An option nobody knows, or one missing its value.</exception>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var loose = new List<string>();

        for (var at = 0; at < args.Count; at++)
        {
            var arg = args[at];

            // Everything after a bare `--` is an argument, so an id or a path that begins with two
            // dashes can still be named.
            if (arg == "--")
            {
                loose.AddRange(args.Skip(at + 1));
                break;
            }

            if (arg is "-h" or "-?" or "/?")
            {
                flags.Add("help");
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                loose.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;
            if (name.IndexOf('=') is var equals and > 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }

            if (Valued.Contains(name))
            {
                if (value is null)
                {
                    if (at + 1 >= args.Count || args[at + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new CommandLineException($"--{name} needs a value.");
                    value = args[++at];
                }

                options[name] = value;
            }
            else if (Flags.Contains(name))
            {
                if (value is not null)
                    throw new CommandLineException($"--{name} does not take a value.");
                flags.Add(name);
            }
            else
            {
                throw new CommandLineException($"Unknown option '--{name}'.");
            }
        }

        var command = loose.Count > 0 ? loose[0].ToLowerInvariant() : "";
        return new CommandLine(command, loose.Skip(1).ToList(), options, flags);
    }

    /// A whole number of at least zero, or the default when the option was not given.
    /// <exception cref="CommandLineException">It was given and is not one.</exception>
    public int Number(string name, int otherwise)
    {
        if (Option(name) is not { } text) return otherwise;
        return int.TryParse(text, out var number) && number >= 0
            ? number
            : throw new CommandLineException($"--{name} takes a whole number, not '{text}'.");
    }

    /// The one positional argument a command takes.
    /// <exception cref="CommandLineException">There is none, or more than one.</exception>
    public string Single(string what)
    {
        if (Positional.Count == 0)
            throw new CommandLineException($"{Command} needs {what}.");

        // Said rather than dropped. The second word was most often the rest of an id with a space
        // in it — the game has one, `avatar_ programmer` — and taking the first half of it on its
        // own answers a question nobody asked.
        if (Positional.Count > 1)
            throw new CommandLineException(
                $"{Command} takes {what}, and was given {Positional.Count} "
                + $"({string.Join(", ", Positional.Select(p => $"'{p}'"))}). Quote one that has a space in it.");

        return Positional[0];
    }

    /// The optional positional argument a command takes.
    /// <exception cref="CommandLineException">More than one was given.</exception>
    public string? Optional(string what)
        => Positional.Count switch
        {
            0 => null,
            1 => Positional[0],
            _ => throw new CommandLineException(
                $"{Command} takes at most {what}, and was given {Positional.Count}. Quote one that has a space in it."),
        };

    /// The exceptions that are about the world rather than about the tool: a file that is not
    /// there, a game that is not where it was said to be, a pack that will not read, a mod that is
    /// not installed. Reported as their message. Anything else is a fault in the tool, and gets a
    /// crash report somebody can send.
    public static bool IsExpected(Exception e)
        => e is IOException or UnauthorizedAccessException or InvalidDataException
            or KeyNotFoundException or System.Text.Json.JsonException
            or System.Security.Cryptography.CryptographicException
            || e is Core.Export.AlteredBundlesException
            // The tool's own refusals — nothing edited to pack, a path outside the workspace — are
            // this type and say what to do. So is an empty sequence in LINQ, which is a bug and
            // says nothing of use, so only the ones the tool threw itself count.
            || (e.GetType() == typeof(InvalidOperationException)
                && e.TargetSite?.DeclaringType?.Namespace?.StartsWith("PGAssetTool", StringComparison.Ordinal) == true);
}
