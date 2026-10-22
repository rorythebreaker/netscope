---
name: netscope
description: Analyze .NET assemblies (.dll/.exe) with the NetScope CLI — list methods, calls and callers, control flow graphs, C# pseudocode, IL, ReadyToRun native code, security audit with SARIF, obfuscation detection, version diff, Ghidra annotation scripts and a read-only debugger. Use this skill whenever the user wants to understand, reverse-engineer, audit, compare or review a compiled .NET binary — "what does this DLL do", "find where it starts processes / talks to the network / uses weak crypto", "is this assembly obfuscated", "what changed between v1 and v2 of this dll", "decompile this method", "name the functions in Ghidra", "step through this program" — even if NetScope is not named and only a .dll, .exe, NuGet package content or bin folder of a .NET app is mentioned.
---

# NetScope

NetScope is a command-line static analyzer for .NET assemblies (.NET Framework 2.0–4.8, .NET Core 3.1, .NET 5–10, .NET Standard; also ReadyToRun, C++/CLI and NativeAOT images). It reads files without running them and prints, per method: kind, calls, control flow, pseudocode, IL and native code. Output is deterministic text, JSON, SARIF, DOT or Mermaid.

This skill is about using it well: picking the right command for the question, keeping output small enough to reason about, and never running the analyzed code without the user's consent.

## Safety first: analysis never runs the code — unless you ask it to

Everything below is pure static analysis **except two things that execute the assembly's own code**:

- `--allow-run` (JIT native code for IL-only methods), and
- `netscope debug` (always needs `--allow-run`).

The assembly may be malware, a customer's binary or simply something with side effects (it can write files, call the network, start processes). NetScope sandboxes these runs (Job Object / rlimits, memory limit, no child processes, temporary working directory), but the sandbox is not a security boundary. So: do not add `--allow-run` or start `debug` on your own initiative. Ask the user first, say what will be executed, and suggest a VM or container for unknown files. Static output (`--il`, `--pseudo`, `--cfg`, R2R `--native`) answers almost every question without it.

`--i-know-running-as-admin` exists only for elevated CI runners; never add it to get past the "refusing to run as administrator" check on a user's machine — tell the user instead.

## Getting the tool

Check in this order and use the first that works:

1. `netscope --version` (on PATH: release binary, Scoop or `dotnet tool install -g NetScope.Tool`).
2. A release binary the user already has (`netscope.exe` / `netscope` from `netscope-<rid>-v<version>.zip|tar.gz`).
3. Inside the NetScope repository: `dotnet run --project src/NetScope -c Release -- <args>` or `dotnet src/NetScope/bin/Release/net8.0/netscope.dll <args>` after `dotnet build`.

Downloading a release is an outward action: ask before doing it, and verify the file against `SHA256SUMS.txt` of the release.

## Pick the command for the question

| The user wants to know… | Command (add `--no-color` when you read the output) |
| --- | --- |
| What is in this assembly, at a glance | `netscope app.dll --methods` (the default group) |
| What a specific method does | `netscope app.dll --filter "Ns.Type::Method" --pseudo --calls` |
| Exact behavior, offsets, tricky control flow | add `--il` and/or `--cfg` |
| Who calls X / where X is used | `--uses "System.Net.Http.HttpClient"` or `--uses "Ns.Type::Method*"` (select methods that call it) and `--calls` (shows callers too) |
| Where a string/URL/key appears | `--find-string "api.example.com"` (`--regex` for patterns), or `--strings` for the full table |
| Who reads/writes a field | `--field-refs "Ns.Type::_field"`, `--fields` |
| Security problems | `--audit` (rules NS001–NS010), `--format sarif --out report.sarif` for tools |
| Is it obfuscated, by what | `--detect-obfuscation` |
| What changed between versions | `netscope diff old.dll new.dll [--pseudo] [--audit]` |
| Call graph for a picture | `--callgraph --format mermaid` (or `dot`) |
| Native code | `--native` (ReadyToRun / C++/CLI are read from the file; IL-only needs `--allow-run` → ask first) |
| Function names for Ghidra | `netscope export-ghidra app.dll --out annotate.py` |
| Watch it run | `netscope debug app.dll --allow-run --script steps.txt` → ask first |

