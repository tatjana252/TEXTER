using System.Globalization;
using System.Text;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Texter.Erl.Ddl;
using Texter.Erl.Grammar;
using Texter.Erl.Semantics;

namespace Texter.Erl.Query
{
    /// <param name="Sql">T-SQL SELECT, or null when the query has errors.</param>
    /// <param name="Columns">Result column headers, in order.</param>
    public record ErlqTranslation(string? Sql, List<string> Columns, List<ErlSyntaxError> Errors);

    /// <summary>
    /// TEXTER step 4: ERLQ query over the ERLD model -> T-SQL over the schema of step 3.
    ///
    ///  - a range "r in Radnik" is a table in FROM; "i in r.isplate" a JOIN correlated to r;
    ///  - single-valued navigation (r.sluzba.naziv) adds LEFT JOINs, following the relationship's storage:
    ///    foreign key on either side, junction table or aggregation table;
    ///  - a collection (r.isplate, Radnik[...], r.isplate.iznos) is only consumed by card / count / sum / avg / min /
    ///    max, exists / forall, "x in c" and ranges; each becomes a correlated subquery, a filter [...] its WHERE;
    ///  - an aggregate over a range variable or a value (not a collection) aggregates the rows of the query (group by);
    ///  - a subtype selects its rows by the discriminator of its hierarchy's table.
    /// The short form (a path such as Radnik[sluzba.naziv = 'IT'].ime) is the collection put into FROM.
    /// </summary>
    public static class ErlqTranslator
    {
        public static ErlqTranslation Translate(string source, ErldModel model, RelationalMap map)
        {
            var (tree, errors) = ErlqSyntax.Parse(source);
            if (errors.Count > 0) return new ErlqTranslation(null, new(), errors);
            return new Translator(model, map).Translate(tree);
        }

        // ------------------------------------------------------------------ values

        private abstract record Val;

        /// <summary>A scalar SQL expression.</summary>
        private sealed record Scalar(string Sql, bool IsGroupAggregate = false) : Val;

        /// <summary>One instance of a type: a table alias in a query.</summary>
        private sealed record EntityRef(TypeDef Type, string Alias, SelectQuery Owner) : Val;

        /// <summary>A composite attribute, waiting for one of its parts.</summary>
        private sealed record CompositeRef(AttributeDef Attribute, string Alias) : Val;

        /// <summary>
        /// A collection: Apply adds its tables to a query (FROM when the query is empty, otherwise JOIN, correlated
        /// to the instance it starts from) and returns the element, an EntityRef or a Scalar of that query.
        /// </summary>
        private sealed record Many(Func<SelectQuery, Val> Apply) : Val;

        private sealed class SelectQuery
        {
            public string? From { get; private set; }
            public List<string> Joins { get; } = new();
            public List<string> Where { get; } = new();

            /// <summary>LEFT JOINs of single-valued navigation, so that r.sluzba is joined once.</summary>
            public Dictionary<(string Alias, object Step), EntityRef> Navigations { get; } = new();

            public void Add(string table, string alias, IReadOnlyList<string> on, bool left)
            {
                if (From is null)
                {
                    From = $"{Q(table)} AS {alias}";
                    Where.AddRange(on);
                }
                else if (on.Count == 0)
                    Joins.Add($"CROSS JOIN {Q(table)} AS {alias}");
                else
                    Joins.Add($"{(left ? "LEFT JOIN" : "JOIN")} {Q(table)} AS {alias} ON {string.Join(" AND ", on)}");
            }

            public string FromWhere(string indent)
            {
                var sb = new StringBuilder();
                if (From is not null) sb.Append($"{indent}FROM {From}");
                foreach (var j in Joins) sb.Append($"\n{indent}  {j}");
                if (Where.Count > 0) sb.Append($"\n{indent}WHERE {string.Join($"\n{indent}  AND ", Where.Select(Paren))}");
                return sb.ToString();
            }

            /// <summary>Single-line form for subqueries.</summary>
            public string Inline()
            {
                var parts = new List<string>();
                if (From is not null) parts.Add($"FROM {From}");
                parts.AddRange(Joins);
                if (Where.Count > 0) parts.Add("WHERE " + string.Join(" AND ", Where.Select(Paren)));
                return string.Join(" ", parts);
            }
        }

