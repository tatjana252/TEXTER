using System.Globalization;
using Texter.Erl.Grammar;
using Texter.Erl.Semantics;

namespace Texter.Erl.Ddl
{
    /// <summary>
    /// ERL expression -> T-SQL scalar expression, for what a table can check on its own row:
    /// literals, attributes of the row, arithmetic, date + duration, comparisons, logic, if-then-else and a few
    /// functions. Navigation, filters, quantifiers and aggregates need other rows (a trigger): null is returned.
    /// </summary>
    public static class SqlExpression
    {
        /// <param name="column">Attribute name -> quoted column, or null when it is not a column of this row.</param>
        public static string? Translate(ERLDParser.ExpressionContext expression, Func<string, string?> column) =>
            new Translator(column).Expr(expression);

        /// <summary>A value constraint (between, like, in, compare, check(...)) applied to one column.</summary>
        public static string? Translate(ERLDParser.ValueConstraintContext constraint, string valueSql, Func<string, string?> column)
        {
            var t = new Translator(name => ErldModel.Same(name, "value") ? valueSql : column(name));
            string? Not(ERLDParser.ValueConstraintContext c) => c.GetChild(0).GetText().Equals("not", StringComparison.OrdinalIgnoreCase) ? "NOT " : "";
            return constraint switch
            {
                ERLDParser.BetweenConstraintContext b => Join(t.Expr(b.expression(0)), t.Expr(b.expression(1)),
                    (lo, hi) => $"{valueSql} {Not(b)}BETWEEN {lo} AND {hi}"),
                ERLDParser.LikeConstraintContext l => $"{valueSql} {Not(l)}LIKE {StringLiteral(l.STRING().GetText())}",
                ERLDParser.InConstraintContext i => t.List(i.expressionList()) is { } list ? $"{valueSql} {Not(i)}IN ({list})" : null,
                ERLDParser.CompareConstraintContext c => t.Expr(c.expression()) is { } right ? $"{valueSql} {Op(c.compOp().GetText())} {right}" : null,
                ERLDParser.CheckConstraintContext ch => t.Expr(ch.expression()),
                _ => null
            };
        }

        public static string StringLiteral(string erlLiteral) => "N'" + ErldModelBuilder.Unquote(erlLiteral).Replace("'", "''") + "'";

        public static string Literal(ERLDParser.LiteralContext literal) => literal switch
        {
            _ when literal.STRING() is { } s => StringLiteral(s.GetText()),
            _ when literal.TRUE() is not null => "1",
            _ when literal.FALSE() is not null => "0",
            _ when literal.NULL() is not null => "NULL",
            _ => literal.GetText()
        };

        private static string Op(string op) => op == "!=" ? "<>" : op;

        private static string? Join(string? a, string? b, Func<string, string, string> f) => a is null || b is null ? null : f(a, b);

        private sealed class Translator(Func<string, string?> column)
        {
            public string? List(ERLDParser.ExpressionListContext list)
            {
                var items = list.expression().Select(Expr).ToList();
                return items.Any(i => i is null) ? null : string.Join(", ", items);
            }

            public string? Expr(ERLDParser.ExpressionContext e) => e switch
            {
                ERLDParser.LiteralExprContext l => Literal(l.literal()),
                ERLDParser.NameExprContext n => column(ErldModelBuilder.Name(n.name())),
                ERLDParser.ParenExprContext p => p.expressionList().expression().Length == 1 && Expr(p.expressionList().expression(0)) is { } inner ? $"({inner})" : null,
                ERLDParser.NegateExprContext n => Expr(n.expression()) is { } v ? $"-{v}" : null,
                ERLDParser.MulExprContext m => Join(Expr(m.expression(0)), Expr(m.expression(1)), (a, b) => $"{a} {m.op.Text} {b}"),
                ERLDParser.AddExprContext a => Add(a),
                ERLDParser.CompareExprContext c => Join(Expr(c.expression(0)), Expr(c.expression(1)), (a, b) => $"{a} {Op(c.compOp().GetText())} {b}"),
                ERLDParser.IsNullExprContext i => Expr(i.expression()) is { } v ? $"{v} IS {(i.NOT() is not null ? "NOT " : "")}NULL" : null,
                ERLDParser.InExprContext i => In(i),
                ERLDParser.BetweenExprContext b => Expr(b.expression(0)) is { } v && Expr(b.expression(1)) is { } lo && Expr(b.expression(2)) is { } hi
                    ? $"{v} {(b.NOT() is not null ? "NOT " : "")}BETWEEN {lo} AND {hi}" : null,
                ERLDParser.LikeExprContext l => Join(Expr(l.expression(0)), Expr(l.expression(1)), (a, b) => $"{a} {(l.NOT() is not null ? "NOT " : "")}LIKE {b}"),
                ERLDParser.NotExprContext n => Expr(n.expression()) is { } v ? $"NOT ({v})" : null,
                ERLDParser.AndExprContext a => Join(Expr(a.expression(0)), Expr(a.expression(1)), (x, y) => $"({x}) AND ({y})"),
                ERLDParser.OrExprContext o => Join(Expr(o.expression(0)), Expr(o.expression(1)), (x, y) => $"({x}) OR ({y})"),
                ERLDParser.ImpliesExprContext i => Join(Expr(i.expression(0)), Expr(i.expression(1)), (x, y) => $"NOT ({x}) OR ({y})"),
                ERLDParser.IfExprContext i => If(i),
                ERLDParser.CallExprContext c => Call(c),
                _ => null // member, filter, quantifiers, for each, bare durations: need other rows
            };

