# NetScope

A command-line static analyzer for .NET assemblies. For each method it shows the method kind, the calls it makes, its control flow graph, C# pseudocode, the IL and the native code. NetScope does not run code from the assemblies it analyzes unless you pass `--allow-run`.

- Input: managed `.dll`/`.exe` files from .NET Framework 2.0–4.8, .NET Core 3.1, .NET 5–10 and .NET Standard. NetScope also reads ReadyToRun images and the managed part of C++/CLI images.
- Output: text, JSON, Graphviz DOT, Mermaid.
- Platforms: Windows and Linux, x64 and ARM64. Ships as a single self-contained executable, so .NET does not need to be installed.

## Getting it

Prebuilt single-file binaries for `win-x64`, `linux-x64`, `win-arm64` and `linux-arm64` are attached to each CI run as artifacts (Actions → latest run → Artifacts).

To build from source you need the .NET 10 SDK (the version is pinned in `global.json`). The tool itself targets .NET 8.

```
dotnet build NetScope.sln
dotnet test NetScope.sln

# self-contained single-file executable
dotnet publish src/NetScope -c Release -r win-x64     -o artifacts/win-x64
dotnet publish src/NetScope -c Release -r linux-x64   -o artifacts/linux-x64
dotnet publish src/NetScope -c Release -r win-arm64   -o artifacts/win-arm64
dotnet publish src/NetScope -c Release -r linux-arm64 -o artifacts/linux-arm64
```

## Usage

```
netscope [options] <file|mask> [<file|mask> ...]

netscope app.dll --methods --cfg
netscope bin\*.dll --calls --filter "MyApp.Auth.*" --format json --out report.json
netscope Service.exe --all --filter "*::Login" --allow-run
netscope --dir C:\App --recursive --callgraph --format dot --out graph.dot
netscope --input-list files.txt --il --out reports\
```

Example (text output, one method):

```
Method   : bool Login(string user, string pass)
Kind     : instance, public, virtual
RVA      : 0x23A0   Size: 36 bytes   Token: 0x06000022

[Calls]
  IL_000C  callvirt  MyApp.Db.Users::Find(string)                internal
  IL_0013  call      MyApp.Auth.LoginService::Hash(string)       internal
  IL_001E  call      System.String::op_Equality(string, string)  external System.Runtime

[Branches]  complexity=2  blocks=3  loops=0
  B0 IL_0000..IL_0002  -> B1 (user == null) | B2 (else)
  B1 IL_0003..IL_0004  -> exit
  B2 IL_0005..IL_0023  -> exit

[Pseudocode]
  public virtual bool Login(string user, string pass)
  {
      if (user == null)
      {
          return false;
      }
      User user2 = _users.Find(user);
      return Hash(pass) == user2.Hash;
  }
```

### Options

| Option | Description |
|---|---|
| `--dir <path>`, `--recursive` | Analyze every `.dll` and `.exe` in a directory, optionally including subdirectories |
| `--input-list <file>` | One path or mask per line; `#` starts a comment; relative paths are resolved against the list file |
| `--ref-path <path>` | Directory with dependencies (repeatable) |
| `--methods` | Methods with kind and signature. This is the default when no other group is selected |
| `--calls` | Calls made by each method, plus a "called by" list across all input files |
| `--callgraph` | Call graph between the methods of all input files |
| `--cfg` | Basic blocks, edges with conditions, loops, unreachable blocks, exits, try/catch/filter/finally/fault regions, cyclomatic complexity |
| `--pseudo` | C# pseudocode via ICSharpCode.Decompiler. If decompilation fails, falls back to three-address code built from the IL and CFG, and prints the reason |
| `--il` | ildasm-style IL listing |
| `--native` | Native code, read from a ReadyToRun image or, with `--allow-run`, taken from the JIT |
| `--allow-run` | Allow running code of the analyzed assembly in a separate helper process |
| `--all` | Enable all groups |
| `--filter <mask>` | Select methods by full name `Namespace.Type::Method`. Supports `*` and `?`, case-insensitive, repeatable |
| `--type <kind>` | Select methods by kind: `ctor`, `property`, `event`, `operator`, `async`, `iterator`, `lambda`, `local-function`, `pinvoke`, `generic`, `abstract`, `virtual`, `override`, `static`, `no-body` and more (see `--help`). Comma-separated or repeatable |
| `--format text\|json\|dot\|mermaid` | Output format |
| `--out <file\|dir>` | Write to a file, or to a directory if the path ends with `\` or `/` or already exists. In directory mode you get one report per assembly, plus `callgraph.<ext>` |
| `--raw` | Show lambdas, async methods and iterators in pseudocode in the form the compiler generated |
| `--threads <n>` | Number of files processed in parallel (default: number of cores) |
| `--decompile-timeout <s>` | Time limit for decompiling one method (default 20 s) |
| `--run-timeout <s>` | Time limit for the `--allow-run` helper process (default 30 s) |
| `--no-color`, `--quiet`, `--verbose`, `--help`, `--version` | Presentation and message level |

If you pass only `--callgraph`, NetScope prints only the graph. With `dot` and `mermaid`, it draws the CFGs of the selected methods by default. Mermaid output is Markdown with ` ```mermaid ` blocks.

