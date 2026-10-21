# Reference set

Twenty assemblies typical of real projects (TZ-NetScope-3 §3). Nineteen are built here from source with the SDK
pinned in `global.json`; the twentieth is the ConfuserEx fixture in `tests/Fixtures/Obfuscated`. No prebuilt binary
is downloaded.

| # | Assembly | What it checks |
| --- | --- | --- |
| 1 | `Ref01.ConsoleApp` | Entry point, apphost |
| 2 | `Ref02.Library` | Base case: class library, .NET 8 |
| 3 | `Ref03.NetStandard` | References to `netstandard` |
| 4 | `Ref04.NetFx48` | CLR 4.0, `mscorlib` (built from `Microsoft.NETFramework.ReferenceAssemblies`) |
| 5 | `Ref05.NetFx35` | CLR 2.0, metadata version `v2.0.50727` |
| 6 | `Ref06.NetCore31` | .NET Core 3.1, the lowest supported Core |
| 7 | `Ref07.Async` | async/await, `ValueTask`, `ConfigureAwait`, state machines |
| 8 | `Ref08.Iterators` | Iterators and `IAsyncEnumerable` |
| 9 | `Ref09.Generics` | Constraints, variance, generic methods, `MethodSpec`/`TypeSpec` |
| 10 | `Ref10.Interop` | `DllImport`, `LibraryImport`, function pointers, `calli` |
| 11 | `Ref11.Unsafe` | Pointers, `fixed`, `stackalloc`, pinned locals |
| 12 | `Ref12.HandIL` | Hand-written IL (`ilasm`): `fault`, `filter`, `jmp`, `tail.`, `switch` without default |
| 13 | `Ref13.Switches` | Pattern matching, string `switch` (hashed), large `switch` (jump table) |
| 14 | `Ref14.Lambdas` | Lambdas, closures, local functions, LINQ |
| 15 | `Ref15.Modern` | Records, `init`, default interface methods, `static abstract` |
| 16 | `Ref16.VbLibrary` | VB.NET, `Microsoft.VisualBasic` |
| 17 | `Ref17.FSharpLibrary` | F#: tail calls, closures, `FSharp.Core` |
| 18 | `Ref02.Library` R2R, win-x64 | ReadyToRun code, funclets, instantiations |
| 19 | `Ref02.Library` R2R, linux-arm64 | R2R `Machine` encoding for Linux, ARM64 |
| 20 | `tests/Fixtures/Obfuscated/confuserex-normal.exe` | Obfuscation detector, robustness |

Outputs: `tests/Reference/bin/<Configuration>/<Project>/<Project>.dll`, and the ReadyToRun copies in
`tests/Reference/bin/r2r/<Configuration>/<rid>/Ref02.Library.dll` (published by `NetScope.Tests.csproj`).
Each assembly has a `<Project>.refs` file next to it: the directories of the reference assemblies it was compiled
against. The tests pass them as `--ref-path`, so names resolve the same way on every platform.

The checks and the known differences from ildasm and ILSpy are described in
[docs/reference-set.md](../../docs/reference-set.md).
