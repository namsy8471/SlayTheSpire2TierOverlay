using System.IO;
using System.Text;

namespace Sts2Overlay.App.Integration.CombatAdvice;

/// <summary>
/// How to start the combat advisor helper: an exe plus optional leading arguments (e.g. <c>dotnet.exe</c> + a dll). The
/// overlay appends <c>live --save &lt;path&gt; --log &lt;path&gt; --exit-on-stdin-eof</c> (<see cref="LiveArgs"/>,
/// <see cref="CommandArgs"/>).
/// </summary>
/// <param name="FileName">Full path of the exe to start.</param>
/// <param name="LeadingArgs">Arguments in front of <c>live</c> (from the <c>combatAdvisorCommand</c> setting).</param>
/// <param name="Source">Where it came from, for the log: "setting" or "default".</param>
public sealed record AdvisorCommand(string FileName, IReadOnlyList<string> LeadingArgs, string Source)
{
    /// <summary>The default place, relative to the overlay exe's folder.</summary>
    public static readonly string DefaultRelativePath = Path.Combine("advisor", "Sts2CombatAdvisor.exe");

    /// <summary>The flag that asks the helper to exit when its stdin closes (<see cref="AdvisorProcessHost"/> keeps it open).</summary>
    public const string ExitOnStdinEof = "--exit-on-stdin-eof";

    /// <summary>
    /// The protocol's arguments for following the live game (docs/COMBAT_ADVISOR.md). <see cref="ExitOnStdinEof"/>: the
    /// overlay holds the helper's stdin open for its whole life, so when the overlay goes away in any way the pipe closes and
    /// the helper ends itself (a second safety net beside the kill-on-close job).
    /// </summary>
    public static IReadOnlyList<string> LiveArgs(string savePath, string logPath)
        => ["live", "--save", savePath, "--log", logPath, ExitOnStdinEof];

    /// <summary>The whole argument list of the process: <see cref="LeadingArgs"/>, then <paramref name="args"/>.</summary>
    public IReadOnlyList<string> CommandArgs(IReadOnlyList<string> args) => [.. LeadingArgs, .. args];

    /// <summary>
    /// Finds the helper, in order: (1) <paramref name="setting"/> (<c>combatAdvisorCommand</c>: a full exe path, optionally
    /// quoted, plus extra leading args), (2) <see cref="DefaultRelativePath"/> next to the overlay exe. Null when neither
    /// exists: the feature is off. <paramref name="warning"/> says why a setting was not used (logged once by the caller).
    /// </summary>
    public static AdvisorCommand? Locate(string? setting, string baseDir, out string? warning, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        warning = null;
        if (!string.IsNullOrWhiteSpace(setting))
        {
            var parts = SplitCommandLine(setting);
            if (parts.Count == 0)
            {
                warning = "combatAdvisorCommand is empty";
            }
            else if (!Path.IsPathFullyQualified(parts[0]))
            {
                warning = "combatAdvisorCommand must start with a full path to an exe";
            }
            else if (!fileExists(parts[0]))
            {
                warning = "combatAdvisorCommand points to a file that does not exist";
            }
            else
            {
                return new AdvisorCommand(parts[0], parts.Skip(1).ToArray(), "setting");
            }
        }
        var fallback = Path.Combine(baseDir, DefaultRelativePath);
        return fileExists(fallback) ? new AdvisorCommand(fallback, [], "default") : null;
    }

    /// <summary>
    /// Splits a command line into arguments: whitespace separates them, double quotes group (and are removed), and
    /// <c>""</c> inside quotes is a literal quote. Backslashes are plain characters, so Windows paths need no escaping
    /// (<c>"C:\Program Files\dotnet\dotnet.exe" C:\x\Helper.dll</c>).
    /// </summary>
    public static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArg = false; // "" is an (empty) argument
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                hasArg = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasArg) args.Add(current.ToString());
                current.Clear();
                hasArg = false;
            }
            else
            {
                current.Append(c);
                hasArg = true;
            }
        }
        if (hasArg) args.Add(current.ToString());
        return args;
    }
}