### Streams and exit codes

Results go to stdout or to the `--out` target. Warnings and errors go only to stderr and name the file, the method and the IL offset. Output is UTF-8 without BOM and uses the platform's line endings. Files are listed in argument order with duplicates removed, and methods in metadata order, so the output of two runs can be compared with `diff`.

| Code | Meaning |
|---|---|
| 0 | Everything was processed |
| 1 | Some files or methods were skipped or had errors (for example, native or corrupted files in a group) |
| 2 | Invalid arguments |
| 3 | No input file could be opened |

### JSON

Every method has `assembly`, `namespace`, `type`, `name`, `signature`, `token`, `access`, `kinds[]`, `rva`, `size` and `errors[]`. Each requested group adds its own fields: `calls[]`, `callers[]`, `complexity`, `blocks[]`, `loops[]`, `regions[]`, `pseudocode`, `pseudocodeSource`, `pseudocodeError`, `il[]`, `ilInfo`, `native[]` and `nativeInfo`. Fields of groups you did not request are left out. The call graph is in the root field `callgraph`.

## Native code

| Case | Output |
|---|---|
| ReadyToRun, ordinary method | Main body plus the funclets of its `catch`/`filter`/`finally`/`fault` handlers, that is, the RUNTIME_FUNCTION entries from the entry point up to the next entry point |
| ReadyToRun, generic method or method of a generic type | Every instantiation precompiled into the image (`InstanceMethodEntryPoints`), e.g. `List<int>::Add` or `Generic<__Canon>::Map<__Canon>`. `__Canon` is the shared code used for reference types |
| C++/CLI native method | Machine code from the image. The size comes from the exception directory (`.pdata`). For leaf functions and import thunks on x86/x64, the size is estimated by a linear sweep to `ret`/`jmp`, and the output says so |
| IL method without ReadyToRun code | JIT output with `--allow-run`; otherwise the reason it is unavailable |

In text output the parts are separated by headers such as `; funclet 1 (address, size)`. In JSON each line has a `part` field, and `nativeInfo.parts` lists the parts with their addresses and sizes.

C++/CLI support was checked against .NET Framework images that ship with Windows: `System.EnterpriseServices.Wrapper.dll` and `System.Data.dll`. Their managed parts are fully analyzed, and all 73 native methods are disassembled. IJW imports, which have no DLL in metadata, show up as `jmp [IAT]` thunks.

## Design

Analysis runs as a pipeline. The analysis stages (catalog through native code) run only for the groups you request, and each stage sits behind an interface.

