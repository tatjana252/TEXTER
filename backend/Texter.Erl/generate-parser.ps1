# Regenerates Generated/*.cs from Grammar/ERLD.g4 (+ ERLCommon.g4).
# Run only after changing the grammar. Needs Java 11+; the ANTLR tool is downloaded on first use.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = '4.13.1'   # must match the Antlr4.Runtime.Standard package version
$jar = "tools/antlr-$version-complete.jar"
if (-not (Test-Path $jar)) {
    New-Item -ItemType Directory -Force tools | Out-Null
    Invoke-WebRequest "https://repo1.maven.org/maven2/org/antlr/antlr4/$version/antlr4-$version-complete.jar" -OutFile $jar
}

Remove-Item -Recurse -Force Generated -ErrorAction SilentlyContinue
java -jar $jar -Dlanguage=CSharp -visitor -no-listener -package Texter.Erl.Grammar -lib Grammar -o Generated -Xexact-output-dir Grammar/ERLD.g4
Remove-Item Generated/*.interp, Generated/*.tokens
Write-Host 'Parser regenerated in Generated/'
