using PckTool;

// Usage:
//   PckTool list [filter]                     print matching paths and a count
//   PckTool extract <glob> <outDir>           copy matching files out of the pack as-is
//   PckTool extract-images <glob> <outDir>    convert Godot textures (.ctex) to .png/.webp
// Options:
//   --pck <path>   pack to read (default: the Steam install path below)

const string DefaultPck = @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.pck";

var argList = args.ToList();
string pckPath = DefaultPck;
int pckIndex = argList.IndexOf("--pck");
if (pckIndex >= 0 && pckIndex + 1 < argList.Count)
{
    pckPath = argList[pckIndex + 1];
    argList.RemoveRange(pckIndex, 2);
}

if (argList.Count == 0)
{
    PrintUsage();
    return 1;
}

using var pck = new PckReader(pckPath);
Console.Error.WriteLine($"Opened {pckPath}: Godot {pck.GodotVersion}, {pck.Entries.Count} files");

switch (argList[0])
{
    case "list":
        return Commands.List(pck, argList.Count > 1 ? argList[1] : null);
    case "extract" when argList.Count == 3:
        return Commands.Extract(pck, argList[1], argList[2]);
    case "extract-images" when argList.Count == 3:
        return Commands.ExtractImages(pck, argList[1], argList[2]);
    default:
        PrintUsage();
        return 1;
}

static void PrintUsage() => Console.Error.WriteLine("""
    Usage:
      PckTool list [filter]                   filter: substring, or glob with * ** ?
      PckTool extract <glob> <outDir>
      PckTool extract-images <glob> <outDir>  glob matches source paths like images/**/*.png
    Options:
      --pck <path>                            default: Steam install of Slay the Spire 2
    """);
