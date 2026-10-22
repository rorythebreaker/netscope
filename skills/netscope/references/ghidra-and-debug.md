# Ghidra export and the debugger

## export-ghidra (static, safe)

`netscope export-ghidra <file> --out annotate.py [--lang python|java] [--audit] [--ref-path <dir>]`

Writes a script that names Ghidra's functions at their addresses with the .NET method names, puts the signature in a plate comment, labels funclets and IAT import thunks, and with `--audit` sets bookmarks at points of interest. Addresses are offsets from the image base. The script only calls the Ghidra API (no network, no files, no external commands); running it twice changes nothing. Checked with Ghidra 11.1.2: Jython (default for `.py`), Ghidrathon 4.0.0 (Python 3) and Java.

| Input | What happens |
| --- | --- |
| ReadyToRun image | Names every precompiled body and generic instantiation |
| C++/CLI image | Names native methods, labels `jmp [IAT]` thunks |
| NativeAOT image (Windows PE) | Names methods from the image's stack trace metadata, no PDB needed; not every stub gets a name |
| R2R image built for Linux/macOS | Works, but Ghidra must import it with the options written in the script header, e.g. `-loader PeLoader -processor AARCH64:LE:64:v8A -cspec default` (arm64) or `-processor x86:LE:64:default -cspec gcc` (x64). The file is not modified |
| Pure IL assembly | Warning "nothing to annotate"; exit 0 |
| Native PE without .NET data, NativeAOT built with `StackTraceSupport=false` | Refused with the reason, exit 3 |

Java: the file name must be a valid Java class name (`NetScopeAnnotate.java`); GhidraScript requires the public class to match the file.

Run it headless: `analyzeHeadless <projDir> <projName> -import app.dll [import options from the header] -postScript annotate.py`, or `-process app.dll` on an existing project. The script's last line is `NetScope: annotations applied [Python 3.12]` or `… , N failed (first RVA …)`.

## debug (executes the program — ask the user first)

`netscope debug <app.dll|app.exe> --allow-run [--script steps.txt] [--format json] [--out file] [-- program args]`

Runs the program as a child process under a read-only debugger in a separate `__debug-host` process and a sandbox (memory limit `--run-memory 1G`, session limit `--run-timeout 600`, no child processes unless `--allow-child-processes`, killed when NetScope exits). It stops on the entry point. There is no memory write, set-variable, setip or func-eval. Before running it prints the file's SHA-256 and keeps a session journal (`--audit-log <file>` for an extra copy); interactively it asks `Continue? [y/N]`.

Backends: .NET Core 3.1 / .NET 5–10 through DbgShim; .NET Framework 2.0–4.8 (`.exe` built against `mscorlib`) through `mscordbi`. Windows x64 only; elsewhere, or for a .NET Framework library or a 32-bit-only program, it refuses with a reason. It refuses to run elevated; do not add `--i-know-running-as-admin` to get around that.

Non-interactive use (what an agent should do): write the commands to a file and pass `--script`; the session ends when the script ends.

| Command | Effect |
| --- | --- |
| `break <mask>[+IL_xxxx]` | Breakpoint on method entry or an IL offset (mask as in `--filter`) |
| `breaks`, `delete <n>` | List / remove breakpoints |
| `continue` (`c`, `run`) | Run to the next stop |
| `step` (`s`), `next` (`n`), `out`, `stepi` (`si`) | Step in / over / out / one IL instruction |
| `stack` (`bt`), `frame <n>`, `threads` | Call stack, select frame, threads |
| `args`, `locals`, `print <name>[.field]` | Values of the selected frame (read-only; value materialization may report "not available" in this version) |
| `il`, `cfg`, `pseudo` | Code of the stopped method |
| `exceptions on\|off` | Stop on first-chance exceptions |
| `quit` (`q`, `kill`) | End the session |

Text stops look like `Stopped (breakpoint) at Ns.Type::Method:` followed by `#0  Ns.Type::Method  IL_0004  (module.dll)` frames; with `--format json` each stop is `{"event":"stopped","reason":…,"thread":…,"frames":[…]}`. The program's own output goes to stderr prefixed with `[program]`.

Example script:

```
break MyApp.Billing::Charge
continue
stack
pseudo
step
continue
```