            /// <summary>date + 15 days -> DATEADD(day, 15, date).</summary>
            private string? Add(ERLDParser.AddExprContext a)
            {
                if (a.expression(1) is ERLDParser.DurationExprContext d && Expr(a.expression(0)) is { } date)
                {
                    var amount = int.Parse(d.INT().GetText(), CultureInfo.InvariantCulture) * (a.op.Text == "-" ? -1 : 1);
                    return $"DATEADD({Unit(d.durationUnit().GetText())}, {amount}, {date})";
                }
                return Join(Expr(a.expression(0)), Expr(a.expression(1)), (x, y) => $"{x} {a.op.Text} {y}");
            }

            private static string Unit(string unit) => unit.ToLowerInvariant().TrimEnd('s') switch
            {
                "month" => "month",
                "year" => "year",
                "hour" => "hour",
                "minute" => "minute",
                _ => "day"
            };

            private string? In(ERLDParser.InExprContext i)
            {
                var not = i.NOT() is not null ? "NOT " : "";
                if (Expr(i.expression(0)) is not { } v) return null;
                // x in (1, 2, 3): the right side is a parenthesised list.
                if (i.expression(1) is ERLDParser.ParenExprContext p && List(p.expressionList()) is { } list) return $"{v} {not}IN ({list})";
                return null; // x in r.projekti: navigation
            }

            private string? If(ERLDParser.IfExprContext i)
            {
                var parts = i.expression().Select(Expr).ToList();
                if (parts.Any(p => p is null)) return null;
                return parts.Count == 3
                    ? $"CASE WHEN {parts[0]} THEN {parts[1]} ELSE {parts[2]} END"
                    : $"CASE WHEN {parts[0]} THEN {parts[1]} END";
            }

            private string? Call(ERLDParser.CallExprContext c)
            {
                var name = ErldModelBuilder.Name(c.name()).ToLowerInvariant();
                var args = c.expressionList()?.expression().Select(Expr).ToList() ?? new List<string?>();
                if (args.Any(a => a is null)) return null;
                return (name, args.Count) switch
                {
                    ("today", 0) => "CAST(GETDATE() AS date)",
                    ("now", 0) => "SYSDATETIME()",
                    ("abs", 1) => $"ABS({args[0]})",
                    ("round", 1) => $"ROUND({args[0]}, 0)",
                    ("round", 2) => $"ROUND({args[0]}, {args[1]})",
                    ("floor", 1) => $"FLOOR({args[0]})",
                    ("ceiling", 1) or ("ceil", 1) => $"CEILING({args[0]})",
                    ("len", 1) or ("length", 1) => $"LEN({args[0]})",
                    ("upper", 1) => $"UPPER({args[0]})",
                    ("lower", 1) => $"LOWER({args[0]})",
                    ("trim", 1) => $"TRIM({args[0]})",
                    ("year", 1) => $"YEAR({args[0]})",
                    ("month", 1) => $"MONTH({args[0]})",
                    ("day", 1) => $"DAY({args[0]})",
                    ("coalesce", > 0) => $"COALESCE({string.Join(", ", args)})",
                    _ => null // matches (regex), card, sum, count, ...: not in a CHECK
                };
            }
        }
    }
}