        /// <summary>Names visible in an expression: variables, and the element a filter is applied to.</summary>
        private sealed class Scope(SelectQuery query, Scope? parent = null)
        {
            public SelectQuery Query { get; } = query;
            public Scope? Parent { get; } = parent;
            public Dictionary<string, Val> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Val? Implicit { get; init; }

            public Val? Variable(string name) =>
                Variables.TryGetValue(name, out var v) ? v : Parent?.Variable(name);
        }

        private sealed class QueryError(ParserRuleContext at, string message) : Exception(message)
        {
            public ParserRuleContext At { get; } = at;
        }

        // ------------------------------------------------------------------ translator

        private sealed class Translator(ErldModel model, RelationalMap map)
        {
            private int _aliases;
            private bool _usesGroupAggregate;

            public ErlqTranslation Translate(ERLQParser.ErlqFileContext file)
            {
                try
                {
                    var main = new SelectQuery();
                    var scope = new Scope(main);
                    var (items, distinct, limit, groupBy, having, orderBy) = file.query() switch
                    {
                        ERLQParser.LongFormContext l => Long(l.longQuery(), scope),
                        ERLQParser.ShortFormContext s => Short(s.expression(), scope),
                        _ => throw new InvalidOperationException()
                    };

                    var sb = new StringBuilder("SELECT ");
                    if (distinct) sb.Append("DISTINCT ");
                    if (limit is { } n) sb.Append($"TOP ({n}) ");
                    sb.Append(string.Join(",\n       ", items.Select(i => $"{i.Sql} AS {Q(i.Header)}")));
                    if (main.From is not null) sb.Append('\n').Append(main.FromWhere(""));
                    if (groupBy.Count > 0) sb.Append("\nGROUP BY ").Append(string.Join(", ", groupBy));
                    if (having is not null) sb.Append("\nHAVING ").Append(having);
                    if (orderBy.Count > 0) sb.Append("\nORDER BY ").Append(string.Join(", ", orderBy));
                    return new ErlqTranslation(sb.ToString(), items.Select(i => i.Header).ToList(), new());
                }
                catch (QueryError e)
                {
                    return new ErlqTranslation(null, new(), new() { new ErlSyntaxError(e.At.Start.Line, e.At.Start.Column + 1, e.Message) });
                }
            }

            private record SelectItem(string Sql, string Header);

            private (List<SelectItem>, bool, int?, List<string>, string?, List<string>) Long(ERLQParser.LongQueryContext q, Scope scope)
            {
                foreach (var range in q.rangeDecl())
                {
                    var name = N(range.name());
                    if (scope.Variables.ContainsKey(name)) throw new QueryError(range, $"Variable '{name}' is already declared.");
                    var collection = Eval(range.expression(), scope) as Many
                        ?? throw new QueryError(range.expression(), $"'{Text(range.expression())}' is not a collection to range over (an entity, a role to many, or a filter).");
                    scope.Variables[name] = collection.Apply(scope.Query);
                }

                // expression(): the where condition (if any) comes first, the having condition (if any) last.
                if (q.WHERE() is not null) scope.Query.Where.Add(Bool(q.expression(0), scope));

                var groupBy = new List<string>();
                if (q.GROUP() is not null)
                    foreach (var e in q.expressionList().expression())
                        groupBy.AddRange(Columns(Eval(e, scope), e).Select(c => c.Sql));
                var having = q.HAVING() is not null ? Bool(q.expression().Last(), scope) : null;

                var items = new List<SelectItem>();
                foreach (var item in q.selectItem())
                {
                    var header = item.name() is { } alias ? N(alias) : null;
                    var columns = Columns(Eval(item.expression(), scope), item.expression());
                    items.AddRange(columns.Count == 1 && header is not null ? new[] { columns[0] with { Header = header } } : columns);
                }

                var orderBy = new List<string>();
                foreach (var o in q.orderItem())
                {
                    var direction = o.DESC() is not null ? " DESC" : "";
                    var alias = o.expression() is ERLQParser.NameExprContext n && scope.Variable(N(n.name())) is null
                        ? items.FirstOrDefault(i => ErldModel.Same(i.Header, N(n.name())))
                        : null;
                    if (alias is not null) orderBy.Add(Q(alias.Header) + direction);
                    else orderBy.AddRange(Columns(Eval(o.expression(), scope), o.expression()).Select(c => c.Sql + direction));
                }

                if (_usesGroupAggregate && groupBy.Count == 0 && items.Any(i => !LooksAggregated(i.Sql)))
                    throw new QueryError(q, "The select list mixes aggregates over rows (count(r), sum(i.iznos)) with plain values: add group by, or aggregate a collection (card(r.isplate)).");

                int? limit = q.LIMIT() is not null ? int.Parse(q.INT().GetText(), CultureInfo.InvariantCulture) : null;
                return (items, q.DISTINCT() is not null, limit, groupBy, having, orderBy);
            }

