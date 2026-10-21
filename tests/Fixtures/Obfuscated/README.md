# Obfuscation fixtures

Inputs of `ObfuscationTests` (TZ 4.3). They are committed as binaries so that the tests are deterministic
and run on every platform without the obfuscator.

| File | What it is |
| --- | --- |
| `plain.exe` | `src/` built with `dotnet build -c Release` (net472) |
| `confuserex-normal.exe` | `plain.exe` after ConfuserEx 1.6.0, preset `normal` (`normal.crproj`) |
| `confuserex-nowatermark.exe` | The same without the `ConfusedBy` watermark (`nowatermark.crproj`), so detection relies on the signs alone |

These files are only read by NetScope, never run by the tests.

## Regenerating

ConfuserEx needs Windows and .NET Framework 4.6.1 or later. It takes `mscorlib` from the GAC, which is why the sample targets net472.

```powershell
dotnet build tests/Fixtures/Obfuscated/src -c Release
# ConfuserEx-CLI.zip from https://github.com/mkaring/ConfuserEx/releases/tag/v1.6.0
# (SHA-256 a00de7cddc740f7edb1baab4c6c9073553dcc88f7e873d15b7fd34ddd33753d7)
cd tests/Fixtures/Obfuscated
Confuser.CLI.exe -n normal.crproj
Confuser.CLI.exe -n nowatermark.crproj
Move-Item out/normal/ObfuscationSample.exe confuserex-normal.exe -Force
Move-Item out/nowatermark/ObfuscationSample.exe confuserex-nowatermark.exe -Force
Copy-Item src/bin/Release/net472/ObfuscationSample.exe plain.exe -Force
Remove-Item -Recurse out
```

ConfuserEx randomizes names and keys, so a new run produces different bytes with the same signs.
