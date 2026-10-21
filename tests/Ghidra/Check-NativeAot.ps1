<#
.SYNOPSIS
  TZ-NetScope-3 §4.3: checks the method names export-ghidra reads from a NativeAOT image (no PDB) against the
  MSVC linker map of the same build.

.DESCRIPTION
  The linker map ("/MAP") lists every symbol ILC emitted with its address. For each function in the exported
  script the check requires:
    1. a linker symbol that starts exactly at that RVA (the address is a method entry point), and
    2. that symbol's mangled name contains the method name and its type name, with every character other than
       a letter or digit replaced by '_' (ILC's mangling keeps those, e.g. AotSample_Ledger__Deposit).
  It also prints which share of the linker's method symbols got a name. Exits with 1 if a check fails.
  Works with Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  ./Check-NativeAot.ps1 -NetScopeDll src/NetScope/bin/Release/net8.0/netscope.dll -Image aot/net10.0/App.exe `
                        -LinkerMap obj/Release/net10.0/win-x64/link.map -WorkDir $env:TEMP\aot-check
#>
param(
    [Parameter(Mandatory)] [string] $NetScopeDll,
    [Parameter(Mandatory)] [string] $Image,
    [Parameter(Mandatory)] [string] $LinkerMap,
    [Parameter(Mandatory)] [string] $WorkDir
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $WorkDir | Out-Null
$failures = 0
function Assert-Check([bool] $condition, [string] $message) {
    if ($condition) { Write-Host "  ok    $message" } else { Write-Host "  FAIL  $message"; $script:failures++ }
}

Write-Host "== $Image"
$script = Join-Path $WorkDir 'aot.py'
& dotnet $NetScopeDll export-ghidra $Image --out $script
if ($LASTEXITCODE -ne 0) { throw "export-ghidra exited with $LASTEXITCODE" }

# Exported functions: the script's data table has one record per line, f \u001f <rva> \u001f <name> \u001f ...
$functions = foreach ($m in Select-String -Path $script -Pattern '"f\\u001f(\d+)\\u001f((?:[^"\\]|\\.)*?)\\u001f') {
    [pscustomobject]@{ Rva = [int]$m.Matches[0].Groups[1].Value; Name = [regex]::Unescape($m.Matches[0].Groups[2].Value) }
}

# Linker map: "Preferred load address is 0000000140000000", then " 0001:00000450  <symbol>  0000000140001450 f  obj"
$text = Get-Content $LinkerMap
$base = [Convert]::ToUInt64(([regex]::Match(($text -join "`n"), 'Preferred load address is ([0-9a-fA-F]+)')).Groups[1].Value, 16)
$symbols = @{}
$sectionOf = @{}
foreach ($line in $text) {
    # ILC's symbols are not always flagged 'f' (function), so the flag is optional.
    $m = [regex]::Match($line, '^\s*([0-9a-fA-F]{4}):[0-9a-fA-F]{8}\s+(\S+)\s+([0-9a-fA-F]{8,16})\s')
    if (-not $m.Success) { continue }
    $va = [Convert]::ToUInt64($m.Groups[3].Value, 16)
    if ($va -lt $base -or ($va - $base) -gt [int]::MaxValue) { continue }   # absolute symbols, not in the image
    $rva = [int]($va - $base)
    if (-not $symbols.ContainsKey($rva)) { $symbols[$rva] = New-Object System.Collections.Generic.List[string]; $sectionOf[$rva] = $m.Groups[1].Value }
    $symbols[$rva].Add($m.Groups[2].Value)
}
# Coverage is measured against the symbol addresses of the section(s) that hold the exported functions (the code).
$codeSections = @($functions | Where-Object { $sectionOf.ContainsKey($_.Rva) } | ForEach-Object { $sectionOf[$_.Rva] } | Sort-Object -Unique)
$methodSymbols = @($sectionOf.Keys | Where-Object { $codeSections -contains $sectionOf[$_] }).Count
Write-Host "  $($functions.Count) function(s) exported, $methodSymbols symbol address(es) in the code section(s) $($codeSections -join ', ')"

function Mangle([string] $s) { ($s -replace '[^A-Za-z0-9]', '_') }
# ILC names some primitive types by their element type in symbols (System.Boolean -> Bool__GetHashCode).
$ilcAliases = @{ 'Boolean' = 'Bool'; 'Byte' = 'UInt8'; 'SByte' = 'Int8'; 'Single' = 'Float'; 'Double' = 'Double' }

$atSymbol = 0; $named = 0; $bad = New-Object System.Collections.Generic.List[string]
foreach ($f in $functions) {
    if (-not $symbols.ContainsKey($f.Rva)) { $bad.Add(("no symbol at RVA 0x{0:X}: {1}" -f $f.Rva, $f.Name)); continue }
    $atSymbol++
    $sep = $f.Name.LastIndexOf('::')
    $method = Mangle($f.Name.Substring($sep + 2))
    $simpleType = ($f.Name.Substring(0, [Math]::Max(0, $sep)) -split '[.+]')[-1]
    $type = Mangle($simpleType)
    $alias = if ($f.Name.StartsWith('System.') -and $ilcAliases.ContainsKey($simpleType)) { $ilcAliases[$simpleType] } else { $type }
    if ($symbols[$f.Rva] | Where-Object { $_.Contains($method) -and ($_.Contains($type) -or $_.Contains($alias)) }) { $named++ }
    else { $bad.Add(("0x{0:X}: {1} vs {2}" -f $f.Rva, $f.Name, ($symbols[$f.Rva] -join ' | '))) }
}
$bad | Select-Object -First 20 | ForEach-Object { Write-Host "        $_" }
Assert-Check ($functions.Count -gt 0) "export-ghidra named functions in the NativeAOT image"
Assert-Check ($atSymbol -eq $functions.Count) "every exported address is a linker symbol ($atSymbol/$($functions.Count))"
Assert-Check ($named -eq $functions.Count) "every name matches the linker symbol at its address ($named/$($functions.Count))"
Write-Host ("  coverage: {0:0.0}% of the linker's function symbols got a name" -f (100.0 * $functions.Count / [Math]::Max(1, $methodSymbols)))
Assert-Check ((Get-Content $script -Raw).Contains('NativeAOT')) "the script header names the image kind"

if ($failures -gt 0) { Write-Host "$failures check(s) failed"; exit 1 }
Write-Host 'all NativeAOT checks passed'