            private (List<SelectItem>, bool, int?, List<string>, string?, List<string>) Short(ERLQParser.ExpressionContext e, Scope scope)
            {
                var value = Eval(e, scope);
                var element = value is Many many ? many.Apply(scope.Query) : value;
                var columns = Columns(element, e);
                return (columns, false, null, new(), null, new());
            }

            private static bool LooksAggregated(string sql) =>
                sql.StartsWith("COUNT(") || sql.StartsWith("SUM(") || sql.StartsWith("AVG(") || sql.StartsWith("MIN(") || sql.StartsWith("MAX(") || sql.StartsWith("(SELECT");

            /// <summary>Output columns of a value: a scalar, or all attributes of an instance (an entity in select).</summary>
            private List<SelectItem> Columns(Val value, ERLQParser.ExpressionContext at)
            {
                var text = Text(at);
                switch (value)
                {
                    case Scalar s:
                        return new() { new SelectItem(s.Sql, text) };
                    case EntityRef e:
                        return Attributes(e.Type)
                            .SelectMany(a => a.IsComposite ? a.Parts.Select(p => (Attr: p, Name: $"{a.Name}.{p.Name}")) : new[] { (Attr: a, Name: a.Name) })
                            .Where(x => map.Columns.ContainsKey(x.Attr))
                            .Select(x => new SelectItem($"{e.Alias}.{Q(map.Columns[x.Attr])}", $"{text}.{x.Name}"))
                            .ToList();
                    case CompositeRef c:
                        return c.Attribute.Parts.Where(map.Columns.ContainsKey)
                            .Select(p => new SelectItem($"{c.Alias}.{Q(map.Columns[p])}", $"{text}.{p.Name}")).ToList();
                    default:
                        throw new QueryError(at, $"'{text}' is a collection; use card(...), sum(...), exists ... or a range variable in from.");
                }
            }

            // -------------------------------------------------------------- expressions

            private string Bool(ERLQParser.ExpressionContext e, Scope scope) => ScalarOf(Eval(e, scope), e);

            private string ScalarOf(Val v, ERLQParser.ExpressionContext at) => v switch
            {
                Scalar s => s.Sql,
                EntityRef e => $"{e.Alias}.{Q(Storage(e.Type).Key[0])}",
                _ => throw new QueryError(at, $"'{Text(at)}' is {(v is Many ? "a collection" : "not a single value")} here; aggregate it (card, sum, ...) or use exists / forall.")
            };