Full option lists and the JSON fields are in `references/cli.md` (read it when you need an option not shown here or want to parse JSON). Ghidra and the debugger are in `references/ghidra-and-debug.md`.

## Keep the output small

A real assembly has thousands of methods; `--all` on it is megabytes you cannot read. Work like a reverse engineer:

1. **Survey**: `--methods` (or `--methods --format json` and summarize types/kinds yourself). For a whole app, `--dir bin/Release --recursive --methods --out report/` writes one report per assembly.
2. **Narrow**: select methods with `--filter` (masks with `*` and `?`, matched against `Namespace.Type::Method`, case-insensitive, repeatable), `--type` (`async`, `pinvoke`, `ctor`, `entry-point`, …), `--uses`, `--find-string`, `--field-refs`. Selectors combine: values of one option are OR-ed, different options are AND-ed.
3. **Deep dive**: only then add `--pseudo`, `--il`, `--cfg` on the few methods that matter.

Prefer `--format json` when you will filter, count or cross-reference results in a script; prefer text when you will read a handful of methods. Put large output in a file (`--out`) and read the parts you need.

Nested types are written `Outer+Inner`; compiler-generated code (`<Main>d__1`, `<>c`) is folded back into its method in pseudocode — pass `--raw` to see the state machines and closures as compiled.

## Dependencies and accuracy

Pseudocode and type names are best when NetScope can find the referenced assemblies. It looks at the other input files, the assembly's directory, `--ref-path` directories, then the installed .NET. When analyzing a single DLL copied out of an app, point `--ref-path` at the app's folder; for .NET Framework DLLs on Linux, at a reference-assemblies folder. If a method falls back to simplified three-address pseudocode, the output says why (missing dependency, timeout, unusual IL) — use `--il` for that method.

Debug symbols are read automatically when a matching Portable PDB is embedded or next to the file (`--pdb-path` for elsewhere); then IL and blocks carry source lines (`--source` prints the text).

## Exit codes — read them

| Code | Meaning | What to do |
| --- | --- | --- |
| 0 | Everything processed | — |
| 1 | Some files or methods skipped or failed | Read stderr: the file, method and IL offset are named |
| 2 | Bad arguments | Fix the command line (`--help`) |
| 3 | Nothing could be opened | Not a managed assembly (native PE, NativeAOT, not a PE, missing file); stderr says which |
| 4 | `--audit --fail-on <level>` found something at that level | Expected outcome for a gate, not an error |
| 5 | `diff --exit-code` found differences | Expected outcome, like `git diff` |

Results go to stdout (or `--out`); warnings and errors only to stderr, so stdout is safe to parse.

## Reporting back

The user usually wants conclusions, not a dump. When you answer:

- Name methods by their full name (`Ns.Type::Method`) and cite IL offsets (`IL_001E`) or source lines for findings, so they can be checked.
- Quote short pseudocode snippets for the interesting parts instead of whole listings.
- For audit findings, give rule, level, method and offset, and say whether it looks like a real issue or a false positive in context.
- Say what you did not cover (methods filtered out, fallback pseudocode, missing dependencies, obfuscation).

## Examples

**"What does Billing.dll do with credit card numbers?"**
`netscope Billing.dll --find-string "card" --methods --no-color`, then `--filter` the hits with `--pseudo --calls`, then `--uses` on the methods they call to follow the data.

**"Is this NuGet package safe to use?"**
`netscope lib/net8.0/*.dll --audit --no-color` and `--detect-obfuscation`; then read the flagged methods with `--pseudo`. Mention that static analysis cannot prove the absence of malicious behavior.

**"What changed in v2.3.1?"**
`netscope diff old/ new/ --pseudo --audit --no-color` (directories are matched by assembly name). Summarize added/removed/changed methods and new findings.
