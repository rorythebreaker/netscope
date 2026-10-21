# NativeAOT and native PE in `export-ghidra`

TZ-NetScope-3 §4.3, option B: can the method names of a NativeAOT image be recovered without a PDB?

## Answer

Yes, for most methods. A NativeAOT application keeps the data that `Exception.StackTrace` needs to name frames when
no symbols are deployed. `export-ghidra` reads it and names the functions in Ghidra. A native PE that is not NativeAOT
has no .NET data at all, and `export-ghidra` refuses it with exit code 3 and the reason.

| Input | `export-ghidra` |
| --- | --- |
| NativeAOT image (PE, Windows) | Names the compiled methods from the image's stack trace metadata. Exit code 0 |
| NativeAOT image published with `StackTraceSupport=false` | No names in the image: error with that reason, exit code 3 |
| Native PE without .NET data | Error: Ghidra reads its exports, imports and `.pdata` itself, and NetScope has nothing to add. Exit code 3 |
| NativeAOT for Linux or macOS (ELF, Mach-O) | Not a PE file, so the loader rejects it (exit code 3). NetScope reads PE images only |

## Where the names are

ILC (the NativeAOT compiler) emits a module header with the same layout as a ReadyToRun header: the signature
`RTR`, a version, and a table of sections whose start and end are absolute addresses. NetScope finds it by scanning
the non-executable sections for the signature and validating the table (entry size, entry type and addresses inside
the image). Two of the sections are needed (section id = 300 + blob id):

| Section | Content |
| --- | --- |
| `BlobIdStackTraceMethodRvaToTokenMapping` (27) | Number of entries, then for each compiled method: a command byte, optional updates of the owning type (a full metadata handle), the name and the signature, and a 32-bit relative pointer to the method's entry point |
| `EmbeddedMetadata` (13) | The names, in the compact NativeFormat metadata encoding (`TypeDefinition`, `TypeReference`, `TypeSpecification`, `NamespaceDefinition`/`Reference`, `ConstantStringValue` records) |

The full name is built from the namespace chain, the enclosing types (`+`) and the method name, e.g.
`AotSample.Program+<Main>d__1::MoveNext`. For an instantiated generic type the generic definition's name is used.

The layouts follow dotnet/runtime `release/8.0` through `release/10.0`. One thing changed between them: in .NET 8 the
handle type is in the top 8 bits of a handle, later in the top 7 bits. NetScope tells the two apart from the first
owning-type handle, which must be a `TypeDefinition`, `TypeReference` or `TypeSpecification`.

## Results

`tests/NetScope.Samples.NativeAot`, published for win-x64 in CI (`nativeaot` job), without a PDB:

| | Names recovered | Linker function symbols | Address check | Name check |
| --- | ---: | ---: | --- | --- |
| .NET 8 | 3465 | 3950 | every address is a symbol in the MSVC linker map | every name matches the linker symbol |
| .NET 10 | 2833 | 3004 | same | same |

The methods without a name are runtime stubs and helpers that ILC gives no stack trace entry (for example the
`<Main>` wrapper), and methods whose identical code was folded into another one (Ghidra gets the first name at that
address). In Ghidra 11.1.2, every exported name is applied at its address by the Python (Jython and Ghidrathon) and
Java scripts, and a second run adds nothing (`ghidra` and `ghidra-python3` jobs).

Checks: `tests/Ghidra/Check-NativeAot.ps1` (names and addresses against the linker map of the same build) and
`tests/Ghidra/Check-Ghidra.ps1` (the scripts in Ghidra).

## Found on the way

A script with one call per function did not run on large images: Jython compiles the module to a JVM class and
stopped with "Module or method too large" at 3465 calls; the Java script would hit javac's "code too large" in the
same way. The scripts now carry the annotations as a data table split into string constants, so their code no longer
grows with the number of functions. The R2R and C++/CLI exports use the same format.

## Text for TZ-NetScope-2

The TZ files are kept outside the repository. To match this behavior:

- §5.2: "NativeAOT: the names of the compiled methods are taken from the image's stack trace metadata (no PDB needed).
  An image published with `StackTraceSupport=false` has no names; `export-ghidra` reports this and exits with 3."
- §6.3: "A native PE without .NET data is not a target of `export-ghidra`: it exits with 3 and says that Ghidra reads
  the PE tables itself."
