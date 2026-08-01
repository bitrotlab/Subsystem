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

    // Second hook, for the per-commander section of patch.json. The per-commander entity types
    // do not exist until Sim's constructor has run, so this has to be applied later than the
    // first hook: inside the OnSceneLoadComplete coroutine, right after SimController.PostLoadInit.
    private const string BuffAnchorMethodName = "PostLoadInit";
    private const string BuffLoaderTypeName = "Subsystem.CommanderBuffLoader";
    private const string BuffLoaderMethodName = "ApplyCommanderBuffs";

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

        var hooks = FindHooks(target, managed);

        if (hooks.All && !force)
        {
            Info("BBI.Unity.Game.dll already contains both Subsystem hooks. Nothing to do.");
            Info("(Use --force to re-apply, or --restore to remove them.)");
            return 0;
        }

        if (hooks.Any && !hooks.All)
            Info("An older Subsystem hook is present; re-patching to add the commander-buff hook.");

        var backup = target + BackupSuffix;

        // Always patch a pristine assembly: if a backup exists it is the unpatched original,
        // so re-patching after a game update means starting from the *new* file, not the backup.
        string source;
        if (hooks.Any)
        {
            if (!File.Exists(backup))
                throw new InvalidOperationException(
                    "BBI.Unity.Game.dll is already patched but no backup exists. "
                    + "Verify your game files through Steam, then run this tool again.");
            source = backup;
            Info("Re-applying hooks from the pristine backup.");
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

        if (!FindHooks(target, managed).All)
            throw new InvalidOperationException("Post-patch verification failed — the hooks are not present.");

        Info("Verified: both Subsystem hooks are present.");
        Info("");
        Info("Put your patch.json in the Data folder (one level up from Managed) and start a match.");
        Info("Subsystem.log will appear next to it.");
        return 0;
    }

    private static void InjectHook(AssemblyDefinition game, AssemblyDefinition subsystem)
    {
        var hookType = game.MainModule.GetType(HookTypeName)
            ?? throw new InvalidOperationException($"{HookTypeName} not found — is this really BBI.Unity.Game.dll?");

        var entityTypesField = hookType.Fields.FirstOrDefault(f => f.Name == EntityTypesFieldName && f.IsStatic)
            ?? throw new InvalidOperationException($"static field {EntityTypesFieldName} not found on {HookTypeName}.");

        var resetEntityManager = hookType.Methods.FirstOrDefault(m => m.Name == HookMethodName && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException(
                $"{HookTypeName}::{HookMethodName}() not found. The game's code changed shape; "
                + "the hook site needs to be re-identified.");

        if (!resetEntityManager.HasBody)
            throw new InvalidOperationException($"{HookMethodName} has no body.");

        // The original 0.4.0 patch appended the call after ResetEntityManager rebuilds the
        // collection via InitializeEntityManager, so anchor on that call rather than on a
        // raw instruction offset.
        InjectLoaderCall(game, subsystem, resetEntityManager, AnchorMethodName,
                         entityTypesField, LoaderTypeName, LoaderMethodName);

        InjectLoaderCall(game, subsystem, FindBuffHookMethod(hookType), BuffAnchorMethodName,
                         entityTypesField, BuffLoaderTypeName, BuffLoaderMethodName);
    }

    /// <summary>
    /// Inserts <c>new Loader().Method(ShipbreakersMain.sEntityTypes)</c> immediately after the
    /// last call to <paramref name="anchorMethodName"/> in <paramref name="hookMethod"/>.
    /// </summary>
    private static void InjectLoaderCall(
        AssemblyDefinition game, AssemblyDefinition subsystem, MethodDefinition hookMethod,
        string anchorMethodName, FieldDefinition entityTypesField,
        string loaderTypeName, string loaderMethodName)
    {
        var loaderType = subsystem.MainModule.GetType(loaderTypeName)
            ?? throw new InvalidOperationException($"{loaderTypeName} not found in {ModAssembly}.");

        var loaderCtor = loaderType.Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException($"{loaderTypeName} has no parameterless constructor.");

        var loaderMethod = loaderType.Methods.FirstOrDefault(
                m => m.Name == loaderMethodName
                     && m.Parameters.Count == 1
                     && m.Parameters[0].ParameterType.FullName == entityTypesField.FieldType.FullName)
            ?? throw new InvalidOperationException(
                $"{loaderTypeName}::{loaderMethodName}({entityTypesField.FieldType.FullName}) not found.");

        var anchor = hookMethod.Body.Instructions.LastOrDefault(
                i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                     && i.Operand is MethodReference mr
                     && mr.Name == anchorMethodName)
            ?? throw new InvalidOperationException(
                $"no call to {anchorMethodName} inside {hookMethod.DeclaringType.Name}::{hookMethod.Name} "
                + "— hook site needs re-identifying.");

        var il = hookMethod.Body.GetILProcessor();

        var ctorRef = game.MainModule.ImportReference(loaderCtor);
        var loadRef = game.MainModule.ImportReference(loaderMethod);

        // newobj  Subsystem.<Loader>::.ctor()
        // ldsfld  ShipbreakersMain::sEntityTypes
        // call    Subsystem.<Loader>::<Method>(EntityTypeCollection)
        var call = Instruction.Create(OpCodes.Call, loadRef);
        var load = Instruction.Create(OpCodes.Ldsfld, entityTypesField);
        var make = Instruction.Create(OpCodes.Newobj, ctorRef);

        il.InsertAfter(anchor, call);
        il.InsertAfter(anchor, load);
        il.InsertAfter(anchor, make);

        Info($"Injected hook into {hookMethod.DeclaringType.Name}::{hookMethod.Name} after the {anchorMethodName} call.");
    }

    /// <summary>
    /// The compiler-generated state machine for ShipbreakersMain.OnSceneLoadComplete, found by
    /// what it does rather than by its name: the <c>&lt;OnSceneLoadComplete&gt;d__29</c> suffix
    /// is a compiler counter that moves whenever the class gains or loses an iterator.
    /// </summary>
    private static MethodDefinition FindBuffHookMethod(TypeDefinition hookType)
    {
        var candidates = hookType.NestedTypes
            .SelectMany(t => t.Methods)
            .Where(m => m.Name == "MoveNext" && m.HasBody && CallsMethodNamed(m, BuffAnchorMethodName))
            .ToList();

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException(
                $"no state machine under {HookTypeName} calls {BuffAnchorMethodName}. The game's code "
                + "changed shape; the commander-buff hook site needs to be re-identified."),
            _ => throw new InvalidOperationException(
                $"{candidates.Count} state machines under {HookTypeName} call {BuffAnchorMethodName} "
                + $"({string.Join(", ", candidates.Select(c => c.DeclaringType.Name))}); "
                + "the commander-buff hook site is ambiguous and needs to be re-identified."),
        };
    }

    private static bool CallsMethodNamed(MethodDefinition method, string name) =>
        method.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == name);

    // --------------------------------------------------------------- verify

    private static int Verify(string managed, string target)
    {
        var hooks = FindHooks(target, managed);
        var patched = hooks.All;
        var mod = File.Exists(Path.Combine(managed, ModAssembly));
        var backup = File.Exists(target + BackupSuffix);
        var stale = CountDanglingReferences(target, managed);

        Info($"attributes hook         : {(hooks.Attributes ? "yes" : "no")}");
        Info($"commander-buff hook     : {(hooks.CommanderBuffs ? "yes" : "no")}");
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

    private readonly record struct Hooks(bool Attributes, bool CommanderBuffs)
    {
        public bool All => Attributes && CommanderBuffs;
        public bool Any => Attributes || CommanderBuffs;
    }

    /// <summary>
    /// Which Subsystem hooks the assembly already contains. Reported separately so that an
    /// install patched by an older SubsystemPatcher — which only injected the attributes hook —
    /// is recognised as incomplete rather than as up to date.
    /// </summary>
    private static Hooks FindHooks(string target, string managed)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.RemoveSearchDirectory(".");
        resolver.AddSearchDirectory(managed);
        using var asm = AssemblyDefinition.ReadAssembly(
            target, new ReaderParameters { AssemblyResolver = resolver });

        var type = asm.MainModule.GetType(HookTypeName);
        if (type == null) return new Hooks(false, false);

        var resetEntityManager = type.Methods.FirstOrDefault(m => m.Name == HookMethodName && m.Parameters.Count == 0);

        var attributes = resetEntityManager is { HasBody: true }
                         && CallsLoader(resetEntityManager, LoaderTypeName, LoaderMethodName);

        var commanderBuffs = type.NestedTypes
            .SelectMany(t => t.Methods)
            .Any(m => m.Name == "MoveNext" && m.HasBody
                      && CallsLoader(m, BuffLoaderTypeName, BuffLoaderMethodName));

        return new Hooks(attributes, commanderBuffs);
    }

    private static bool CallsLoader(MethodDefinition method, string typeFullName, string methodName) =>
        method.Body.Instructions.Any(
            i => i.Operand is MethodReference mr
                 && mr.Name == methodName
                 && mr.DeclaringType?.FullName == typeFullName);

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
