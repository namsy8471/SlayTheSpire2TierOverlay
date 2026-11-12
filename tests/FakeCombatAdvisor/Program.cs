// FAKE combat advisor helper (test stand-in, no game logic): replays a script of canned lines.
//
//   FakeCombatAdvisor [--script <file>] [anything else, e.g. the overlay's "live --save … --log …"]
//
// Script lines (UTF-8, one command per line; blank lines and lines starting with '#' are skipped):
//   print <text>     write <text> to stdout as one line
//   stderr <text>    write <text> to stderr
//   args             write the command-line arguments as one JSON line: {"fake":"args","args":[...]}
//   sleep <ms>       wait
//   exit <code>      exit with that code
//   hang             wait forever (until killed)
//   loop             start the script again from the top
// The end of the script exits with code 0. Without --script: print an idle line, then hang.
using System.Globalization;
using System.Text;
using System.Text.Json;

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };

var scriptIndex = Array.IndexOf(args, "--script");
string[] script = scriptIndex >= 0 && scriptIndex + 1 < args.Length
    ? File.ReadAllLines(args[scriptIndex + 1], Encoding.UTF8)
    : ["print {\"v\":1,\"state\":\"idle\"}", "hang"];

for (var i = 0; i < script.Length; i++)
{
    var line = script[i].TrimEnd();
    if (line.Length == 0 || line.StartsWith('#')) continue;
    var space = line.IndexOf(' ');
    var command = space < 0 ? line : line[..space];
    var rest = space < 0 ? "" : line[(space + 1)..];
    switch (command)
    {
        case "print":
            stdout.WriteLine(rest);
            break;
        case "stderr":
            stderr.WriteLine(rest);
            break;
        case "args":
            stdout.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["fake"] = "args", ["args"] = args }));
            break;
        case "sleep":
            Thread.Sleep(int.Parse(rest, CultureInfo.InvariantCulture));
            break;
        case "exit":
            return int.Parse(rest, CultureInfo.InvariantCulture);
        case "hang":
            Thread.Sleep(Timeout.Infinite);
            break;
        case "loop":
            i = -1;
            break;
        default:
            stderr.WriteLine("fake advisor: unknown script command " + command);
            break;
    }
}
return 0;
