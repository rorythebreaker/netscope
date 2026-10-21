<#
.SYNOPSIS
  TZ-NetScope-2 §5.5 acceptance check in a real Ghidra (analyzeHeadless), for Python (Jython or Ghidrathon) and Java scripts.

.DESCRIPTION
  For each target assembly:
    1. Builds the expected function list from `netscope --native --format json`.
    2. Exports the annotation script with `netscope export-ghidra --audit` (python and java).
    3. Imports the assembly into a throw-away Ghidra project, runs the script, then NsVerify.java,
       and requires every expected function to exist at its RVA with the expected name.
    4. Python only: runs the script twice and requires the same number of user symbols and bookmarks
       as after a single run (idempotency).
  The Python script reports the interpreter it ran in; -PythonMajor 2 expects Jython (Ghidra's default for .py),
  -PythonMajor 3 expects Ghidrathon. Checked with Ghidra 11.1.2, Jython 2.7 and Ghidrathon 4.0.0 (Jep 4.2.0).
  Exits with 1 if any check fails. Works with Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  ./Check-Ghidra.ps1 -GhidraDir C:\tools\ghidra_11.1.2_PUBLIC -NetScopeDll src/NetScope/bin/Release/net8.0/netscope.dll `
                     -Targets tests/NetScope.Samples/bin/r2r/Release/NetScope.Samples.dll -WorkDir $env:TEMP\ghidra-check
#>
param(
    [Parameter(Mandatory)] [string] $GhidraDir,
    [Parameter(Mandatory)] [string] $NetScopeDll,
    [Parameter(Mandatory)] [string[]] $Targets,
    [Parameter(Mandatory)] [string] $WorkDir,
    [ValidateSet('2', '3')] [string] $PythonMajor = '2'
)

$ErrorActionPreference = 'Stop'
$headless = Join-Path $GhidraDir 'support\analyzeHeadless.bat'
if (-not (Test-Path $headless)) { $headless = Join-Path $GhidraDir 'support/analyzeHeadless' }
$verify = Join-Path $PSScriptRoot 'NsVerify.java'
$utf8 = New-Object System.Text.UTF8Encoding $false
New-Item -ItemType Directory -Force $WorkDir | Out-Null
Copy-Item $verify $WorkDir -Force
$failures = 0

function Invoke-NetScope([string[]] $arguments) {
    & dotnet $NetScopeDll @arguments
    if ($LASTEXITCODE -ne 0) { throw "netscope $($arguments -join ' ') exited with $LASTEXITCODE" }
}

# Runs the given post-scripts in a fresh project; returns the NSVERIFY numbers and the log text.
function Invoke-Ghidra([string] $target, [string] $name, [string[]] $scripts, [string] $expected) {
    $proj = Join-Path $WorkDir 'proj'
    New-Item -ItemType Directory -Force $proj | Out-Null
    $log = Join-Path $WorkDir "$name.log"
    $post = @()
    foreach ($s in $scripts) { $post += @('-postScript', $s) }
    $post += @('-postScript', 'NsVerify.java', $expected)
    & $headless $proj $name -import $target @script:ImportOptions -overwrite -noanalysis -scriptPath $WorkDir @post -deleteProject *> $log
    $text = Get-Content $log -Raw
    $m = [regex]::Match($text, 'NSVERIFY expected=(\d+) ok=(\d+) missing=(\d+) wrong=(\d+) userSymbols=(\d+) bookmarks=(\d+)')
    if (-not $m.Success) { throw "no NSVERIFY line in $log" }
    [pscustomobject]@{
        Expected = [int]$m.Groups[1].Value; Ok = [int]$m.Groups[2].Value
        Missing = [int]$m.Groups[3].Value; Wrong = [int]$m.Groups[4].Value
        Symbols = [int]$m.Groups[5].Value; Bookmarks = [int]$m.Groups[6].Value
        ScriptFailed = $text -match 'NetScope: annotations applied, \d+ failed'
        Python = [regex]::Match($text, 'NetScope: annotations applied.*\[Python (\d+)\.\d+\]').Groups[1].Value
        Log = $log
    }
}

function Assert-Check([bool] $condition, [string] $message) {
    if ($condition) { Write-Host "  ok    $message" }
    else { Write-Host "  FAIL  $message"; $script:failures++ }
}

foreach ($target in $Targets) {
    $target = (Resolve-Path $target).Path
    $base = [IO.Path]::GetFileNameWithoutExtension($target) -replace '[^A-Za-z0-9]', '_'
    Write-Host "== $target"

    # Both languages. The Java file name must match its public class.
    $py = "ns_$base.py"
    $java = "Ns$($base)Annotate.java"
    Invoke-NetScope @('export-ghidra', $target, '--out', (Join-Path $WorkDir $py), '--audit', '-q')
    Invoke-NetScope @('export-ghidra', $target, '--out', (Join-Path $WorkDir $java), '--lang', 'java', '--audit', '-q')

    # 1. Expected functions: every non-funclet native part from --native. A plain body is named Type::Method, an
    #    instantiation by its label. Ghidra forbids whitespace in symbol names, so the export turns it into '_'.
    #    A NativeAOT image has no IL for --native (exit code 3): its names come from the image's stack trace
    #    metadata, which Check-NativeAot.ps1 checks against the linker map; here the exported list is expected.
    $json = Join-Path $WorkDir "$base.native.json"
    $rows = New-Object System.Collections.Generic.List[string]
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns native stderr into a terminating error
    & dotnet $NetScopeDll $target --native --format json --out $json -q 2> $null
    $nativeExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($nativeExit -eq 0) {
        $doc = [IO.File]::ReadAllText($json) | ConvertFrom-Json
        foreach ($m in $doc.assemblies[0].methods) {
            if (-not $m.nativeInfo -or -not $m.nativeInfo.available) { continue }
            foreach ($p in $m.nativeInfo.parts) {
                if ($p.name -like '*funclet*') { continue }
                $name = if ($p.name -eq 'main') { "$($m.type)::$($m.name)" } else { $p.name }
                $rows.Add("$($p.address)`t$($name -replace '[\s\p{Cc}]', '_')")
            }
        }
    }
    elseif ((Get-Content (Join-Path $WorkDir $py) -TotalCount 6) -match 'NativeAOT') {
        # Function records of the script's data table: f \u001f <rva> \u001f <name> \u001f ...
        foreach ($m in Select-String -Path (Join-Path $WorkDir $py) -Pattern '"f\\u001f(\d+)\\u001f((?:[^"\\]|\\.)*?)\\u001f') {
            $rows.Add("$($m.Matches[0].Groups[1].Value)`t$([regex]::Unescape($m.Matches[0].Groups[2].Value))")
        }
    }
    else { throw "netscope --native exited with $nativeExit" }
    $expected = Join-Path $WorkDir "$base.expected.tsv"
    [IO.File]::WriteAllLines($expected, $rows, $utf8)
    Write-Host "  $($rows.Count) native function(s) expected"

    # TZ-3 §4.2: an R2R image for Linux or macOS is imported with the options the script header documents
    # (the file itself is never changed). For Windows images the header has no such line.
    $script:ImportOptions = @()
    $line = Select-String -Path (Join-Path $WorkDir $py) -Pattern '^# Ghidra import   : (-loader .+)$' | Select-Object -First 1
    if ($line) {
        $script:ImportOptions = $line.Matches[0].Groups[1].Value -split ' '
        Write-Host "  import options from the script header: $($script:ImportOptions -join ' ')"
    }
    $hash = (Get-FileHash $target -Algorithm SHA256).Hash

    foreach ($script in @($py, $java)) {
        $r = Invoke-Ghidra $target "$base-$([IO.Path]::GetExtension($script).TrimStart('.'))" @($script) $expected
        Assert-Check ($r.Expected -eq $rows.Count -and $r.Ok -eq $rows.Count) "$script names $($r.Ok)/$($rows.Count) functions at the --native RVAs (missing $($r.Missing), wrong $($r.Wrong))"
        Assert-Check (-not $r.ScriptFailed) "$script applied every annotation"
        if ($script -eq $py)
        {
            Assert-Check ($r.Python -eq $PythonMajor) "$script ran in Python $PythonMajor (reported: '$($r.Python)')"
            $once = $r
        }
    }

    # 4. Idempotency: a second run in the same session must not add anything.
    $twice = Invoke-Ghidra $target "$base-twice" @($py, $py) $expected
    Assert-Check ($twice.Symbols -eq $once.Symbols -and $twice.Bookmarks -eq $once.Bookmarks) "running $py twice keeps $($once.Symbols) user symbols and $($once.Bookmarks) bookmarks (got $($twice.Symbols), $($twice.Bookmarks))"
    Assert-Check ((Get-FileHash $target -Algorithm SHA256).Hash -eq $hash) "the analyzed file is unchanged (TZ-2 section 5.1)"
}

if ($failures -gt 0) { Write-Host "$failures check(s) failed"; exit 1 }
Write-Host 'all Ghidra checks passed'
