using System.Text;
using System.Text.RegularExpressions;
using Texter.Model.Conceptual;

namespace Texter.Logic.Traceability
{
    /// <summary>
    /// Finds where an LLM evidence quote is in the requirements text.
    /// LLMs do not always copy verbatim, so matching is done in three increasingly tolerant passes:
    /// exact -> normalized (case, whitespace, typographic quotes) -> best matching sentence by word overlap.
    /// </summary>
    public class EvidenceLocator
    {
        private const double MinSentenceOverlap = 0.7;

        private readonly string _text;
        private readonly string _normalized;
        private readonly List<int> _normalizedToOriginal;
        private readonly List<TextSpan> _sentences;

        public EvidenceLocator(string text)
        {
            _text = text;
            (_normalized, _normalizedToOriginal) = Normalize(text);
            _sentences = SplitSentences(text);
        }

        public TextSpan? Locate(string quote)
        {
            if (string.IsNullOrWhiteSpace(quote)) return null;
            quote = quote.Trim();

            var exact = _text.IndexOf(quote, StringComparison.Ordinal);
            if (exact >= 0) return new TextSpan(exact, quote.Length);

            var normalizedQuote = Normalize(quote).Text;
            var index = _normalized.IndexOf(normalizedQuote, StringComparison.Ordinal);
            if (index >= 0)
            {
                var start = _normalizedToOriginal[index];
                var end = _normalizedToOriginal[index + normalizedQuote.Length - 1] + 1;
                return new TextSpan(start, end - start);
            }

            return BestSentence(quote);
        }

        public List<TextSpan> LocateAll(IEnumerable<string> quotes) =>
            quotes.Select(Locate).OfType<TextSpan>().Distinct().ToList();

        private TextSpan? BestSentence(string quote)
        {
            var quoteWords = Words(quote);
            if (quoteWords.Count == 0) return null;

            TextSpan? best = null;
            var bestScore = 0.0;
            foreach (var sentence in _sentences)
            {
                var sentenceWords = Words(_text.Substring(sentence.Start, sentence.Length));
                var score = (double)quoteWords.Count(sentenceWords.Contains) / quoteWords.Count;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = sentence;
                }
            }
            return bestScore >= MinSentenceOverlap ? best : null;
        }

        /// <summary>Lowercases, unifies quotes/dashes and collapses whitespace, keeping a map back to original indices.</summary>
        private static (string Text, List<int> Map) Normalize(string input)
        {
            var builder = new StringBuilder(input.Length);
            var map = new List<int>(input.Length);
            var previousWasSpace = true;

            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (char.IsWhiteSpace(c))
                {
                    if (previousWasSpace) continue;
                    c = ' ';
                    previousWasSpace = true;
                }
                else
                {
                    previousWasSpace = false;
                    c = c switch
                    {
                        '‘' or '’' or '`' => '\'',
                        '“' or '”' => '"',
                        '–' or '—' => '-',
                        _ => char.ToLowerInvariant(c)
                    };
                }
                builder.Append(c);
                map.Add(i);
            }

            if (builder.Length > 0 && builder[^1] == ' ')
            {
                builder.Length--;
                map.RemoveAt(map.Count - 1);
            }
            return (builder.ToString(), map);
        }

        private static List<TextSpan> SplitSentences(string text) =>
            Regex.Matches(text, @"[^.!?\r\n]+[.!?]?")
                .Where(m => !string.IsNullOrWhiteSpace(m.Value))
                .Select(m =>
                {
                    var leading = m.Value.Length - m.Value.TrimStart().Length;
                    return new TextSpan(m.Index + leading, m.Value.Trim().Length);
                })
                .ToList();

        private static HashSet<string> Words(string text) =>
            Regex.Matches(text.ToLowerInvariant(), @"\p{L}[\p{L}\p{N}]*")
                .Select(m => m.Value)
                .ToHashSet();
    }
}
