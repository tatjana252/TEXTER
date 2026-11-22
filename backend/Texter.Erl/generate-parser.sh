#!/usr/bin/env bash
# Regenerates Generated/*.cs from Grammar/ERLD.g4 (+ ERLCommon.g4).
# Run only after changing the grammar. Needs Java 11+; the ANTLR tool is downloaded on first use.
set -euo pipefail
cd "$(dirname "$0")"

version=4.13.1   # must match the Antlr4.Runtime.Standard package version
jar="tools/antlr-$version-complete.jar"
if [ ! -f "$jar" ]; then
  mkdir -p tools
  curl -fsSLo "$jar" "https://repo1.maven.org/maven2/org/antlr/antlr4/$version/antlr4-$version-complete.jar"
fi

rm -rf Generated
java -jar "$jar" -Dlanguage=CSharp -visitor -no-listener -package Texter.Erl.Grammar -lib Grammar -o Generated -Xexact-output-dir Grammar/ERLD.g4
rm -f Generated/*.interp Generated/*.tokens
echo "Parser regenerated in Generated/"