            private Val Eval(ERLQParser.ExpressionContext e, Scope scope)
            {
                switch (e)
                {
                    case ERLQParser.LiteralExprContext l: return new Scalar(Literal(l.literal()));
                    case ERLQParser.NameExprContext n: return Name(N(n.name()), n, scope);
                    case ERLQParser.ParenExprContext p:
                        if (p.expressionList().expression().Length != 1) throw new QueryError(p, "A list (a, b, c) is only allowed after in.");
                        return Eval(p.expressionList().expression(0), scope) is Scalar inner ? new Scalar($"({inner.Sql})") : Eval(p.expressionList().expression(0), scope);
                    case ERLQParser.MemberExprContext m: return Member(Eval(m.expression(), scope), N(m.name()), m);
                    case ERLQParser.FilterExprContext f: return Filter(Eval(f.expression(0), scope), f.expression(1), scope, f);
                    case ERLQParser.NegateExprContext n: return new Scalar($"-{Num(n.expression(), scope)}");
                    case ERLQParser.MulExprContext m: return new Scalar($"{Num(m.expression(0), scope)} {m.op.Text} {Num(m.expression(1), scope)}");
                    case ERLQParser.AddExprContext a:
                        if (a.expression(1) is ERLQParser.DurationExprContext d)
                        {
                            var amount = int.Parse(d.INT().GetText(), CultureInfo.InvariantCulture) * (a.op.Text == "-" ? -1 : 1);
                            return new Scalar($"DATEADD({Unit(d.durationUnit().GetText())}, {amount}, {Num(a.expression(0), scope)})");
                        }
                        return new Scalar($"{Num(a.expression(0), scope)} {a.op.Text} {Num(a.expression(1), scope)}");
                    case ERLQParser.CompareExprContext c: return Compare(c, scope);
                    case ERLQParser.IsNullExprContext i:
                        return new Scalar($"{ScalarOf(Eval(i.expression(), scope), i.expression())} IS {(i.NOT() is not null ? "NOT " : "")}NULL");
                    case ERLQParser.InExprContext i: return In(i, scope);
                    case ERLQParser.BetweenExprContext b:
                        return new Scalar($"{Num(b.expression(0), scope)} {(b.NOT() is not null ? "NOT " : "")}BETWEEN {Num(b.expression(1), scope)} AND {Num(b.expression(2), scope)}");
                    case ERLQParser.LikeExprContext l:
                        return new Scalar($"{Num(l.expression(0), scope)} {(l.NOT() is not null ? "NOT " : "")}LIKE {Num(l.expression(1), scope)}");
                    case ERLQParser.NotExprContext n: return new Scalar($"NOT ({Bool(n.expression(), scope)})");
                    case ERLQParser.AndExprContext a: return new Scalar($"({Bool(a.expression(0), scope)}) AND ({Bool(a.expression(1), scope)})");
                    case ERLQParser.OrExprContext o: return new Scalar($"({Bool(o.expression(0), scope)}) OR ({Bool(o.expression(1), scope)})");
                    case ERLQParser.ImpliesExprContext i: return new Scalar($"NOT ({Bool(i.expression(0), scope)}) OR ({Bool(i.expression(1), scope)})");
                    case ERLQParser.IfExprContext i:
                        var parts = i.expression().Select(x => ScalarOf(Eval(x, scope), x)).ToList();
                        return new Scalar(parts.Count == 3 ? $"CASE WHEN {parts[0]} THEN {parts[1]} ELSE {parts[2]} END" : $"CASE WHEN {parts[0]} THEN {parts[1]} END");
                    case ERLQParser.QuantifiedExprContext q: return Quantified(q, scope);
                    case ERLQParser.CallExprContext c: return Call(c, scope);
                    case ERLQParser.DurationExprContext dur: throw new QueryError(dur, "A duration (15 days) can only be added to or subtracted from a date.");
                    case ERLQParser.ForEachExprContext f: throw new QueryError(f, "for each belongs to ERLD constraints; in a query use from ... in ....");
                    default: throw new QueryError(e, $"Unsupported expression '{Text(e)}'.");
                }
            }

            private string Num(ERLQParser.ExpressionContext e, Scope scope) => ScalarOf(Eval(e, scope), e);

            private Val Compare(ERLQParser.CompareExprContext c, Scope scope)
            {
                var op = c.compOp().GetText() == "!=" ? "<>" : c.compOp().GetText();
                var (left, right) = (Eval(c.expression(0), scope), Eval(c.expression(1), scope));
                // Instances are compared by key.
                if (left is EntityRef a && right is EntityRef b && op is "=" or "<>")
                {
                    var keys = Storage(a.Type).Key.Zip(Storage(b.Type).Key).Select(k => $"{a.Alias}.{Q(k.First)} = {b.Alias}.{Q(k.Second)}");
                    var equal = string.Join(" AND ", keys);
                    return new Scalar(op == "=" ? equal : $"NOT ({equal})");
                }
                return new Scalar($"{ScalarOf(left, c.expression(0))} {op} {ScalarOf(right, c.expression(1))}");
            }

            private Val In(ERLQParser.InExprContext i, Scope scope)
            {
                var not = i.NOT() is not null;
                var value = Eval(i.expression(0), scope);
                if (i.expression(1) is ERLQParser.ParenExprContext p)
                {
                    var list = p.expressionList().expression().Select(x => ScalarOf(Eval(x, scope), x));
                    return new Scalar($"{ScalarOf(value, i.expression(0))} {(not ? "NOT " : "")}IN ({string.Join(", ", list)})");
                }
                var collection = Eval(i.expression(1), scope) as Many
                    ?? throw new QueryError(i.expression(1), $"'{Text(i.expression(1))}' is not a collection.");
                var sub = new SelectQuery();
                var element = collection.Apply(sub);
                sub.Where.Add(Equal(element, value, i));
                return new Scalar($"{(not ? "NOT " : "")}EXISTS (SELECT 1 {sub.Inline()})");
            }

