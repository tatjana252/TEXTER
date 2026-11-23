# Regenerates Generated/*.cs from Grammar/ERLD.g4 and Grammar/ERLQ.g4 (both import ERLCommon.g4).
# Run only after changing a grammar. Needs Java 11+; the ANTLR tool is downloaded on first use.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = '4.13.1'   # must match the Antlr4.Runtime.Standard package version
$jar = "tools/antlr-$version-complete.jar"
if (-not (Test-Path $jar)) {
    New-Item -ItemType Directory -Force tools | Out-Null
    Invoke-WebRequest "https://repo1.maven.org/maven2/org/antlr/antlr4/$version/antlr4-$version-complete.jar" -OutFile $jar
}

Remove-Item -Recurse -Force Generated -ErrorAction SilentlyContinue
foreach ($grammar in 'ERLD', 'ERLQ') {
    java -jar $jar -encoding UTF-8 -Dlanguage=CSharp -visitor -no-listener -package Texter.Erl.Grammar -lib Grammar -o Generated -Xexact-output-dir "Grammar/$grammar.g4"
    if ($LASTEXITCODE -ne 0) { throw "ANTLR failed for $grammar.g4" }
}
Remove-Item Generated/*.interp, Generated/*.tokens
Write-Host 'Parsers regenerated in Generated/'
