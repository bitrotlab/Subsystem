using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SubsystemPatcher;

/// <summary>
/// Injects the Subsystem hook into the game's own BBI.Unity.Game.dll.
///
/// Subsystem 0.4.0 shipped a pre-patched copy of BBI.Unity.Game.dll built against the
/// November 2018 game. Dropping that file into a current install replaces the entire game
/// assembly with an eight-year-old one, which is why the game dies on the way from the
/// menu into a match. The hook itself is three IL instructions; this tool applies those
/// three instructions to whatever BBI.Unity.Game.dll the player actually has.
/// </summary>
internal static class Program
{
    private const string TargetAssembly = "BBI.Unity.Game.dll";
    private const string ModAssembly = "Subsystem.dll";
    private const string BackupSuffix = ".subsystem-backup";

    private const string HookTypeName = "BBI.Unity.Game.ShipbreakersMain";
    private const string HookMethodName = "ResetEntityManager";
    private const string AnchorMethodName = "InitializeEntityManager";
    private const string EntityTypesFieldName = "sEntityTypes";

    private const string LoaderTypeName = "Subsystem.AttributeLoader";
    private const string LoaderMethodName = "LoadAttributes";

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception e)
        {
            Error(e.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string? managed = null;
        var mode = Mode.Patch;
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--managed" or "-m":
                    if (++i >= args.Length) throw new ArgumentException("--managed needs a directory");
                    managed = args[i];
                    break;
                case "--verify" or "-v": mode = Mode.Verify; break;
                case "--restore" or "-r": mode = Mode.Restore; break;
                case "--force" or "-f": force = true; break;
                case "--help" or "-h": Usage(); return 0;
                default: throw new ArgumentException($"unknown argument: {args[i]}");
            }
        }

        managed ??= GameLocator.FindManagedDirectory()
                    ?? throw new InvalidOperationException(
                        "Could not find the game's Data/Managed folder automatically. "
                        + "Pass it explicitly:  --managed \"<...>/Deserts of Kharak/Data/Managed\"");

        managed = Path.GetFullPath(managed);
        var target = Path.Combine(managed, TargetAssembly);

        if (!File.Exists(target))
            throw new FileNotFoundException($"{TargetAssembly} not found in {managed}");

        Info($"Managed folder: {managed}");

        return mode switch
        {
            Mode.Verify => Verify(managed, target),
            Mode.Restore => Restore(target),
            _ => Patch(managed, target, force),
        };
    }

    private enum Mode { Patch, Verify, Restore }

    // ---------------------------------------------------------------- patch

    private static int Patch(string managed, string target, bool force)
    {
        var mod = Path.Combine(managed, ModAssembly);
        if (!File.Exists(mod))
        {
            var bundled = FindBundledSubsystemDll();
            if (bundled == null)
                throw new FileNotFoundException(
                    $"{ModAssembly} is not in {managed}. Copy Subsystem.dll there first "
                    + "(from the 0.4.0 release zip, or from Subsystem/bin/Release after building it).");

            File.Copy(bundled, mod);
            Info($"Copied {ModAssembly} into the Managed folder.");
        }

        var stale = CountDanglingReferences(target, managed);
        if (stale > 0)
        {
            Error($"{TargetAssembly} does not match the rest of this install "
                  + $"({stale} references into the other game assemblies do not resolve).");
            Error("");
            Error("That is what the old install instructions produce: the 0.4.0 zip contains a whole");
            Error("BBI.Unity.Game.dll built against the 2018 game, and dropping it in replaces eight");
            Error("years of game code. Restore the real file first, then re-run this tool:");
            Error("");
            Error($"  - if {Path.GetFileName(target + BackupSuffix)} exists, run with --restore, or");
            Error("  - in Steam: right-click the game > Properties > Installed Files > Verify integrity.");
            return 1;
        }

        if (IsPatched(target, managed) && !force)
        {
            Info("BBI.Unity.Game.dll already contains the Subsystem hook. Nothing to do.");
            Info("(Use --force to re-apply, or --restore to remove it.)");
            return 0;
        }

        var backup = target + BackupSuffix;

        // Always patch a pristine assembly: if a backup exists it is the unpatched original,
        // so re-patching after a game update means starting from the *new* file, not the backup.
        string source;
        if (IsPatched(target, managed))
        {
            if (!File.Exists(backup))
                throw new InvalidOperationException(
                    "BBI.Unity.Game.dll is already patched but no backup exists. "
                    + "Verify your game files through Steam, then run this tool again.");
            source = backup;
            Info("Re-applying hook from the pristine backup.");
        }
        else
        {
            source = target;
            if (!File.Exists(backup))
            {
                File.Copy(target, backup);
                Info($"Backed up original to {Path.GetFileName(backup)}");
            }
            else
            {
                Info($"Backup already exists ({Path.GetFileName(backup)}); refreshing it from the current file.");
                File.Copy(target, backup, overwrite: true);
            }
        }

        var temp = target + ".tmp";

        using (var resolver = new DefaultAssemblyResolver())
        {
            resolver.RemoveSearchDirectory(".");
            resolver.AddSearchDirectory(managed);

            using var game = AssemblyDefinition.ReadAssembly(
                source, new ReaderParameters { AssemblyResolver = resolver });
            using var subsystem = AssemblyDefinition.ReadAssembly(
                Path.Combine(managed, ModAssembly), new ReaderParameters { AssemblyResolver = resolver });

            InjectHook(game, subsystem);
            game.Write(temp);
        }

        File.Move(temp, target, overwrite: true);
        Info("Patched BBI.Unity.Game.dll.");

        if (!IsPatched(target, managed))
            throw new InvalidOperationException("Post-patch verification failed — the hook is not present.");

        Info("Verified: the Subsystem hook is present.");
        Info("");
        Info("Put your patch.json in the Data folder (one level up from Managed) and start a match.");
        Info("Subsystem.log will appear next to it.");
        return 0;
    }

    private static void InjectHook(AssemblyDefinition game, AssemblyDefinition subsystem)
    {
        var hookType = game.MainModule.GetType(HookTypeName)
            ?? throw new InvalidOperationException($"{HookTypeName} not found — is this really BBI.Unity.Game.dll?");

        var hookMethod = hookType.Methods.FirstOrDefault(m => m.Name == HookMethodName && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException(
                $"{HookTypeName}::{HookMethodName}() not found. The game's code changed shape; "
                + "the hook site needs to be re-identified.");

        if (!hookMethod.HasBody)
            throw new InvalidOperationException($"{HookMethodName} has no body.");

        var entityTypesField = hookType.Fields.FirstOrDefault(f => f.Name == EntityTypesFieldName && f.IsStatic)
            ?? throw new InvalidOperationException($"static field {EntityTypesFieldName} not found on {HookTypeName}.");

        var loaderType = subsystem.MainModule.GetType(LoaderTypeName)
            ?? throw new InvalidOperationException($"{LoaderTypeName} not found in {ModAssembly}.");

        var loaderCtor = loaderType.Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException($"{LoaderTypeName} has no parameterless constructor.");

        var loadAttributes = loaderType.Methods.FirstOrDefault(
                m => m.Name == LoaderMethodName
                     && m.Parameters.Count == 1
                     && m.Parameters[0].ParameterType.FullName == entityTypesField.FieldType.FullName)
            ?? throw new InvalidOperationException(
                $"{LoaderTypeName}::{LoaderMethodName}({entityTypesField.FieldType.FullName}) not found.");

        var il = hookMethod.Body.GetILProcessor();

        // The original 0.4.0 patch appended the call after ResetEntityManager rebuilds the
        // collection via InitializeEntityManager, so anchor on that call rather than on a
        // raw instruction offset.
        var anchor = hookMethod.Body.Instructions.LastOrDefault(
                i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                     && i.Operand is MethodReference mr
                     && mr.Name == AnchorMethodName)
            ?? throw new InvalidOperationException(
                $"no call to {AnchorMethodName} inside {HookMethodName} — hook site needs re-identifying.");

        var ctorRef = game.MainModule.ImportReference(loaderCtor);
        var loadRef = game.MainModule.ImportReference(loadAttributes);

        // newobj  Subsystem.AttributeLoader::.ctor()
        // ldsfld  ShipbreakersMain::sEntityTypes
        // call    Subsystem.AttributeLoader::LoadAttributes(EntityTypeCollection)
        var call = Instruction.Create(OpCodes.Call, loadRef);
        var load = Instruction.Create(OpCodes.Ldsfld, entityTypesField);
        var make = Instruction.Create(OpCodes.Newobj, ctorRef);

        il.InsertAfter(anchor, call);
        il.InsertAfter(anchor, load);
        il.InsertAfter(anchor, make);

        Info($"Injected hook into {HookTypeName}::{HookMethodName} after the {AnchorMethodName} call.");
    }

    // --------------------------------------------------------------- verify

    private static int Verify(string managed, string target)
    {
        var patched = IsPatched(target, managed);
        var mod = File.Exists(Path.Combine(managed, ModAssembly));
        var backup = File.Exists(target + BackupSuffix);
        var stale = CountDanglingReferences(target, managed);

        Info($"{TargetAssembly} hooked : {(patched ? "yes" : "no")}");
        Info($"{ModAssembly} present   : {(mod ? "yes" : "no")}");
        Info($"backup present          : {(backup ? "yes" : "no")}");
        Info($"matches this install    : {(stale == 0 ? "yes" : $"NO — {stale} dangling references")}");

        if (stale > 0)
        {
            Error("This BBI.Unity.Game.dll was built against a different version of the game. "
                  + "Restore it (--restore, or verify the game files in Steam) before playing.");
            return 3;
        }

        if (patched && mod) { Info("Subsystem is installed."); return 0; }
        Info("Subsystem is NOT fully installed. Run this tool with no arguments to install it.");
        return 2;
    }

    /// <summary>
    /// Counts type references in <paramref name="target"/> that no longer resolve against the
    /// other assemblies in the Managed folder. A healthy BBI.Unity.Game.dll scores zero; the
    /// 2018 copy shipped in the release zip scores high, because it was built against a
    /// different BBI.Game / BBI.Core / BBI.Unity.Core.
    /// </summary>
    private static int CountDanglingReferences(string target, string managed)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.RemoveSearchDirectory(".");
        resolver.AddSearchDirectory(managed);
        using var asm = AssemblyDefinition.ReadAssembly(
            target, new ReaderParameters { AssemblyResolver = resolver });

        var dangling = 0;
        foreach (var t in asm.MainModule.GetTypeReferences())
        {
            // Only judge references into the game's own assemblies. Subsystem.dll may
            // legitimately be absent at this point, and BCL drift is not our concern.
            var scope = t.Scope?.Name ?? "";
            if (!scope.StartsWith("BBI.")) continue;
            try { if (t.Resolve() == null) dangling++; }
            catch { dangling++; }
        }
        return dangling;
    }

    private static bool IsPatched(string target, string managed)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.RemoveSearchDirectory(".");
        resolver.AddSearchDirectory(managed);
        using var asm = AssemblyDefinition.ReadAssembly(
            target, new ReaderParameters { AssemblyResolver = resolver });

        var type = asm.MainModule.GetType(HookTypeName);
        var method = type?.Methods.FirstOrDefault(m => m.Name == HookMethodName && m.Parameters.Count == 0);
        if (method is not { HasBody: true }) return false;

        return method.Body.Instructions.Any(
            i => i.Operand is MethodReference mr
                 && mr.Name == LoaderMethodName
                 && mr.DeclaringType?.FullName == LoaderTypeName);
    }

    // -------------------------------------------------------------- restore

    private static int Restore(string target)
    {
        var backup = target + BackupSuffix;
        if (!File.Exists(backup))
        {
            Error($"No backup found at {backup}. Verify the game files through Steam to restore the original.");
            return 1;
        }

        File.Copy(backup, target, overwrite: true);
        Info("Restored the original BBI.Unity.Game.dll from backup.");
        Info($"(Subsystem.dll was left in place; delete it and {Path.GetFileName(backup)} if you want a clean install.)");
        return 0;
    }

    // --------------------------------------------------------------- helpers

    private static string? FindBundledSubsystemDll()
    {
        var probes = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Subsystem", "bin", "Release"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Subsystem", "bin", "Debug"),
        };

        foreach (var p in probes)
        {
            var candidate = Path.Combine(p, ModAssembly);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static void Usage()
    {
        Console.WriteLine("""
            SubsystemPatcher — installs the Subsystem mod loader into Homeworld: Deserts of Kharak
            by patching the game's own BBI.Unity.Game.dll instead of replacing it.

              SubsystemPatcher [options]

              --managed, -m <dir>   Path to "Deserts of Kharak/Data/Managed" (auto-detected if omitted)
              --verify,  -v         Report whether the hook and Subsystem.dll are installed
              --restore, -r         Restore the original BBI.Unity.Game.dll from the backup
              --force,   -f         Re-apply the hook even if it is already present
              --help,    -h         This text

            Re-run after every game update: Steam replaces BBI.Unity.Game.dll and removes the hook.
            """);
    }

    private static void Info(string m) => Console.WriteLine(m);
    private static void Error(string m) => Console.Error.WriteLine("error: " + m);
}