            private string Equal(Val element, Val value, ERLQParser.InExprContext at)
            {
                if (element is EntityRef a && value is EntityRef b)
                    return string.Join(" AND ", Storage(a.Type).Key.Zip(Storage(b.Type).Key).Select(k => $"{a.Alias}.{Q(k.First)} = {b.Alias}.{Q(k.Second)}"));
                return $"{ScalarOf(element, at.expression(1))} = {ScalarOf(value, at.expression(0))}";
            }

            /// <summary>exists x in c : p -> EXISTS (... AND p); forall x in c : p -> NOT EXISTS (... AND NOT p).</summary>
            private Val Quantified(ERLQParser.QuantifiedExprContext q, Scope scope)
            {
                var collection = Eval(q.expression(0), scope) as Many
                    ?? throw new QueryError(q.expression(0), $"'{Text(q.expression(0))}' is not a collection.");
                var sub = new SelectQuery();
                var inner = new Scope(sub, scope);
                inner.Variables[N(q.name())] = collection.Apply(sub);
                var condition = Bool(q.expression(1), inner);
                if (q.FORALL() is not null)
                {
                    sub.Where.Add($"NOT ({condition})");
                    return new Scalar($"NOT EXISTS (SELECT 1 {sub.Inline()})");
                }
                sub.Where.Add(condition);
                return new Scalar($"EXISTS (SELECT 1 {sub.Inline()})");
            }

            private Val Call(ERLQParser.CallExprContext c, Scope scope)
            {
                var name = N(c.name()).ToLowerInvariant();
                var args = c.expressionList()?.expression() ?? Array.Empty<ERLQParser.ExpressionContext>();

                if (name is "card" or "count" or "sum" or "avg" or "min" or "max")
                {
                    if (args.Length != 1) throw new QueryError(c, $"{name} takes one argument.");
                    var arg = Eval(args[0], scope);
                    if (arg is Many collection)
                    {
                        // Aggregate of a collection: a correlated subquery per row.
                        var sub = new SelectQuery();
                        var element = collection.Apply(sub);
                        var aggregate = name is "card" || (name == "count" && element is EntityRef)
                            ? "COUNT(*)"
                            : Aggregate(name, ScalarOf(element, args[0]));
                        return new Scalar($"(SELECT {aggregate} {sub.Inline()})");
                    }
                    if (name == "card") throw new QueryError(args[0], $"card needs a collection; '{Text(args[0])}' is a single value (use count for the rows of a group).");
                    // Aggregate over the rows of the query (group by, or the whole result).
                    _usesGroupAggregate = true;
                    if (arg is EntityRef e)
                    {
                        var key = Storage(e.Type).Key;
                        return new Scalar(key.Count == 1
                            ? $"COUNT(DISTINCT {e.Alias}.{Q(key[0])})"
                            : $"COUNT(DISTINCT CONCAT_WS('|', {string.Join(", ", key.Select(k => $"{e.Alias}.{Q(k)}"))}))", true);
                    }
                    return new Scalar(Aggregate(name, ScalarOf(arg, args[0])), true);
                }

                var values = args.Select(a => ScalarOf(Eval(a, scope), a)).ToList();
                return (name, values.Count) switch
                {
                    ("today", 0) => new Scalar("CAST(GETDATE() AS date)"),
                    ("now", 0) => new Scalar("SYSDATETIME()"),
                    ("abs", 1) => new Scalar($"ABS({values[0]})"),
                    ("round", 1) => new Scalar($"ROUND({values[0]}, 0)"),
                    ("round", 2) => new Scalar($"ROUND({values[0]}, {values[1]})"),
                    ("floor", 1) => new Scalar($"FLOOR({values[0]})"),
                    ("ceiling", 1) or ("ceil", 1) => new Scalar($"CEILING({values[0]})"),
                    ("len", 1) or ("length", 1) => new Scalar($"LEN({values[0]})"),
                    ("upper", 1) => new Scalar($"UPPER({values[0]})"),
                    ("lower", 1) => new Scalar($"LOWER({values[0]})"),
                    ("trim", 1) => new Scalar($"TRIM({values[0]})"),
                    ("year", 1) => new Scalar($"YEAR({values[0]})"),
                    ("month", 1) => new Scalar($"MONTH({values[0]})"),
                    ("day", 1) => new Scalar($"DAY({values[0]})"),
                    ("coalesce", > 0) => new Scalar($"COALESCE({string.Join(", ", values)})"),
                    ("concat", > 1) => new Scalar($"CONCAT({string.Join(", ", values)})"),
                    _ => throw new QueryError(c, $"Unknown function '{name}' with {values.Count} argument(s).")
                };
            }

