using Antlr4.Runtime;
using Texter.Erl.Grammar;

namespace Texter.Erl
{
    /// <summary>Parses ERLD source and reports syntax errors.</summary>
    public static class ErldSyntax
    {
        public static (ERLDParser.ErldFileContext Tree, List<ErlSyntaxError> Errors) Parse(string source)
        {
            var errors = new List<ErlSyntaxError>();
            var listener = new ErrorCollector(errors);

            var lexer = new ERLDLexer(new AntlrInputStream(source));
            lexer.RemoveErrorListeners();
            lexer.AddErrorListener(listener);

            var parser = new ERLDParser(new CommonTokenStream(lexer));
            parser.RemoveErrorListeners();
            parser.AddErrorListener(listener);

            return (parser.erldFile(), errors);
        }

        public static List<ErlSyntaxError> Validate(string source) => Parse(source).Errors;

        private sealed class ErrorCollector(List<ErlSyntaxError> errors) : IAntlrErrorListener<IToken>, IAntlrErrorListener<int>
        {
            public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
                => errors.Add(new ErlSyntaxError(line, charPositionInLine + 1, msg));

            public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
                => errors.Add(new ErlSyntaxError(line, charPositionInLine + 1, msg));
        }
    }
}