| Stage | Location | Responsibility |
|---|---|---|
| InputResolver | `src/NetScope/Input` | Turns files, masks (including `**`), directories and list files into an ordered list without duplicates |
| AssemblyLoader | `src/NetScope/Loading` | Opens files read-only through memory mapping, checks the PE and CLR headers, detects R2R, C++/CLI and NativeAOT images, and resolves dependencies: other input files, then the file's directory, then `--ref-path`, then the installed framework |
| MethodCatalog | `src/NetScope/Catalog` | Walks types and methods in metadata order, classifies methods, applies `--filter` and `--type` |
| CallAnalyzer | `src/NetScope/Calls` | Finds `call`, `callvirt`, `newobj`, `calli`, `ldftn` and `ldvirtftn`, and builds the reverse index and call graph across the group |
| CfgBuilder | `src/NetScope/Cfg` | Builds blocks and edges with conditions recovered by symbolic stack evaluation (e.g. `user != null`), finds loops from back edges, builds the exception region tree, computes complexity as E − N + 2 |
| PseudoGenerator | `src/NetScope/Pseudo` | Runs ICSharpCode.Decompiler with a time limit, with a three-address fallback |
| AsmProvider | `src/NetScope/Asm`, `src/NetScope/Native` | Produces the IL listing; reads ReadyToRun images and `.pdata` without running anything; disassembles with Iced (x86/x64) and Disarm (ARM64); gets JIT code through `DOTNET_JitDisasm` in a helper process |
| Reporter | `src/NetScope/Reporting` | Writes text, JSON, DOT and Mermaid |

Metadata is read with `System.Reflection.Metadata`, the same layer the decompiler uses, so each image is mapped only once.

## Safety

- Without `--allow-run`, assemblies are only read as files. They are never loaded into a process or executed. The test `AnalyzedCodeIsNeverExecutedWithoutAllowRun` checks this: the sample assembly has a module initializer that leaves a marker file if its code ever runs.
- With `--allow-run`, NetScope starts itself as a separate process (`__jit-helper`). That process loads the assembly and JIT-compiles the selected methods through `RuntimeHelpers.PrepareMethod`. If it exceeds `--run-timeout`, NetScope kills it together with its child processes. Loading an assembly can run its initializers, so use this mode in a VM or container.
- NetScope makes no network requests.

## Limitations

- **ReadyToRun:** composite images are not supported, and NetScope reports the reason. Two things are not shown: the cold parts of hot/cold-split methods (these appear only in profile-guided builds), and instantiations of this assembly's generic methods that were compiled into other assemblies of the version bubble. For methods without R2R code, add `--allow-run`.
- **JIT:** code is produced for the current machine's architecture at FullOpts, because the helper disables tiered compilation. Open generic methods are compiled as their shared instantiation over `object`. .NET Framework assemblies are compiled by the .NET 8+ runtime, so some of them may fail to load.
- **ARM64 disassembler:** Disarm (MIT) is only available as a prerelease package. It has been checked against linux-arm64 R2R images from the official runtime package.
- **NativeAOT** images are detected but not analyzed. Obfuscated assemblies are shown as they are; there is no deobfuscation.
- **Missing dependencies:** on a machine without .NET installed, framework assemblies cannot be found. Types are then shown by their metadata names, and pseudocode may be simplified. Pass `--ref-path` to fix this.

## Tests

`tests/NetScope.Samples` is a sample assembly with classes, properties, events, operators, a finalizer, async methods, an iterator, generics, lambdas, local functions, P/Invoke, loops, `switch`, nested `try/catch/finally` and `catch when`. The tests in `tests/NetScope.Tests` cover:

- method kinds;
- the IL listing, compared instruction by instruction and offset by offset with ILSpy's independent ildasm-style disassembler;
- the CFG: loops, `switch`, nested regions;
- the decompiler, which must handle at least 95% of methods;
- exit codes and clean stdout;
- JSON, DOT and Mermaid output;
- deterministic output with different thread counts;
- ReadyToRun code in CoreLib and in an R2R copy of the sample assembly that the test build publishes: funclets and generic instantiations;
- a C++/CLI image from .NET Framework (Windows only);
- that no analyzed code runs without `--allow-run`;
- reference output in `tests/NetScope.Tests/Golden`.

After an intended change to the output format, regenerate the reference files and review the diff:

```
NETSCOPE_UPDATE_GOLDEN=1 dotnet test NetScope.sln
```

CI (`.github/workflows/ci.yml`) runs the tests on Windows and Linux and publishes single-file binaries for win-x64, linux-x64, win-arm64 and linux-arm64.

## License

The project depends on ICSharpCode.Decompiler, Iced, Disarm and System.CommandLine, all MIT-licensed. The repository does not include a license file yet.