            /// <summary>avg works in decimals, so that an int column does not give an integer average.</summary>
            private static string Aggregate(string name, string value) => name switch
            {
                "avg" => $"AVG(CAST({value} AS decimal(19, 4)))",
                "count" => $"COUNT({value})",
                _ => $"{name.ToUpperInvariant()}({value})"
            };

            // -------------------------------------------------------------- names and navigation

            private Val Name(string name, ERLQParser.NameExprContext at, Scope scope)
            {
                if (scope.Variable(name) is { } variable) return variable;
                for (var s = scope; s is not null; s = s.Parent)
                    if (s.Implicit is { } element && TryMember(element, name, at, out var member)) return member!;
                if (model.FindType(name) is { } type) return All(type);
                throw new QueryError(at, $"Unknown name '{name}': not a variable, an attribute or role here, nor an entity.");
            }

            /// <summary>All instances of a type (for a subtype, the rows of its hierarchy table that are of it).</summary>
            private Many All(TypeDef type) => new(query =>
            {
                var storage = Storage(type);
                var alias = Alias(type);
                query.Add(storage.Table, alias, Conditions(storage, alias), left: false);
                return new EntityRef(type, alias, query);
            });

            private Val Filter(Val collection, ERLQParser.ExpressionContext condition, Scope scope, ERLQParser.FilterExprContext at)
            {
                if (collection is not Many many) throw new QueryError(at, $"A filter [...] needs a collection; '{Text(at.expression(0))}' is a single value.");
                return new Many(query =>
                {
                    var element = many.Apply(query);
                    var inner = new Scope(query, scope) { Implicit = element };
                    query.Where.Add(Bool(condition, inner));
                    return element;
                });
            }

            private Val Member(Val target, string name, ParserRuleContext at)
            {
                if (target is Many many)
                    return new Many(query => Member(many.Apply(query), name, at) switch
                    {
                        Many next => next.Apply(query), // r.projekti.ucesnici: flatten into the same query
                        var v => v
                    });
                if (TryMember(target, name, at, out var member)) return member!;
                throw new QueryError(at, target switch
                {
                    EntityRef e => $"'{e.Type.Name}' has no attribute or role '{name}'.",
                    CompositeRef c => $"Composite attribute '{c.Attribute.Name}' has no part '{name}'.",
                    _ => $"'{name}' cannot be taken from a value."
                });
            }

            private bool TryMember(Val target, string name, ParserRuleContext at, out Val? member)
            {
                member = null;
                if (target is CompositeRef composite)
                {
                    var part = composite.Attribute.Parts.FirstOrDefault(p => ErldModel.Same(p.Name, name));
                    if (part is null) return false;
                    member = new Scalar($"{composite.Alias}.{Q(map.Columns[part])}");
                    return true;
                }
                if (target is not EntityRef e) return false;

                // 1. Attributes of the type and its supertypes.
                var attribute = Attributes(e.Type).FirstOrDefault(a => ErldModel.Same(a.Name, name));
                if (attribute is not null)
                {
                    member = AttributeValue(e, attribute);
                    return true;
                }
                if (SubtypeAttributeOwner(e.Type, name) is { } owner)
                    throw new QueryError(at, $"'{name}' belongs to subtype '{owner.Name}'; range over {owner.Name} (from x in {owner.Name}) to use it.");

                // 2. Roles: mappings from the type (or a supertype); default role = co-domain name, lower case first.
                var lineage = Lineage(e.Type).ToList();
                foreach (var r in model.Relationships)
                    foreach (var m in r.Mappings.Where(m => lineage.Contains(m.Domain)))
                        if (ErldModel.Same(m.Role ?? LowerFirst(m.CoDomainName), name))
                        {
                            member = Navigate(e, r, m);
                            return true;
                        }

                // 3. From an aggregation to its participants (ucestvuje.radnik), and from a participant to the
                //    aggregation's instances (radnik.ucestvuje).
                if (e.Type.Relationship is { } own)
                    foreach (var m in own.Mappings)
                        if (ErldModel.Same(LowerFirst(m.Domain.Name), name))
                        {
                            member = ToParticipant(e, own, m);
                            return true;
                        }
                foreach (var r in model.Relationships.Where(r => r.IsAggregation))
                    foreach (var m in r.Mappings.Where(m => lineage.Contains(m.Domain)))
                        if (ErldModel.Same(LowerFirst(r.Name), name))
                        {
                            member = ToAggregation(e, r, m);
                            return true;
                        }
                return false;
            }

