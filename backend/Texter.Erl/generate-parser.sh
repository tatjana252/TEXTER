#!/usr/bin/env bash
# Regenerates Generated/*.cs from Grammar/ERLD.g4 and Grammar/ERLQ.g4 (both import ERLCommon.g4).
# Run only after changing a grammar. Needs Java 11+; the ANTLR tool is downloaded on first use.
set -euo pipefail
cd "$(dirname "$0")"

version=4.13.1   # must match the Antlr4.Runtime.Standard package version
jar="tools/antlr-$version-complete.jar"
if [ ! -f "$jar" ]; then
  mkdir -p tools
  curl -fsSLo "$jar" "https://repo1.maven.org/maven2/org/antlr/antlr4/$version/antlr4-$version-complete.jar"
fi

rm -rf Generated
for grammar in ERLD ERLQ; do
  java -jar "$jar" -encoding UTF-8 -Dlanguage=CSharp -visitor -no-listener -package Texter.Erl.Grammar -lib Grammar -o Generated -Xexact-output-dir "Grammar/$grammar.g4"
done
rm -f Generated/*.interp Generated/*.tokens
echo "Parsers regenerated in Generated/"
