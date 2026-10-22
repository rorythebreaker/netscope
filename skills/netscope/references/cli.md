# NetScope CLI reference

Verified against NetScope 1.2.0 (`netscope --help`, `netscope <command> --help`). If the installed version differs, run `--help` to confirm.

Contents: [Analysis](#analysis-netscope-files-options) · [Selecting methods](#selecting-methods) · [Output](#output) · [JSON fields](#json-fields) · [Security rules](#security-rules) · [diff](#diff) · [Text output layout](#text-output-layout)

## Analysis: `netscope <files...> [options]`

Inputs: files or masks (`*.dll`, `bin\Release\*.exe`, `**` recursion in masks), `--dir <dir>` (`-r/--recursive`), `--input-list <file>` (one path or mask per line, `#` comments, relative to the list file). The file type is decided by the PE/CLR header, not the extension; native PE and non-PE files are skipped with a reason on stderr.

| Group | Option | Notes |
| --- | --- | --- |
| Methods | `--methods` | Kind and signature. The default when no group is given |
| Calls | `--calls` | Call sites (`call`, `callvirt`, `newobj`, `calli`, `ldftn`, `ldvirtftn`) with internal/external, plus callers across all input files |
| Call graph | `--callgraph` | Between methods of all inputs; with only `--callgraph`, only the graph is printed |
| CFG | `--cfg` | Blocks, edges with conditions (`user != null`), loops, unreachable blocks, exits, try/catch/filter/finally/fault regions, cyclomatic complexity |
| Pseudocode | `--pseudo` | ICSharpCode.Decompiler; on failure a three-address fallback plus the reason. `--raw` keeps lambdas/async/iterators as compiled. `--decompile-timeout <s>` (20), `--max-abandoned <n>` (4) |
| IL | `--il` | ildasm-style listing; `--source` adds source text (PDB), `--source-path <dir>` |
| Native | `--native` | ReadyToRun bodies, funclets and generic instantiations; C++/CLI native methods. IL-only methods need `--allow-run` (executes code in a sandboxed helper: `--run-memory 1G`, `--run-timeout 30`) |
| Everything | `--all` | All groups — only for a few filtered methods |
| Fields | `--fields` | Fields each method reads/writes |
| Strings | `--strings` | All string literals and where they are loaded |
| Audit | `--audit` | Security rules; `--rules NS001,NS004`, `--min-level note\|warning\|error`, `--fail-on <level>` (exit 4) |
| Obfuscation | `--detect-obfuscation` | Verdict `no`/`possible`/`yes`, signs, obfuscator name, hint |

Other: `--ref-path <dir>` (dependencies, repeatable), `--no-pdb`, `--pdb-path <dir>`, `--threads <n>`, `--no-color`, `-q/--quiet` (only errors on stderr), `-v/--verbose`.

## Selecting methods

| Option | Matches |
| --- | --- |
| `--filter <mask>` | Full name `Namespace.Type::Method`, `*` and `?`, case-insensitive. Examples: `"MyApp.Auth.*"`, `"*::Login*"`, `"MyApp.Db.Users::Find"` |
| `--type <kind>` | `ctor`, `cctor`, `finalizer`, `property`, `getter`, `setter`, `event`, `operator`, `conversion`, `abstract`, `virtual`, `override`, `sealed`, `static`, `instance`, `pinvoke`, `extern`, `internalcall`, `runtime`, `native`, `async`, `iterator`, `lambda`, `anonymous`, `local-function`, `localfunc`, `state-machine`, `compiler-generated`, `generic`, `extension`, `entry-point`, `no-body`, `nobody`. Comma-separated or repeatable |
| `--uses <mask>` | Methods that call a method (`System.Diagnostics.Process::Start*`) or any member of a type (`System.Diagnostics.Process`) |
| `--find-string <text>` | Methods with a string literal containing the text (case-insensitive), or matching a `*`/`?` mask; `--regex` for a regular expression |
| `--field-refs <mask>` | Methods that read, write or take the address of a field (`MyApp.Db.User::_name`, auto-properties: `<Name>k__BackingField`) |

Several values of one option are OR-ed; different options and `--filter`/`--type` are AND-ed. `--uses`, `--find-string` and `--field-refs` add a `[Matches]` group with the IL offset (and source line) of each match.

## Output

`--format text|json|dot|mermaid|sarif` (sarif implies `--audit`). `--out <file>` writes one file; `--out <dir>/` (trailing slash or existing directory) writes one report per assembly plus `callgraph.<ext>`. Output is UTF-8 without BOM, deterministic: files in argument order, methods in metadata order, so two runs can be diffed.

## JSON fields

Root: `tool`, `version`, `assemblies[]`, and `callgraph` when requested.

Assembly: `file`, `path`, `assembly`, `version`, `targetFramework`, `metadataVersion`, `machine`, `readyToRun`, `mixedMode`, `notes[]`, `errors[]`, `methods[]`; `symbols` (with a PDB), `strings[]` (`--strings`), `audit` (counts, `--audit`), `obfuscation` (`verdict`, `obfuscator`, `signs[]`, `hint`).

Method: always `assembly`, `namespace`, `type`, `name`, `signature`, `token`, `access`, `kinds[]`, `rva`, `size`, `errors[]`. Per group:

| Group | Fields |
| --- | --- |
| `--calls` | `calls[]`, `callers[]` |
| `--cfg` | `complexity`, `blocks[]`, `loops[]`, `regions[]`, `unreachable`, `exits` |
| `--pseudo` | `pseudocode`, `pseudocodeSource` (`decompiler` or `fallback`), `pseudocodeError` |
| `--il` | `il[]`, `ilInfo` |
| `--native` | `native[]` (each line has `part`), `nativeInfo` (`available`, `parts[]` with `name`, `address`, `size`) |
| selectors | `matches[]` |
| `--fields` | `fields[]` |
| `--audit` | `findings[]` |

With symbols, instructions and blocks carry `source` (`file`, `line`). Fields of groups you did not request are left out.

Example (PowerShell): `(netscope app.dll --type async --format json | ConvertFrom-Json).assemblies[0].methods | Select type,name`.
Example (bash + jq): `netscope app.dll --audit --format json | jq '.assemblies[].methods[] | {m: (.type+"::"+.name), f: .findings}'`.

## Security rules

| ID | Finds | Level |
| --- | --- | --- |
| NS001 | Process start (`Process.Start`, `ProcessStartInfo`) | warning |
| NS002 | Dynamic code loading (`Assembly.Load*`, `AppDomain.Load`, `AssemblyLoadContext.LoadFrom*`, `Activator.CreateInstance(string, …)`) | warning |
| NS003 | Reflection by string name followed by `Invoke` | note |
| NS004 | Unsafe deserialization (`BinaryFormatter`, `NetDataContractSerializer`, `LosFormatter`, `SoapFormatter`, Json.NET `TypeNameHandling` ≠ None) | error |
| NS005 | Weak crypto (MD5, SHA-1, DES, 3DES, RC2, ECB, `new Random()` for secrets) | warning |
| NS006 | TLS certificate validation callback that always returns `true` | error |
| NS007 | Hard-coded secrets (passwords in connection strings, AWS key IDs, PEM private keys; the secret is never printed) | error |
| NS008 | P/Invoke of sensitive kernel32/advapi32/ntdll functions, `Marshal.GetDelegateForFunctionPointer` | note |
| NS009 | SQL text built by concatenation, `string.Format` or interpolation | warning |
| NS010 | Registry writes, autorun keys, service installation | note |

Suppression: `[SuppressMessage("…", "NS004")]` (compiled only with `CODE_ANALYSIS`), `[UnconditionalSuppressMessage]`, or a `netscope.suppress` file (`NS004 MyApp.Legacy.*`) in the current directory or next to the assembly. The text header shows `Audit : N error(s), … , K suppressed`.

## diff

`netscope diff <old> <new> [options]` — two files, or two directories matched by assembly name (`-r` for subdirectories). Methods are matched by full name and signature; "changed" means the IL differs after tokens are replaced by names, so a rebuild of the same code shows no changes.

Options: `--pseudo` (pseudocode diff), `--audit` (only findings new in `<new>`; `--rules`, `--min-level`), `--filter`, `--type`, `--ref-path`, `--format text|json`, `--out`, `--exit-code` (0 = no differences, 5 = differences). A file literally named `diff` is analyzed as `./diff`.

Text summary line: `Methods : 0 added, 0 removed, 0 changed, 117 unchanged` and `Result : no changes`.

## Text output layout

```
Assembly : NetScope.Samples.dll
Name     : NetScope.Samples, Version=1.0.0.0
Path     : <full path>
Runtime  : .NETCoreApp,Version=v8.0   Machine: anycpu
Methods  : 3

Type     : MyApp.Auth.LoginService

Method   : bool Login(string user, string pass)
Kind     : instance, public, virtual
RVA      : 0x24F4   Size: 36 bytes   Token: 0x0600002F

[Calls]
  IL_000C  callvirt  MyApp.Db.Users::Find(string)                internal
  IL_001E  call      System.String::op_Equality(string, string)  external System.Runtime
[Callers]
  MyApp.Auth.AdminLoginService::Login  IL_0010  call  (NetScope.Samples.dll)

[Branches]  complexity=2  blocks=3  loops=0
  B0 IL_0000..IL_0002  -> B1 (user == null) | B2 (else)
```

Groups appear as `[Calls]`, `[Callers]`, `[Branches]`, `[Pseudocode]`, `[IL]`, `[Native]`, `[Matches]`, `[Fields]`, `[Audit]` in that method's block. With `--detect-obfuscation` the header has `Obfuscated: yes (ConfuserEx)` followed by `sign:` and `hint:` lines.