            private Val AttributeValue(EntityRef e, AttributeDef a)
            {
                if (map.Multivalued.TryGetValue(a, out var mv))
                    return new Many(query =>
                    {
                        var alias = NewAlias(mv.Table);
                        var key = Storage(e.Type).Key;
                        query.Add(mv.Table, alias, mv.OwnerColumns.Zip(key).Select(k => $"{alias}.{Q(k.First)} = {e.Alias}.{Q(k.Second)}").ToList(), left: false);
                        return new Scalar($"{alias}.{Q(mv.ValueColumn)}");
                    });
                if (a.IsComposite) return new CompositeRef(a, e.Alias);
                if (!map.Columns.TryGetValue(a, out var column))
                    throw new InvalidOperationException($"No column for {e.Type.Name}.{a.Name}.");
                return new Scalar($"{e.Alias}.{Q(column)}");
            }

            /// <summary>Navigation e -> co-domain of mapping m (m's domain is e's type or a supertype).</summary>
            private Val Navigate(EntityRef e, RelationshipDef r, MappingDef m)
            {
                var storage = map.Relationships[r];
                var target = m.CoDomain;
                var inverse = r.Mappings.First(x => x != m);

                IReadOnlyList<string> Join(string alias) => storage.Kind switch
                {
                    // FK in e's table: target.key = e.fk
                    RelationshipStorageKind.ForeignKey when storage.Holder == m =>
                        Storage(target).Key.Zip(storage.Columns).Select(k => $"{alias}.{Q(k.First)} = {e.Alias}.{Q(k.Second)}").ToList(),
                    // FK in the target's table: target.fk = e.key
                    RelationshipStorageKind.ForeignKey =>
                        storage.Columns.Zip(Storage(e.Type).Key).Select(k => $"{alias}.{Q(k.First)} = {e.Alias}.{Q(k.Second)}").ToList(),
                    _ => throw new InvalidOperationException()
                };

                if (storage.Kind == RelationshipStorageKind.LinkTable)
                    return Through(e, storage.Table!, storage.EndColumns[m], storage.EndColumns[inverse], target, m.IsMany, (r, m));

                return Step(e, target, Join, m.IsMany, (r, m));
            }

            /// <summary>Via a junction or aggregation table: link.end(e) = e.key, target.key = link.end(target).</summary>
            private Val Through(EntityRef e, string table, IReadOnlyList<string> fromColumns, IReadOnlyList<string> toColumns, TypeDef target, bool many, object step)
            {
                void AddLink(SelectQuery query, string link, bool left) =>
                    query.Add(table, link, fromColumns.Zip(Storage(e.Type).Key).Select(k => $"{link}.{Q(k.First)} = {e.Alias}.{Q(k.Second)}").ToList(), left);

                IReadOnlyList<string> TargetOn(string link, string alias) =>
                    Storage(target).Key.Zip(toColumns).Select(k => $"{alias}.{Q(k.First)} = {link}.{Q(k.Second)}").ToList();

                if (many)
                    return new Many(query =>
                    {
                        var link = NewAlias(table);
                        AddLink(query, link, left: false);
                        var alias = Alias(target);
                        query.Add(Storage(target).Table, alias, TargetOn(link, alias).Concat(Conditions(Storage(target), alias)).ToList(), left: false);
                        return new EntityRef(target, alias, query);
                    });

                var owner = e.Owner;
                if (owner.Navigations.TryGetValue((e.Alias, step), out var known)) return known;
                var linkAlias = NewAlias(table);
                AddLink(owner, linkAlias, left: true);
                var targetAlias = Alias(target);
                owner.Add(Storage(target).Table, targetAlias, TargetOn(linkAlias, targetAlias).Concat(Conditions(Storage(target), targetAlias)).ToList(), left: true);
                return owner.Navigations[(e.Alias, step)] = new EntityRef(target, targetAlias, owner);
            }

