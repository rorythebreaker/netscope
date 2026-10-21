# Reference set

NetScope is accepted against twenty assemblies typical of real projects (TZ-NetScope-1 §10, TZ-NetScope-3 §3).
Nineteen are built from source in [`tests/Reference`](../tests/Reference/README.md) with the SDK pinned in
`global.json`. The twentieth is the ConfuserEx fixture in `tests/Fixtures/Obfuscated`. No prebuilt binary is
downloaded, so the license and origin of every file are known.

The set is built by `dotnet build NetScope.sln` and checked by `tests/NetScope.Tests/ReferenceSetTests.cs` in the
`test` job of CI on all four platforms (Windows and Linux, x64 and ARM64). The .NET Framework assemblies (4 and 5) are
compiled from `Microsoft.NETFramework.ReferenceAssemblies`, so no .NET Framework installation is needed. The
ReadyToRun copies (18 and 19) are cross-compiled on every host.

## What is checked

| Check | Test | Reference |
| --- | --- | --- |
| Reference output of `--methods`, `--calls`, `--cfg`, `--il`, `--pseudo`, `--format json` (`--methods --calls`), and `--native` for 18 and 19 | `OutputMatchesReference` | `tests/NetScope.Tests/Golden/Reference`, regenerated with `NETSCOPE_UPDATE_GOLDEN=1` |
| Method list, and every IL instruction with its offset, for every method | `MethodsAndILMatchIldasm` | `ildasm` from the `runtime.<rid>.Microsoft.NETCore.ILDAsm` package (10.0.13) |
| Method list and method kinds | `MethodKindsMatchILSpy` | ILSpy's type system (`ICSharpCode.Decompiler`) |
| Pseudocode for at least 95% of the methods, never fewer than ILSpy decompiles alone; the fallback for the rest | `PseudocodeCoversAtLeast95Percent` | ILSpy's `CSharpDecompiler` |
| CFG of loops, `switch`, nested `try/catch/finally`, `filter` and `fault` | `CfgOf*` | Assemblies 12 and 13 |

Each assembly is analyzed with `--ref-path` set to the reference assemblies it was compiled against (the `.refs` file
next to it), so type names resolve the same way whatever framework the machine has.

## Results

| # | Assembly | Methods | Pseudocode (decompiler) | IL vs ildasm | Kinds vs ILSpy |
| --- | --- | ---: | ---: | --- | --- |
| 1 | Console application, .NET 8 | 21 | 100% | match | match |
| 2 | Class library, .NET 8 | 44 | 100% | match | match |
| 3 | .NET Standard 2.0 | 27 | 100% | match | match |
| 4 | .NET Framework 4.8 | 22 | 100% | match | match |
| 5 | .NET Framework 3.5 (`v2.0.50727`) | 35 | 100% | match | match |
| 6 | .NET Core 3.1 | 17 | 100% | match | match |
| 7 | async/await, `ValueTask` | 29 | 100% | match | match |
| 8 | Iterators, `IAsyncEnumerable` | 100 | 100% | match | match |
| 9 | Generics | 36 | 100% | match | match |
| 10 | `DllImport`, `LibraryImport`, function pointers | 21 | 100% | match | match |
| 11 | `unsafe` | 16 | 100% | match | match |
| 12 | Hand-written IL | 10 | 100% | match | match |
| 13 | Pattern matching, `switch` | 67 | 100% | match | match |
| 14 | Lambdas, closures, LINQ | 84 | 100% | match | match |
| 15 | Records, DIM, `static abstract` | 97 | 100% | match | match (after the fix below) |
| 16 | VB.NET | 49 | 100% | match | match |
| 17 | F# | 159 | 100% | match | match |
| 18 | R2R win-x64 copy of 2 | 44 | 100% | match | match |
| 19 | R2R linux-arm64 copy of 2 | 44 | 100% | match | match |
| 20 | ConfuserEx | 93 | 100% | match (Linux); match with ILSpy's disassembler on Windows (see below) | match |

"Match" means no difference other than the documented ones below. The kinds compared with ILSpy are those both tools
define: `ctor`, `cctor`, `finalizer`, property and event accessors, `operator`, `conversion`, `static`/`instance`,
`abstract`, `virtual`, `override`, `sealed`, `generic`, `extension`, `async` and `iterator`. For methods NetScope
marks `lambda` or `local-function`, ILSpy must hide them in its C# output.

## Known differences

| Where | Difference | Reason |
| --- | --- | --- |
| Kinds vs ILSpy | An implicit interface implementation is `virtual, sealed` in NetScope and not virtual in ILSpy | The metadata is `virtual final newslot`. NetScope reports the metadata, ILSpy reports the C# view. The test drops `virtual` and `sealed` for this attribute combination before comparing |
| Kinds vs ILSpy | Lambdas of a closure class (`<>c`, `<>c__DisplayClass`) are not hidden by ILSpy individually | ILSpy hides the whole closure class. The test accepts a lambda when the method or one of its declaring types is hidden |
| Method list vs ILSpy | ILSpy's type definitions contain members without a metadata row | ILSpy synthesizes them (nil token). They are not methods of the file and are not compared |
| IL vs ildasm, assembly 20 | On Windows, ildasm aborts on the ConfuserEx fixture ("class … is nested in missing class …", exit code `0xC0000409`); the Linux build of the same ildasm reads it | ConfuserEx nests types in a type that does not exist, which is its protection against ildasm. Where ildasm fails, the IL is compared with ILSpy's independent disassembler (`ReflectionDisassembler`); on Linux it is compared with ildasm |
| IL vs ildasm | `endfault` is shown as `endfinally` | It is the same opcode (`0xDC`). Both tools print `endfinally` |

Found by the set and fixed: a `static virtual` interface member (`static virtual string Kind => …` in assembly 15) was
classified as `override`, because its metadata has no `newslot`. A static method overrides nothing, so NetScope now
reports `virtual` (`src/NetScope/Catalog/MethodCatalog.cs`).

## CFG notes

- Assembly 12: `fault` handlers, a filter with branches inside the filter block, `try/catch` inside a `finally`,
  `try/fault` inside a `catch`, a `switch` whose out-of-range value falls through into the first case, a loop around a
  `switch`, `jmp`, a `tail.` call, `rethrow`, and unreachable code after a handler.
- Assembly 13: `HttpStatus` is a jump table, `Keyword` is a hashed string `switch` (`ComputeStringHash`), and
  `CountTokens` is a `switch` inside a loop. Its sparse `char` cases are compiled to comparisons, not to a `switch`
  opcode, so the test checks the loop and the complexity.

## Ghidra

Assembly 19 (and the linux-x64 and osx-arm64 copies of assembly 2, built only for this) is imported into Ghidra
11.1.2 by the `ghidra` and `ghidra-python3` CI jobs with the options `export-ghidra` writes in the script header
(`-loader PeLoader -processor ...`), because Ghidra's PE loader does not recognize the OS-encoded `Machine` field on
its own. Every function from `--native` is named at its address, a second run adds nothing, and the file is not
changed. See the README section "Export to Ghidra".

## C++/CLI

A C++/CLI assembly cannot be built in CI without MSVC, so it is not part of the twenty. As before, the managed and
native parts are checked on the .NET Framework images that ship with Windows (`System.EnterpriseServices.Wrapper.dll`),
on Windows only.
