# NetScope

A command-line static analyzer for .NET assemblies. For each method it shows the method kind, the calls it makes, its control flow graph, C# pseudocode, the IL and the native code. NetScope does not run code from the assemblies it analyzes unless you pass `--allow-run`.

- Input: managed `.dll`/`.exe` files from .NET Framework 2.0–4.8, .NET Core 3.1, .NET 5–10 and .NET Standard. NetScope also reads ReadyToRun images and the managed part of C++/CLI images.
- Output: text, JSON, SARIF, Graphviz DOT, Mermaid.
- Platforms: Windows and Linux, x64 and ARM64. Ships as a single self-contained executable, so .NET does not need to be installed.

## Getting it

- **Single-file binary** for `win-x64`, `linux-x64`, `win-arm64` or `linux-arm64` from the [Releases](https://github.com/rorythebreaker/netscope/releases) page. It is self-contained, so .NET does not need to be installed. Builds of every commit are also attached to its CI run as artifacts.
- **dotnet tool** (needs the .NET 8 runtime or later): download `NetScope.Tool.<version>.nupkg` from the release and run `dotnet tool install -g NetScope.Tool --add-source <folder> --version <version>`. Publishing to nuget.org (`dotnet tool install -g NetScope.Tool`) and a Scoop bucket is prepared in CI and starts once the repository secrets are configured (see below).

Every release file is listed with its hash in `SHA256SUMS.txt` and has a GitHub build provenance attestation. To check that a binary was built by this repository's CI from the tagged commit:

```
gh attestation verify netscope-win-x64-v1.1.0.zip -R rorythebreaker/netscope
```

To publish a release, push a tag `vX.Y.Z`. CI builds the binaries and the `NetScope.Tool` package from that commit, attests them, and creates the GitHub release with the license files and checksums. If `.github/release-notes/vX.Y.Z.md` exists, it becomes the release notes. If the repository has the `NUGET_API_KEY` secret, the package is pushed to NuGet. If it has `SCOOP_BUCKET_TOKEN` (write access to `rorythebreaker/scoop-bucket`), the Scoop manifest is updated (`packaging/scoop/make-manifest.sh`).

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
| `--format text\|json\|dot\|mermaid\|sarif` | Output format |
| `--out <file\|dir>` | Write to a file, or to a directory if the path ends with `\` or `/` or already exists. In directory mode you get one report per assembly, plus `callgraph.<ext>` |
| `--raw` | Show lambdas, async methods and iterators in pseudocode in the form the compiler generated |
| `--threads <n>` | Number of files processed in parallel (default: number of cores) |
| `--decompile-timeout <s>` | Time limit for decompiling one method (default 20 s) |
| `--max-abandoned <n>` | After this many decompilations exceed the time limit, the remaining methods get the simplified pseudocode (default 4) |
| `--run-memory <size>` | Memory limit of the `--allow-run` helper process, e.g. `512M`, `2G` (default `1G`) |
| `--run-timeout <s>` | Time limit for the `--allow-run` helper process (default 30 s) |
| `--audit`, `--rules <ids>`, `--min-level <level>`, `--fail-on <level>` | Security rules NS001-NS010, see [Security rules](#security-rules) |
| `--format sarif` | SARIF 2.1.0 with the audit findings (implies `--audit`) |
| `--detect-obfuscation` | Obfuscation verdict, signs and obfuscator name for each assembly, see [Obfuscation](#obfuscation) |
| `--no-pdb` | Do not read debug symbols |
| `--pdb-path <dir>` | Directory searched for `.pdb` files after the assembly's own directory (repeatable) |
| `--source`, `--source-path <dir>` | Print source lines next to the IL. Files are taken from the path in the PDB, or found by name under `--source-path` |
| `--uses <mask>` | Select methods that call a method (`System.Diagnostics.Process::Start*`) or any member of a type (`System.Diagnostics.Process`) |
| `--find-string <text>` | Select methods with a string literal containing the text (case-insensitive), or matching a mask with `*`/`?`; `--regex` makes it a regular expression |
| `--field-refs <mask>` | Select methods that read, write or take the address of a field (`MyApp.Db.User::_name`) |
| `--fields` | Fields each method reads and writes |
| `--strings` | Table of all string literals of the assembly and where they are loaded |
| `--no-color`, `--quiet`, `--verbose`, `--help`, `--version` | Presentation and message level |

`--uses`, `--find-string` and `--field-refs` are filters, like `--filter`: they select the methods (in all input files) to which the other groups are then applied, and add a `[Matches]` group with the IL offset and source line of each match. Several values of one option are OR-ed; different options and `--filter`/`--type` are AND-ed.

### Debug symbols

NetScope reads Portable PDBs: embedded in the assembly, then `<name>.pdb` next to it, then in `--pdb-path`. Nothing is downloaded and no symbol server is used. A PDB is used only if its ID matches the CodeView record of the assembly; otherwise, or for a Windows PDB, NetScope prints a warning and continues without symbols. With symbols:

- the IL listing has `// Program.cs:42` before the first instruction of each sequence point (and the source text with `--source`);
- CFG blocks show their source lines;
- pseudocode and IL use the real names of local variables;
- JSON instructions and blocks have `source.file` and `source.line`.

If you pass only `--callgraph`, NetScope prints only the graph. With `dot` and `mermaid`, it draws the CFGs of the selected methods by default. Mermaid output is Markdown with ` ```mermaid ` blocks.

### Streams and exit codes

Results go to stdout or to the `--out` target. Warnings and errors go only to stderr and name the file, the method and the IL offset. Output is UTF-8 without BOM and uses the platform's line endings. Files are listed in argument order with duplicates removed, and methods in metadata order, so the output of two runs can be compared with `diff`.

| Code | Meaning |
|---|---|
| 0 | Everything was processed |
| 1 | Some files or methods were skipped or had errors (for example, native or corrupted files in a group) |
| 2 | Invalid arguments |
| 3 | No input file could be opened |
| 4 | `--audit --fail-on <level>`: a finding of that level or higher remains (takes precedence over 1) |

### JSON

Every method has `assembly`, `namespace`, `type`, `name`, `signature`, `token`, `access`, `kinds[]`, `rva`, `size` and `errors[]`. Each requested group adds its own fields: `calls[]`, `callers[]`, `complexity`, `blocks[]`, `loops[]`, `regions[]`, `pseudocode`, `pseudocodeSource`, `pseudocodeError`, `il[]`, `ilInfo`, `native[]`, `nativeInfo`, `matches[]` (`--uses`, `--find-string`, `--field-refs`) and `fields[]`. With symbols, instructions and blocks have `source`. The assembly gets `symbols` (`embedded` or the PDB path) and, with `--strings`, `strings[]`. With `--audit`, methods have `findings[]` and the assembly has `audit` (counts by level and of suppressed findings); `--detect-obfuscation` adds `obfuscation` (`verdict`, `obfuscator`, `signs[]`, `hint`). Fields of groups you did not request are left out. The call graph is in the root field `callgraph`.

## Comparing versions

```
netscope diff old/App.dll new/App.dll
netscope diff old-bin/ new-bin/ --pseudo --audit --format json --out diff.json
netscope diff old/App.dll new/App.dll --exit-code   # 0: no differences, 5: differences (like git diff)
```

You can compare two files, or two directories whose files are matched by assembly name. Methods are matched by full name and signature, not by metadata token, because tokens change between builds. Each method is `added`, `removed`, `changed` or unchanged. A method counts as `changed` when the hash of its IL differs. The hash is taken over the IL with tokens replaced by full names and branch targets replaced by instruction numbers, so rebuilding the same code shows no differences. For a changed method the output has:

- the IL difference in unified diff format;
- the calls that were added and removed;
- the change in cyclomatic complexity and in the number of blocks;
- with `--pseudo`, the pseudocode difference.

A separate `[Metadata]` section lists changed types, fields, attributes, assembly references and P/Invoke targets. With `--audit`, only the findings that are new in the second version are shown. `--filter` and `--type` restrict the comparison to some methods. Output formats: `text` and `json`. To analyze a file that is literally named `diff`, write `./diff`.

## Security rules

`--audit` checks the selected methods against the rules below and reports only the methods with findings, each with its level, IL offset and, with a PDB, its source line. The rules only read IL and metadata; nothing is executed. `--format sarif` writes SARIF 2.1.0 (and implies `--audit`); the file validates against the OASIS schema and loads into GitHub Code Scanning with `github/codeql-action/upload-sarif` (CI does this for the samples).

| ID | Finds | Level |
|---|---|---|
| NS001 | Process start: `Process.Start`, `ProcessStartInfo` | warning |
| NS002 | Dynamic code loading: `Assembly.Load*`, `AppDomain.Load`, `AssemblyLoadContext.LoadFrom*`, `Activator.CreateInstance(string, ...)` | warning |
| NS003 | Reflection by string name (`Type.GetType(string)`, `GetMethod(string)`, ...) followed by `Invoke` | note |
| NS004 | Unsafe deserialization: `BinaryFormatter`, `NetDataContractSerializer`, `LosFormatter`, `SoapFormatter`, Json.NET `TypeNameHandling` other than `None` | error |
| NS005 | Weak cryptography: MD5, SHA-1, DES, 3DES, RC2, ECB mode, `new Random()` in methods that create tokens, passwords or keys | warning |
| NS006 | TLS certificate validation callback that always returns `true` | error |
| NS007 | Hard-coded secrets: connection strings with `Password=`, AWS access key IDs, PEM private keys. The secret itself is never printed | error |
| NS008 | P/Invoke of sensitive `kernel32`, `advapi32`, `ntdll` functions (memory, processes, threads, tokens, services), `Marshal.GetDelegateForFunctionPointer` | note |
| NS009 | SQL text of `SqlCommand`, `OleDbCommand`, `NpgsqlCommand` built by concatenation, `string.Format` or interpolation | warning |
| NS010 | Registry writes, autorun keys, service installation | note |

- `--rules NS001,NS004` selects rules. `--min-level warning` drops findings below a level. `--fail-on error` returns exit code 4 when a finding of that level or higher remains.
- Suppression: `[SuppressMessage("...", "NS004")]` on the method, a containing type or the assembly. It is compiled only with `CODE_ANALYSIS` defined; `[UnconditionalSuppressMessage]` is always compiled. You can also use a `netscope.suppress` file in the current directory or next to the assembly, with lines such as `NS004 MyApp.Legacy.*` (rule, then a method mask; `#` starts a comment).
- A rule is a class that implements `IRule` (`src/NetScope/Audit/Rules`). NetScope finds rules by reflection, so a new rule is one new file.

## Obfuscation

`--detect-obfuscation` prints a verdict for each assembly: `no`, `possible` (one sign) or `yes` (an obfuscator's attribute, or two or more signs). It also lists the signs and names the obfuscator when its attribute identifies it.

| Sign | How it is detected |
|---|---|
| Obfuscator attributes | `ConfusedByAttribute`, `DotfuscatorAttribute`, `SmartAssembly.Attributes`, and others, on the assembly, the module or the types |
| Unreadable names | More than 30% of type, method and field names are one character long or contain control, format, private-use or unassigned characters |
| Flat CFG | A `switch` dispatcher with 10 or more cases inside a loop, whose case number is computed (`xor`/`rem`) |
| String encryption | Few `ldstr` but many calls to one static method that takes a number |
| Corrupted bodies | Bodies that cannot be decoded or that reference tokens that do not exist |
| Proxy calls | Many static methods with unreadable names whose body is a single call |

NetScope does not deobfuscate. For `yes`, it suggests processing the file with an external tool such as de4dot first. It was checked against ConfuserEx output (`tests/Fixtures/Obfuscated`) and, for false positives, against NetScope's dependencies and large framework assemblies (`System.Private.CoreLib`, `System.Text.RegularExpressions`, `System.Private.Xml`).

## Native code

| Case | Output |
|---|---|
| ReadyToRun, ordinary method | Main body plus the funclets of its `catch`/`filter`/`finally`/`fault` handlers, that is, the RUNTIME_FUNCTION entries from the entry point up to the next entry point |
| ReadyToRun, generic method or method of a generic type | Every instantiation precompiled into the image (`InstanceMethodEntryPoints`), e.g. `List<int>::Add` or `Generic<__Canon>::Map<__Canon>`. `__Canon` is the shared code used for reference types |
| C++/CLI native method | Machine code from the image. The size comes from the exception directory (`.pdata`). For leaf functions and import thunks on x86/x64, the size is estimated by a linear sweep to `ret`/`jmp`, and the output says so |
| IL method without ReadyToRun code | JIT output with `--allow-run`; otherwise the reason it is unavailable |

In text output the parts are separated by headers such as `; funclet 1 (address, size)`. In JSON each line has a `part` field, and `nativeInfo.parts` lists the parts with their addresses and sizes.

C++/CLI support was checked against .NET Framework images that ship with Windows: `System.EnterpriseServices.Wrapper.dll` and `System.Data.dll`. Their managed parts are fully analyzed, and all 73 native methods are disassembled. IJW imports, which have no DLL in metadata, show up as `jmp [IAT]` thunks.

## Debugging (read-only)

`netscope debug` runs an assembly as a child process under a read-only debugger, stops on its entry point and shows the call stack. The analyzed code is executed, so `--allow-run` is required.

```
netscope debug app.dll --allow-run                       # stop on Main, show the stack (interactive)
netscope debug app.dll --allow-run --script steps.txt    # run commands from a file, then exit
netscope debug app.dll --allow-run --format json         # each stop as a JSON object
netscope debug app.dll --allow-run -- arg1 arg2          # arguments after -- go to the program
```

It is strictly an observer. There is no memory write, no set-variable, no `setip` and no func-eval anywhere in the protocol: the target is only read. The design keeps the debug API out of the analyzer:

- The ICorDebug session lives in a separate `__debug-host` process, reached over pipes (no TCP, no DAP port); a fault in the debug API never reaches NetScope.
- The target is launched suspended and confined before it runs: a memory limit, no child processes, and it is killed when NetScope exits (Windows Job Object).
- `--allow-run` is mandatory — without it no process is created. NetScope refuses to run while elevated unless `--i-know-running-as-admin` is given (and logged). The file's SHA-256 is shown before launch, and the run is aborted if the file changes between inspection and launch.
- Every session is journaled (`%LOCALAPPDATA%\NetScope\logs`, or `--audit-log <file>`): time, user, path and SHA-256, arguments, the restrictions applied, the target PID and the exit code. Variable values are never written to the log.

Commands (`--script` file, interactive REPL, or one per stop in the default run):

| Command | Action |
|---|---|
| `break <mask>[+IL_xxxx]` | Breakpoint on every method matching the mask (as in `--filter`), optionally at an IL offset |
| `breaks`, `delete <n>` | List / remove breakpoints |
| `continue`, `step`, `next`, `out`, `stepi` | Resume; step in / over / out / one instruction |
| `stack`, `frame <n>`, `threads` | Call stack, select a frame, list threads |
| `args`, `locals`, `print <name>[.field]` | Read arguments, locals and fields (read-only, bounded, escaped) |
| `il`, `cfg`, `pseudo` | IL, basic blocks, and decompiled pseudocode of the stopped method (static analysis in the analyzer process) |
| `exceptions on|off`, `quit` | Stop on first-chance exceptions; end the session |

The backend is Windows x64 in this version (on other platforms the host reports a clear error rather than crashing). Breakpoints, stepping, the stack, and the `il`/`cfg`/`pseudo` code views are working; value inspection (`args`/`locals`/`print`) is wired end-to-end but currently reports values as unavailable, pending a fix to ICorDebug argument/local materialization. `break <file>:<line>` and `--attach` are not yet implemented.

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
- With `--allow-run`, NetScope starts itself as a separate process (`__jit-helper`). That process loads the assembly and JIT-compiles the selected methods through `RuntimeHelpers.PrepareMethod`. Loading an assembly can run its initializers, so the helper runs in a sandbox (`Sandboxing/Sandbox.cs`), and the restrictions are in place before any analyzed code runs:

  | Restriction | Windows | Linux |
  | --- | --- | --- |
  | Dies with NetScope | Job Object, `KILL_ON_JOB_CLOSE` | `PR_SET_PDEATHSIG` |
  | Memory (`--run-memory`, default 1 GB) | Job Object, `JOB_OBJECT_LIMIT_PROCESS_MEMORY` | `RLIMIT_AS` on growth beyond the runtime's initial reservation, plus `DOTNET_GCHeapHardLimit` |
  | No child processes | Job Object, active process limit 1 | seccomp filter on `fork`/`clone`/`execve` (EPERM); `RLIMIT_NPROC` if seccomp is unavailable |
  | Time (`--run-timeout`) | NetScope timer | NetScope timer |
  | Working directory | New temporary directory, deleted afterwards | Same |
  | Environment | Only `PATH`, `DOTNET_*` (and `SystemRoot` on Windows) | Only `PATH`, `DOTNET_*` |

  If a restriction cannot be applied, the helper does not run and the native code of the methods shows the reason. The test `JitHelperBlocksChildProcessesAndLargeAllocations` uses an assembly whose module initializer tries to start `cmd`/`sh` and allocate 4 GB. The sandbox limits the damage an assembly can do, but it is not a security boundary against a determined attacker: analyze unknown assemblies in a VM or container.
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

NetScope is released under the [MIT License](LICENSE). The binaries include ICSharpCode.Decompiler, Iced, Disarm, System.CommandLine and the .NET runtime, all under the MIT License. Their notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which is also included in every release archive.
