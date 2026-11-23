using Antlr4.Runtime;
using Texter.Erl.Grammar;

namespace Texter.Erl
{
    /// <summary>Parses an ERLQ query and reports syntax errors.</summary>
    public static class ErlqSyntax
    {
        public static (ERLQParser.ErlqFileContext Tree, List<ErlSyntaxError> Errors) Parse(string source)
        {
            var errors = new List<ErlSyntaxError>();
            var listener = new ErrorCollector(errors);

            var lexer = new ERLQLexer(new AntlrInputStream(source));
            lexer.RemoveErrorListeners();
            lexer.AddErrorListener(listener);

            var parser = new ERLQParser(new CommonTokenStream(lexer));
            parser.RemoveErrorListeners();
            parser.AddErrorListener(listener);

            return (parser.erlqFile(), errors);
        }

        private sealed class ErrorCollector(List<ErlSyntaxError> errors) : IAntlrErrorListener<IToken>, IAntlrErrorListener<int>
        {
            public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
                => errors.Add(new ErlSyntaxError(line, charPositionInLine + 1, msg));

            public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
                => errors.Add(new ErlSyntaxError(line, charPositionInLine + 1, msg));
        }
    }
}
