# Building and releasing

## Requirements

The [.NET SDK](https://dotnet.microsoft.com/download) 8.0 or newer, on Windows, Linux or macOS.
Nothing else — no Visual Studio, no Mono, no Windows.

## The three projects

| Project | Target | Builds on CI? |
| --- | --- | --- |
| `build/Subsystem.Sdk.csproj` | `net35` — the mod assembly, `Subsystem.dll` | **no** |
| `SubsystemPatcher/` | `net8.0` — installs the hook into the game | yes |
| `SubsystemLint/` | `net8.0` — validates `patch.json` | yes |

`Subsystem/Subsystem.csproj` is the original 2018 legacy-format project. It is kept for reference
and is not used by any of the above; `build/Subsystem.Sdk.csproj` compiles the same sources.

### Why the mod assembly cannot be built on CI

It references `BBI.Core`, `BBI.Game.Data` and `UnityEngine` directly from the game's
`Data/Managed/` folder. Those assemblies belong to the game and cannot be redistributed, so there
is no way to build `Subsystem.dll` on a machine without the game installed.

CI therefore builds the two tools and verifies the mod project still fails cleanly when pointed
at a missing game folder, rather than silently producing nothing.

## Building the mod

```sh
dotnet build build/Subsystem.Sdk.csproj -c Release
```

Output: `build/bin/Release/net35/Subsystem.dll`.

The project locates a Steam install automatically, including Flatpak Steam and the usual macOS
and Windows paths. Override it when that fails:

```sh
dotnet build build/Subsystem.Sdk.csproj -c Release \
  -p:ManagedDir="/path/to/Deserts of Kharak/Data/Managed"
```

If the folder cannot be found the build fails with a clear message rather than a wall of
unresolved-type errors.

`net35` is built using the `Microsoft.NETFramework.ReferenceAssemblies.net35` package, which is
what lets a modern SDK target it on any OS. The game runs Unity 5.2's Mono, so the assembly must
stay on the .NET 2.0/3.5 profile — do not raise `TargetFramework`.

## Building the tools

```sh
dotnet build SubsystemPatcher/SubsystemPatcher.csproj -c Release
dotnet build SubsystemLint/SubsystemLint.csproj -c Release
```

Both target `net8.0` with `RollForward: LatestMajor`, so they run on any newer runtime too.

`SubsystemLint` compiles `GameLocator.cs` from `SubsystemPatcher` as a linked file so install
detection lives in one place.

## Installing what you built

```sh
cp build/bin/Release/net35/Subsystem.dll "<...>/Deserts of Kharak/Data/Managed/"
dotnet run --project SubsystemPatcher -c Release
```

The patcher backs up the original `BBI.Unity.Game.dll` before writing, and `--restore` puts it
back byte-for-byte.

## Releasing

`.github/workflows/release.yml` runs on any tag matching `v*`:

```sh
git tag v0.5.0
git push origin v0.5.0
```

It publishes both tools as portable framework-dependent builds — one set of files that runs on
every OS, about 600 KB each rather than the ~67 MB a self-contained build produces per platform —
packages them with the docs, sources and `patch.example.json`, and creates a GitHub release with
that archive attached.

It can also be run from the Actions tab via `workflow_dispatch` to produce the archive as a
build artifact without tagging or publishing anything.

Note that a release **cannot** contain `Subsystem.dll` for the reason above. The archive ships
the sources and the build project; users run one `dotnet build` against their own installation.
This is also what keeps the mod working across game updates, since it is compiled against the
assemblies actually installed.

## CI

`.github/workflows/ci.yml` runs on pushes to `master` and `develop`, on pull requests, and on
demand. It builds both tools and checks the mod project's `ManagedDir` guard still fires.

## Testing a change to the patcher

There is no automated test suite. The checks worth repeating by hand, against a copy of
`Data/Managed` rather than the real install:

```sh
dotnet run --project SubsystemPatcher -c Release -- --managed /tmp/managed-copy --verify
dotnet run --project SubsystemPatcher -c Release -- --managed /tmp/managed-copy
dotnet run --project SubsystemPatcher -c Release -- --managed /tmp/managed-copy --verify
dotnet run --project SubsystemPatcher -c Release -- --managed /tmp/managed-copy --restore
```

A correct patcher will: report the hook missing, install it, report it present, refuse to patch
twice, and restore a file whose checksum matches the original exactly. It should also refuse to
run against a `BBI.Unity.Game.dll` that does not match the rest of the install — test that by
dropping the 0.4.0 release's copy in and confirming it reports dangling references.

[compatibility.md](compatibility.md) records the deeper verification that was done on the IL
rewrite, and how to repeat it if the patcher's approach ever changes.