            /// <summary>One join to the target's table: LEFT JOIN in e's query (single-valued) or a collection.</summary>
            private Val Step(EntityRef e, TypeDef target, Func<string, IReadOnlyList<string>> on, bool many, object step)
            {
                if (many)
                    return new Many(query =>
                    {
                        var alias = Alias(target);
                        query.Add(Storage(target).Table, alias, on(alias).Concat(Conditions(Storage(target), alias)).ToList(), left: false);
                        return new EntityRef(target, alias, query);
                    });

                var owner = e.Owner;
                if (owner.Navigations.TryGetValue((e.Alias, step), out var known)) return known;
                var targetAlias = Alias(target);
                owner.Add(Storage(target).Table, targetAlias, on(targetAlias).Concat(Conditions(Storage(target), targetAlias)).ToList(), left: true);
                return owner.Navigations[(e.Alias, step)] = new EntityRef(target, targetAlias, owner);
            }

            /// <summary>Aggregation instance -> one participant (its foreign key in the aggregation's table).</summary>
            private Val ToParticipant(EntityRef aggregation, RelationshipDef r, MappingDef participant)
            {
                var columns = map.Relationships[r].EndColumns[participant];
                return Step(aggregation, participant.Domain,
                    alias => Storage(participant.Domain).Key.Zip(columns).Select(k => $"{alias}.{Q(k.First)} = {aggregation.Alias}.{Q(k.Second)}").ToList(),
                    many: false, (r, "participant", participant));
            }

            /// <summary>Participant -> instances of the aggregation it takes part in.</summary>
            private Val ToAggregation(EntityRef e, RelationshipDef r, MappingDef m)
            {
                var columns = map.Relationships[r].EndColumns[m];
                return Step(e, r.Aggregation!,
                    alias => columns.Zip(Storage(e.Type).Key).Select(k => $"{alias}.{Q(k.First)} = {e.Alias}.{Q(k.Second)}").ToList(),
                    m.IsMany, (r, "aggregation", m));
            }

            // -------------------------------------------------------------- model helpers

            private TypeStorage Storage(TypeDef type) => map.Types[type];

            private static IReadOnlyList<string> Conditions(TypeStorage storage, string alias) =>
                storage.Conditions.Select(c => $"{alias}.{Q(c.Column)} = {c.Value}").ToList();

            /// <summary>The type and its supertypes, nearest first.</summary>
            private static IEnumerable<TypeDef> Lineage(TypeDef type)
            {
                for (var t = type; t is not null; t = t.SubtypeOf?.Super) yield return t;
            }

            /// <summary>Attributes visible on an instance: its own and its supertypes' (supertypes first).</summary>
            private static IEnumerable<AttributeDef> Attributes(TypeDef type) =>
                Lineage(type).Reverse().SelectMany(t => t.Attributes);

            private TypeDef? SubtypeAttributeOwner(TypeDef type, string name) =>
                model.Types.FirstOrDefault(t => t != type && Lineage(t).Contains(type) && t.Attributes.Any(a => ErldModel.Same(a.Name, name)));

            private string Alias(TypeDef type) => NewAlias(type.Name);

            private string NewAlias(string name) => $"{char.ToLowerInvariant(name.FirstOrDefault(char.IsLetter, 't'))}{++_aliases}";

            private static string Unit(string unit) => unit.ToLowerInvariant().TrimEnd('s') switch
            {
                "month" => "month",
                "year" => "year",
                "hour" => "hour",
                "minute" => "minute",
                _ => "day"
            };

            private static string Literal(ERLQParser.LiteralContext literal) => literal switch
            {
                _ when literal.STRING() is { } s => "N'" + ErldModelBuilder.Unquote(s.GetText()).Replace("'", "''") + "'",
                _ when literal.TRUE() is not null => "1",
                _ when literal.FALSE() is not null => "0",
                _ when literal.NULL() is not null => "NULL",
                _ => literal.GetText()
            };

            private static string Text(ParserRuleContext ctx) =>
                string.Join(" ", ctx.Start.InputStream.GetText(Interval.Of(ctx.Start.StartIndex, ctx.Stop?.StopIndex ?? ctx.Start.StopIndex))
                    .Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

            private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
        }

        private static string N(ERLQParser.NameContext name) =>
            name.QUOTED_ID() is { } q ? q.GetText()[1..^1] : name.GetText();

        private static string Paren(string condition) => condition.Contains(" OR ") ? $"({condition})" : condition;

        public static string Q(string name) => "[" + name.Replace("]", "]]") + "]";
    }
}
