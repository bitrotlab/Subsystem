using System.Text.Json;
using GameLocator = SubsystemPatcher.GameLocator;

namespace SubsystemLint;

/// <summary>
/// Checks a patch.json against the shapes Subsystem actually deserializes, and (optionally)
/// against the entity/component names the installed game actually loaded.
///
/// Subsystem swallows almost everything that goes wrong: an unknown key is silently dropped by
/// LitJson, and a name that does not exist is a line in Subsystem.log you have to go looking
/// for. This reports both up front.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e) { Console.Error.WriteLine("error: " + e.Message); return 1; }
    }

    private static int Run(string[] args)
    {
        string? patchPath = null, managed = null, entities = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--managed" or "-m": managed = Next(args, ref i, "--managed"); break;
                case "--entities" or "-e": entities = Next(args, ref i, "--entities"); break;
                case "--help" or "-h": Usage(); return 0;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException($"unknown argument: {args[i]}");
                    patchPath = args[i];
                    break;
            }
        }

        managed ??= GameLocator.FindManagedDirectory()
                    ?? throw new InvalidOperationException(
                        "Could not find Data/Managed automatically; pass --managed <dir>.");
        managed = Path.GetFullPath(managed);

        var dataDir = Path.GetDirectoryName(managed)!;
        patchPath ??= Path.Combine(dataDir, "patch.json");
        entities ??= File.Exists(Path.Combine(dataDir, "Subsystem.entities.log"))
            ? Path.Combine(dataDir, "Subsystem.entities.log")
            : null;

        if (!File.Exists(patchPath)) throw new FileNotFoundException($"patch file not found: {patchPath}");

        var subsystemDll = Path.Combine(managed, "Subsystem.dll");
        if (!File.Exists(subsystemDll))
            throw new FileNotFoundException(
                $"Subsystem.dll not found in {managed} — build it and copy it there first.");

        Console.WriteLine($"patch    : {patchPath}");
        Console.WriteLine($"schema   : {subsystemDll}");
        Console.WriteLine($"names    : {entities ?? "(none — run the game once to produce Subsystem.entities.log)"}");
        Console.WriteLine();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(patchPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            Console.Error.WriteLine($"FATAL: patch.json is not valid JSON — {e.Message}");
            return 1;
        }

        var schema = new Schema(subsystemDll, managed);
        var names = entities != null ? EntityNames.Load(entities) : null;
        var report = new Report();

        schema.Validate(doc.RootElement, "Subsystem.Patch.AttributesPatch", "", report, names);

        report.Print();
        return report.Errors > 0 ? 2 : 0;
    }

    private static string Next(string[] args, ref int i, string name)
    {
        if (++i >= args.Length) throw new ArgumentException($"{name} needs a value");
        return args[i];
    }

    private static void Usage() => Console.WriteLine("""
        SubsystemLint — validate a Deserts of Kharak patch.json before you launch the game.

          SubsystemLint [patch.json] [options]

          --managed,  -m <dir>   Path to Data/Managed (auto-detected if omitted)
          --entities, -e <file>  Subsystem.entities.log, to check entity and component names
          --help,     -h         This text

        With no arguments it checks Data/patch.json next to the detected install, using
        Data/Subsystem.entities.log for name checking if the game has written one.

        Exit codes: 0 clean, 2 problems found, 1 could not run.
        """);
}
