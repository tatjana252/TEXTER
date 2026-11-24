using System.Globalization;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using Texter.Erl.Grammar;
using Texter.Erl.Semantics;

namespace Texter.Erl.Mongo
{
    /// <param name="Collection">Collection the pipeline runs on; null: a database-level aggregate starting with $documents.</param>
    /// <param name="Columns">Output columns: field in the result documents and header.</param>
    /// <param name="Script">The query as mongosh code, for reading.</param>
    public record MongoQuery(string? Collection, BsonArray Pipeline, List<(string Field, string Header)> Columns, string Script);

    public record ErlqMongoTranslation(MongoQuery? Query, List<ErlSyntaxError> Errors);

    /// <summary>
    /// ERLQ -> MongoDB aggregation pipeline over the schema of step 3 (MongoMap).
    ///
    /// Every variable is a field of the pipeline's documents (the first range: $replaceRoot { v1: $$ROOT }).
    ///  - a range over a type: the collection (or $lookup + $unwind for further ranges), with the subtype's condition;
    ///  - single-valued navigation: $lookup of the referenced document, then $first;
    ///  - a collection (role to many, embedded array, filter, multivalued attribute) consumed by card / count / sum /
    ///    avg / min / max, exists / forall or "x in c": a $lookup whose sub-pipeline is the collection, correlated
    ///    through "let" (as a correlated subquery in SQL); embedded arrays enter a sub-pipeline through $documents;
    ///  - group by: $group; select: $project to fields c0, c1, ...; order by, limit: $sort, $limit.
    /// </summary>
    public static class ErlqMongoTranslator
    {
        public static ErlqMongoTranslation Translate(string source, ErldModel model, MongoMap map)
        {
            var (tree, errors) = ErlqSyntax.Parse(source);
            if (errors.Count > 0) return new ErlqMongoTranslation(null, errors);
            try
            {
                return new ErlqMongoTranslation(new Translator(model, map).Translate(tree), new());
            }
            catch (QueryError e)
            {
                return new ErlqMongoTranslation(null, new() { new ErlSyntaxError(e.At.Start.Line, e.At.Start.Column + 1, e.Message) });
            }
        }

        private sealed class QueryError(ParserRuleContext at, string message) : Exception(message)
        {
            public ParserRuleContext At { get; } = at;
        }

        // ------------------------------------------------------------------ values

        /// <summary>A field of a pipeline's documents that holds one value: an entity document or a scalar.</summary>
        private sealed class Binding(string id, Pipeline owner)
        {
            public string Id { get; } = id;
            public Pipeline Owner { get; } = owner;
        }

        private abstract record Val;

        /// <summary>An aggregation expression; IsDate: compare string literals as dates.</summary>
        /// <summary>IsLiteral: a constant written in the query (a string there is text, not a field path).</summary>
        private sealed record Scalar(BsonValue Expr, bool IsDate = false, bool IsRowAggregate = false, bool IsLiteral = false) : Val;

        /// <summary>A document of a type, held by a binding (or a path below it, for a composite attribute's parent).</summary>
        private sealed record EntityRef(TypeDef Type, Binding Binding) : Val;

        private sealed record CompositeRef(AttributeDef Attribute, Binding Binding, string Path) : Val;

        /// <summary>A collection; Apply adds it to a pipeline and returns the element (bound in that pipeline).</summary>
        private sealed record Many(Func<Pipeline, Val> Apply) : Val;

        /// <summary>
        /// A pipeline being built: the main query or a $lookup sub-pipeline. Its outer bindings reach it through
        /// "let" ($$id); its own through fields ($id).
        /// </summary>
        private sealed class Pipeline(Pipeline? parent)
        {
            public Pipeline? Parent { get; } = parent;
            public string? Collection { get; set; }
            public bool Started { get; set; }
            public BsonArray Stages { get; } = new();
            public Dictionary<(Binding, object), EntityRef> Navigations { get; } = new();

            /// <summary>Outer bindings this sub-pipeline uses: let { id: <expression in the parent> }.</summary>
            public Dictionary<string, Binding> Lets { get; } = new();

            /// <summary>The expression of a binding's value seen from this pipeline.</summary>
            public string Ref(Binding b)
            {
                if (b.Owner == this) return "$" + b.Id;
                // An outer binding: through let, declared on every pipeline between this one and the owner.
                for (var p = this; p is not null && p != b.Owner; p = p.Parent) p.Lets[b.Id] = b;
                return "$$" + b.Id;
            }

            public BsonDocument LetDocument()
            {
                var let = new BsonDocument();
                foreach (var b in Lets.Values) let[b.Id] = Parent!.Ref(b);
                return let;
            }
        }

        // ------------------------------------------------------------------ translator

        private sealed class Translator(ErldModel model, MongoMap map)
        {
            private int _ids;

            private sealed class Scope(Pipeline pipeline, Scope? parent = null)
            {
                public Pipeline Pipeline { get; } = pipeline;
                public Scope? Parent { get; } = parent;
                public Dictionary<string, Val> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
                public Val? Implicit { get; init; }
                public Val? Variable(string name) => Variables.TryGetValue(name, out var v) ? v : Parent?.Variable(name);
            }

            private record Item(BsonValue Expr, string Header, bool IsRowAggregate);

            public MongoQuery Translate(ERLQParser.ErlqFileContext file)
            {
                var main = new Pipeline(null);
                var scope = new Scope(main);
                var items = new List<Item>();
                var groupBy = new List<(BsonValue Expr, string Text)>();
                BsonValue? having = null;
                var orderBy = new List<(ERLQParser.ExpressionContext Expr, int Direction)>();
                var distinct = false;
                int? limit = null;

                switch (file.query())
                {
                    case ERLQParser.ShortFormContext s:
                        var value = Eval(s.expression(), scope);
                        var element = value is Many many ? many.Apply(main) : value;
                        items.AddRange(Columns(element, s.expression(), main));
                        break;

                    case ERLQParser.LongFormContext l:
                        var q = l.longQuery();
                        foreach (var range in q.rangeDecl())
                        {
                            var name = N(range.name());
                            var collection = Eval(range.expression(), scope) as Many
                                ?? throw new QueryError(range.expression(), $"'{Text(range.expression())}' is not a collection to range over.");
                            scope.Variables[name] = collection.Apply(main);
                        }
                        if (q.WHERE() is not null) Match(main, Bool(q.expression(0), scope));
                        if (q.GROUP() is not null)
                            foreach (var e in q.expressionList().expression())
                                groupBy.Add((ScalarOf(Eval(e, scope), e, main), Text(e)));
                        foreach (var item in q.selectItem())
                        {
                            var header = item.name() is { } a ? N(a) : null;
                            var groupIndex = groupBy.FindIndex(g => g.Text == Text(item.expression()));
                            if (groupIndex >= 0)
                            {
                                items.Add(new Item($"$_id.g{groupIndex}", header ?? Text(item.expression()), false));
                                continue;
                            }
                            var columns = Columns(Eval(item.expression(), scope), item.expression(), main);
                            items.AddRange(columns.Count == 1 && header is not null ? new[] { columns[0] with { Header = header } } : columns);
                        }
                        if (q.HAVING() is not null) having = Bool(q.expression().Last(), scope);
                        foreach (var o in q.orderItem()) orderBy.Add((o.expression(), o.DESC() is not null ? -1 : 1));
                        distinct = q.DISTINCT() is not null;
                        if (q.LIMIT() is not null) limit = int.Parse(q.INT().GetText(), CultureInfo.InvariantCulture);
                        break;
                }

                var stages = main.Stages;
                var grouped = groupBy.Count > 0 || items.Any(i => i.IsRowAggregate);
                var project = new BsonDocument("_id", 0);

                if (grouped)
                {
                    // $group: keys g0.., accumulators a0..; then the select list over the groups.
                    var group = new BsonDocument("_id", groupBy.Count == 0 ? BsonNull.Value : new BsonDocument(groupBy.Select((g, i) => new BsonElement($"g{i}", g.Expr))));
                    var accumulators = 0;
                    for (var i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        if (item.IsRowAggregate)
                        {
                            var accumulator = item.Expr.AsBsonDocument;
                            var name = $"a{accumulators++}";
                            // count(r): distinct instances, as COUNT(DISTINCT key) in SQL.
                            if (accumulator.Contains("$addToSet")) { group[name] = accumulator; project[$"c{i}"] = new BsonDocument("$size", "$" + name); }
                            else { group[name] = accumulator; project[$"c{i}"] = "$" + name; }
                        }
                        else if (item.Expr is BsonString s && s.Value.StartsWith("$_id.g")) project[$"c{i}"] = s;
                        else throw new QueryError(file, $"'{item.Header}' is neither grouped nor an aggregate (count, sum, avg, min, max).");
                    }
                    stages.Add(new BsonDocument("$group", group));
                    if (having is not null) throw new QueryError(file, "having is not supported for MongoDB yet; filter in where, or sort and limit.");
                }
                else
                {
                    for (var i = 0; i < items.Count; i++) project[$"c{i}"] = items[i].Expr;
                }

                // order by: a select column (by header) or, without grouping, any expression.
                var sort = new BsonDocument();
                var hidden = new List<string>();
                foreach (var (expr, direction) in orderBy)
                {
                    var text = Text(expr);
                    var column = items.FindIndex(i => ErldModel.Same(i.Header, text) || i.Header == text);
                    if (column >= 0) { sort[$"c{column}"] = direction; continue; }
                    if (grouped || distinct) throw new QueryError(expr, $"With group by or distinct, order by a column of the select list ('{text}' is not one).");
                    var field = $"o{hidden.Count}";
                    project[field] = ScalarOf(Eval(expr, scope), expr, main);
                    hidden.Add(field);
                    sort[field] = direction;
                }

                stages.Add(new BsonDocument("$project", project));
                if (distinct)
                {
                    stages.Add(new BsonDocument("$group", new BsonDocument("_id", "$$ROOT")));
                    stages.Add(new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$_id")));
                }
                if (sort.ElementCount > 0) stages.Add(new BsonDocument("$sort", sort));
                if (limit is { } n) stages.Add(new BsonDocument("$limit", n));
                if (hidden.Count > 0) stages.Add(new BsonDocument("$unset", new BsonArray(hidden)));

                if (main.Collection is null) stages.Insert(0, new BsonDocument("$documents", new BsonArray { new BsonDocument() }));
                var columnsOut = items.Select((item, i) => ($"c{i}", item.Header)).ToList();
                return new MongoQuery(main.Collection, stages, columnsOut, Script(main.Collection, stages));
            }

            private static void Match(Pipeline p, BsonValue condition) =>
                p.Stages.Add(new BsonDocument("$match", new BsonDocument("$expr", condition)));

            private List<Item> Columns(Val value, ERLQParser.ExpressionContext at, Pipeline p)
            {
                var text = Text(at);
                switch (value)
                {
                    case Scalar s:
                        return new() { new Item(s.Expr, text, s.IsRowAggregate) };
                    case EntityRef e:
                        var baseRef = p.Ref(e.Binding);
                        return Attributes(e.Type)
                            .SelectMany(a => a.IsComposite ? a.Parts.Select(part => (Attr: part, Name: $"{a.Name}.{part.Name}")) : new[] { (Attr: a, Name: a.Name) })
                            .Where(x => map.Fields.ContainsKey(x.Attr))
                            .Select(x => new Item($"{baseRef}.{map.Fields[x.Attr]}", $"{text}.{x.Name}", false))
                            .ToList();
                    case CompositeRef c:
                        return c.Attribute.Parts.Where(map.Fields.ContainsKey)
                            .Select(part => new Item($"{p.Ref(c.Binding)}.{map.Fields[part]}", $"{text}.{part.Name}", false)).ToList();
                    default:
                        throw new QueryError(at, $"'{text}' is a collection; use card(...), sum(...), exists ... or a range variable in from.");
                }
            }

            // -------------------------------------------------------------- expressions

            private BsonValue Bool(ERLQParser.ExpressionContext e, Scope scope) => ScalarOf(Eval(e, scope), e, scope.Pipeline);

            private BsonValue ScalarOf(Val v, ERLQParser.ExpressionContext at, Pipeline p) => v switch
            {
                Scalar s => s.Expr,
                EntityRef e => Identity(e, p),
                _ => throw new QueryError(at, $"'{Text(at)}' is {(v is Many ? "a collection" : "not a single value")} here; aggregate it (card, sum, ...) or use exists / forall.")
            };

            private Val Eval(ERLQParser.ExpressionContext e, Scope scope)
            {
                var p = scope.Pipeline;
                switch (e)
                {
                    case ERLQParser.LiteralExprContext l: return new Scalar(Literal(l.literal()), IsLiteral: true);
                    case ERLQParser.NameExprContext n: return Name(N(n.name()), n, scope);
                    case ERLQParser.ParenExprContext par:
                        if (par.expressionList().expression().Length != 1) throw new QueryError(par, "A list (a, b, c) is only allowed after in.");
                        return Eval(par.expressionList().expression(0), scope);
                    case ERLQParser.MemberExprContext m: return Member(Eval(m.expression(), scope), N(m.name()), m, p);
                    case ERLQParser.FilterExprContext f: return Filter(Eval(f.expression(0), scope), f.expression(1), scope, f);
                    case ERLQParser.NegateExprContext neg: return new Scalar(new BsonDocument("$multiply", new BsonArray { -1, Num(neg.expression(), scope) }));
                    case ERLQParser.MulExprContext m:
                        var op = m.op.Text switch { "*" => "$multiply", "/" => "$divide", _ => "$mod" };
                        return new Scalar(new BsonDocument(op, new BsonArray { Num(m.expression(0), scope), Num(m.expression(1), scope) }));
                    case ERLQParser.AddExprContext a:
                        if (a.expression(1) is ERLQParser.DurationExprContext d)
                        {
                            var amount = int.Parse(d.INT().GetText(), CultureInfo.InvariantCulture) * (a.op.Text == "-" ? -1 : 1);
                            return new Scalar(new BsonDocument("$dateAdd", new BsonDocument
                            {
                                ["startDate"] = Num(a.expression(0), scope),
                                ["unit"] = Unit(d.durationUnit().GetText()),
                                ["amount"] = amount
                            }), IsDate: true);
                        }
                        return new Scalar(new BsonDocument(a.op.Text == "+" ? "$add" : "$subtract", new BsonArray { Num(a.expression(0), scope), Num(a.expression(1), scope) }));
                    case ERLQParser.CompareExprContext c: return Compare(c, scope);
                    case ERLQParser.IsNullExprContext i:
                        var isNull = new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { ScalarOf(Eval(i.expression(), scope), i.expression(), p), BsonNull.Value }), BsonNull.Value });
                        return new Scalar(i.NOT() is not null ? new BsonDocument("$not", new BsonArray { isNull }) : isNull);
                    case ERLQParser.InExprContext i: return In(i, scope);
                    case ERLQParser.BetweenExprContext b:
                        var (v0, lo, hi) = (Eval(b.expression(0), scope), Eval(b.expression(1), scope), Eval(b.expression(2), scope));
                        var x = ScalarOf(v0, b.expression(0), p);
                        var between = new BsonDocument("$and", new BsonArray
                        {
                            new BsonDocument("$gte", new BsonArray { x, Coerce(lo, v0, b.expression(1), p) }),
                            new BsonDocument("$lte", new BsonArray { x, Coerce(hi, v0, b.expression(2), p) })
                        });
                        return new Scalar(b.NOT() is not null ? new BsonDocument("$not", new BsonArray { between }) : between);
                    case ERLQParser.LikeExprContext l:
                        if (l.expression(1) is not ERLQParser.LiteralExprContext { } lit || lit.literal().STRING() is null)
                            throw new QueryError(l.expression(1), "like needs a string literal pattern.");
                        var like = new BsonDocument("$regexMatch", new BsonDocument
                        {
                            ["input"] = Num(l.expression(0), scope),
                            ["regex"] = LikeToRegex(ErldModelBuilder.Unquote(lit.literal().STRING().GetText()))
                        });
                        return new Scalar(l.NOT() is not null ? new BsonDocument("$not", new BsonArray { like }) : like);
                    case ERLQParser.NotExprContext n: return new Scalar(new BsonDocument("$not", new BsonArray { Bool(n.expression(), scope) }));
                    case ERLQParser.AndExprContext a: return new Scalar(new BsonDocument("$and", new BsonArray { Bool(a.expression(0), scope), Bool(a.expression(1), scope) }));
                    case ERLQParser.OrExprContext o: return new Scalar(new BsonDocument("$or", new BsonArray { Bool(o.expression(0), scope), Bool(o.expression(1), scope) }));
                    case ERLQParser.ImpliesExprContext i:
                        return new Scalar(new BsonDocument("$or", new BsonArray { new BsonDocument("$not", new BsonArray { Bool(i.expression(0), scope) }), Bool(i.expression(1), scope) }));
                    case ERLQParser.IfExprContext i:
                        var parts = i.expression().Select(x2 => ScalarOf(Eval(x2, scope), x2, p)).ToList();
                        return new Scalar(new BsonDocument("$cond", new BsonArray { parts[0], parts[1], parts.Count == 3 ? parts[2] : BsonNull.Value }));
                    case ERLQParser.QuantifiedExprContext q: return Quantified(q, scope);
                    case ERLQParser.CallExprContext c: return Call(c, scope);
                    case ERLQParser.DurationExprContext dur: throw new QueryError(dur, "A duration (15 days) can only be added to or subtracted from a date.");
                    case ERLQParser.ForEachExprContext f: throw new QueryError(f, "for each belongs to ERLD constraints; in a query use from ... in ....");
                    default: throw new QueryError(e, $"Unsupported expression '{Text(e)}'.");
                }
            }

            private BsonValue Num(ERLQParser.ExpressionContext e, Scope scope) => ScalarOf(Eval(e, scope), e, scope.Pipeline);

            /// <summary>A string literal compared with a date becomes a date ('2026-01-31').</summary>
            private BsonValue Coerce(Val value, Val other, ERLQParser.ExpressionContext at, Pipeline p)
            {
                if (other is Scalar { IsDate: true } && value is Scalar { IsLiteral: true, Expr: BsonString s })
                    return new BsonDocument("$dateFromString", new BsonDocument("dateString", s));
                return ScalarOf(value, at, p);
            }

            private Val Compare(ERLQParser.CompareExprContext c, Scope scope)
            {
                var p = scope.Pipeline;
                var op = c.compOp().GetText() switch { "=" => "$eq", "<>" or "!=" => "$ne", "<" => "$lt", "<=" => "$lte", ">" => "$gt", _ => "$gte" };
                var (left, right) = (Eval(c.expression(0), scope), Eval(c.expression(1), scope));
                if (left is EntityRef a && right is EntityRef b && op is "$eq" or "$ne")
                {
                    var same = new BsonDocument("$eq", new BsonArray { Identity(a, p), Identity(b, p) });
                    return new Scalar(op == "$eq" ? same : new BsonDocument("$not", new BsonArray { same }));
                }
                return new Scalar(new BsonDocument(op, new BsonArray { Coerce(left, right, c.expression(0), p), Coerce(right, left, c.expression(1), p) }));
            }

            private Val In(ERLQParser.InExprContext i, Scope scope)
            {
                var p = scope.Pipeline;
                var value = Eval(i.expression(0), scope);
                if (i.expression(1) is ERLQParser.ParenExprContext par)
                {
                    var list = new BsonArray(par.expressionList().expression().Select(x => Coerce(Eval(x, scope), value, x, p)));
                    var inList = new BsonDocument("$in", new BsonArray { ScalarOf(value, i.expression(0), p), list });
                    return new Scalar(i.NOT() is not null ? new BsonDocument("$not", new BsonArray { inList }) : inList);
                }
                var collection = Eval(i.expression(1), scope) as Many ?? throw new QueryError(i.expression(1), $"'{Text(i.expression(1))}' is not a collection.");
                var exists = Sub(p, collection, (sub, element) =>
                {
                    Match(sub, new BsonDocument("$eq", new BsonArray { ScalarOf(element, i.expression(1), sub), ScalarOf(value, i.expression(0), sub) }));
                    sub.Stages.Add(new BsonDocument("$limit", 1));
                });
                var found = new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", exists), 0 });
                return new Scalar(i.NOT() is not null ? new BsonDocument("$not", new BsonArray { found }) : found);
            }

            /// <summary>exists x in c : p -> a matching element exists; forall -> no element violates p.</summary>
            private Val Quantified(ERLQParser.QuantifiedExprContext q, Scope scope)
            {
                var p = scope.Pipeline;
                var collection = Eval(q.expression(0), scope) as Many ?? throw new QueryError(q.expression(0), $"'{Text(q.expression(0))}' is not a collection.");
                var forall = q.FORALL() is not null;
                var found = Sub(p, collection, (sub, element) =>
                {
                    var inner = new Scope(sub, scope);
                    inner.Variables[N(q.name())] = element;
                    var condition = Bool(q.expression(1), inner);
                    Match(sub, forall ? new BsonDocument("$not", new BsonArray { condition }) : condition);
                    sub.Stages.Add(new BsonDocument("$limit", 1));
                });
                var any = new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", found), 0 });
                return new Scalar(forall ? new BsonDocument("$not", new BsonArray { any }) : any);
            }

            /// <summary>
            /// A collection as a $lookup into a new field of p; finish adds the final stages of the sub-pipeline.
            /// Returns the field's expression (an array).
            /// </summary>
            private BsonValue Sub(Pipeline p, Many collection, Action<Pipeline, Val> finish)
            {
                var sub = new Pipeline(p);
                var element = collection.Apply(sub);
                finish(sub, element);
                var field = NewId("s");
                p.Stages.Add(Lookup(sub, field));
                return "$" + field;
            }

            private Val Call(ERLQParser.CallExprContext c, Scope scope)
            {
                var p = scope.Pipeline;
                var name = N(c.name()).ToLowerInvariant();
                var args = c.expressionList()?.expression() ?? Array.Empty<ERLQParser.ExpressionContext>();

                if (name is "card" or "count" or "sum" or "avg" or "min" or "max")
                {
                    if (args.Length != 1) throw new QueryError(c, $"{name} takes one argument.");
                    var arg = Eval(args[0], scope);
                    if (arg is Many collection)
                    {
                        // Aggregate of a collection: a $lookup with $group at the end (a correlated subquery).
                        var array = Sub(p, collection, (sub, element) =>
                        {
                            BsonValue accumulator = name is "card" || (name == "count" && element is EntityRef)
                                ? new BsonDocument("$sum", 1)
                                : name == "count"
                                    ? new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { ScalarOf(element, args[0], sub), BsonNull.Value }), BsonNull.Value }), 0, 1 }))
                                    : new BsonDocument("$" + name, ScalarOf(element, args[0], sub));
                            sub.Stages.Add(new BsonDocument("$group", new BsonDocument { ["_id"] = BsonNull.Value, ["v"] = accumulator }));
                        });
                        var first = new BsonDocument("$first", array + ".v");
                        return new Scalar(name is "card" or "count" ? new BsonDocument("$ifNull", new BsonArray { first, 0 }) : first);
                    }
                    if (name == "card") throw new QueryError(args[0], $"card needs a collection; '{Text(args[0])}' is a single value (use count for the rows of a group).");
                    // Aggregate over the rows of the query: a $group accumulator.
                    if (arg is EntityRef e) return new Scalar(new BsonDocument("$addToSet", Identity(e, p)), IsRowAggregate: true);
                    var value = ScalarOf(arg, args[0], p);
                    return new Scalar(name == "count"
                        ? new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { value, BsonNull.Value }), BsonNull.Value }), 0, 1 }))
                        : new BsonDocument("$" + name, value), IsRowAggregate: true);
                }

                var values = args.Select(a => ScalarOf(Eval(a, scope), a, p)).ToList();
                BsonValue One(string op) => new BsonDocument(op, values[0]);
                return (name, values.Count) switch
                {
                    ("today", 0) => new Scalar(new BsonDocument("$dateTrunc", new BsonDocument { ["date"] = "$$NOW", ["unit"] = "day" }), IsDate: true),
                    ("now", 0) => new Scalar("$$NOW", IsDate: true),
                    ("abs", 1) => new Scalar(One("$abs")),
                    ("round", 1) => new Scalar(new BsonDocument("$round", new BsonArray { values[0], 0 })),
                    ("round", 2) => new Scalar(new BsonDocument("$round", new BsonArray { values[0], values[1] })),
                    ("floor", 1) => new Scalar(One("$floor")),
                    ("ceiling", 1) or ("ceil", 1) => new Scalar(One("$ceil")),
                    ("len", 1) or ("length", 1) => new Scalar(One("$strLenCP")),
                    ("upper", 1) => new Scalar(One("$toUpper")),
                    ("lower", 1) => new Scalar(One("$toLower")),
                    ("trim", 1) => new Scalar(new BsonDocument("$trim", new BsonDocument("input", values[0]))),
                    ("year", 1) => new Scalar(One("$year")),
                    ("month", 1) => new Scalar(One("$month")),
                    ("day", 1) => new Scalar(One("$dayOfMonth")),
                    ("coalesce", > 0) => new Scalar(new BsonDocument("$ifNull", new BsonArray(values))),
                    ("concat", > 1) => new Scalar(new BsonDocument("$concat", new BsonArray(values.Select(v => new BsonDocument("$toString", v))))),
                    _ => throw new QueryError(c, $"Unknown function '{name}' with {values.Count} argument(s).")
                };
            }

            // -------------------------------------------------------------- names and navigation

            private Val Name(string name, ERLQParser.NameExprContext at, Scope scope)
            {
                if (scope.Variable(name) is { } variable) return variable;
                for (var s = scope; s is not null; s = s.Parent)
                    if (s.Implicit is { } element && TryMember(element, name, at, scope.Pipeline, out var member)) return member!;
                if (model.FindType(name) is { } type) return All(type);
                throw new QueryError(at, $"Unknown name '{name}': not a variable, an attribute or role here, nor an entity.");
            }

            private Val Filter(Val collection, ERLQParser.ExpressionContext condition, Scope scope, ERLQParser.FilterExprContext at)
            {
                if (collection is not Many many) throw new QueryError(at, $"A filter [...] needs a collection; '{Text(at.expression(0))}' is a single value.");
                return new Many(p =>
                {
                    var element = many.Apply(p);
                    Match(p, Bool(condition, new Scope(p, scope) { Implicit = element }));
                    return element;
                });
            }

            private Val Member(Val target, string name, ParserRuleContext at, Pipeline p)
            {
                if (target is Many many)
                    return new Many(q => Member(many.Apply(q), name, at, q) switch
                    {
                        Many next => next.Apply(q), // r.projekti.ucesnici: further elements in the same pipeline
                        var v => v
                    });
                if (TryMember(target, name, at, p, out var member)) return member!;
                throw new QueryError(at, target switch
                {
                    EntityRef e => $"'{e.Type.Name}' has no attribute or role '{name}'.",
                    CompositeRef c => $"Composite attribute '{c.Attribute.Name}' has no part '{name}'.",
                    _ => $"'{name}' cannot be taken from a value."
                });
            }

            private bool TryMember(Val target, string name, ParserRuleContext at, Pipeline p, out Val? member)
            {
                member = null;
                if (target is CompositeRef composite)
                {
                    var part = composite.Attribute.Parts.FirstOrDefault(x => ErldModel.Same(x.Name, name));
                    if (part is null) return false;
                    member = new Scalar($"{p.Ref(composite.Binding)}.{map.Fields[part]}", IsDate(part));
                    return true;
                }
                if (target is not EntityRef e) return false;

                var attribute = Attributes(e.Type).FirstOrDefault(a => ErldModel.Same(a.Name, name));
                if (attribute is not null)
                {
                    member = AttributeValue(e, attribute, at, p);
                    return true;
                }
                if (model.Types.FirstOrDefault(t => t != e.Type && Lineage(t).Contains(e.Type) && t.Attributes.Any(a => ErldModel.Same(a.Name, name))) is { } owner)
                    throw new QueryError(at, $"'{name}' belongs to subtype '{owner.Name}'; range over {owner.Name} (from x in {owner.Name}) to use it.");

                var lineage = Lineage(e.Type).ToList();
                foreach (var r in model.Relationships)
                    foreach (var m in r.Mappings.Where(m => lineage.Contains(m.Domain)))
                        if (ErldModel.Same(m.Role ?? LowerFirst(m.CoDomainName), name))
                        {
                            member = Navigate(e, r, m, p);
                            return true;
                        }
                if (e.Type.Relationship is { } own)
                    foreach (var m in own.Mappings)
                        if (ErldModel.Same(LowerFirst(m.Domain.Name), name))
                        {
                            member = Lookup(e, m.Domain, map.Relationships[own].EndFields[m], many: false, (own, "participant", m));
                            return true;
                        }
                foreach (var r in model.Relationships.Where(r => r.IsAggregation))
                    foreach (var m in r.Mappings.Where(m => lineage.Contains(m.Domain)))
                        if (ErldModel.Same(LowerFirst(r.Name), name))
                        {
                            member = Referencing(e, r.Aggregation!, map.Relationships[r].EndFields[m], m.IsMany, (r, "aggregation", m));
                            return true;
                        }
                return false;
            }

            private Val AttributeValue(EntityRef e, AttributeDef a, ParserRuleContext at, Pipeline p)
            {
                if (map.Arrays.TryGetValue(a, out var arrayField))
                    return Embedded(e.Binding, arrayField, weak: null);
                if (a.IsComposite) return new CompositeRef(a, e.Binding, a.Name);
                if (a.IsDerived) throw new QueryError(at, $"'{a.Name}' is a derived attribute; it is not stored in MongoDB.");
                return new Scalar($"{p.Ref(e.Binding)}.{map.Fields[a]}", IsDate(a));
            }

            /// <summary>Navigation e -> co-domain of mapping m, following how the relationship is stored.</summary>
            private Val Navigate(EntityRef e, RelationshipDef r, MappingDef m, Pipeline p)
            {
                var storage = map.Relationships[r];
                var target = m.CoDomain;
                var inverse = r.Mappings.First(x => x != m);
                var step = (r, m);
                switch (storage.Kind)
                {
                    case MongoRelationshipKind.Embedded when m == storage.Holder:
                        // Owner -> its weak entities: the embedded array.
                        return Embedded(e.Binding, storage.Field!, weak: target);
                    case MongoRelationshipKind.Embedded:
                        // Weak entity -> its owner: the _owner every element carries.
                        return Lookup(e, target, "_owner", many: false, step);
                    case MongoRelationshipKind.Reference when m == storage.Holder:
                        return Lookup(e, target, storage.Field!, many: false, step);
                    case MongoRelationshipKind.Reference:
                        return Referencing(e, target, storage.Field!, m.IsMany, step);
                    case MongoRelationshipKind.ReferenceArray when m == storage.Holder:
                        return ReferencedBy(e, target, storage.Field!);
                    case MongoRelationshipKind.ReferenceArray:
                        return Containing(e, target, storage.Field!, m.IsMany, step);
                    default: // aggregation's own collection between two participants
                        var aggregation = r.Aggregation!;
                        var from = storage.EndFields[m];
                        var to = storage.EndFields[inverse];
                        var links = Referencing(e, aggregation, from, m.IsMany, (r, "link", m));
                        return links switch
                        {
                            Many many => new Many(q =>
                            {
                                var link = (EntityRef)many.Apply(q);
                                return ((Many)Lookup(link, target, to, many: true, (r, "end", m))).Apply(q);
                            }),
                            EntityRef link => Lookup(link, target, to, many: false, (r, "end", m)),
                            _ => throw new InvalidOperationException()
                        };
                }
            }

            /// <summary>A field of an instance (a reference it holds, or its _owner), seen from pipeline p.</summary>
            private static BsonValue Ref(EntityRef e, string field, Pipeline p) => $"{p.Ref(e.Binding)}.{field}";

            // -------------------------------------------------------------- sources

            /// <summary>All instances of a type.</summary>
            private Many All(TypeDef type) => new(p => Start(p, type, condition: null));

            /// <summary>
            /// Puts the documents of a type into a pipeline: as its source when the pipeline is empty, otherwise
            /// joined in by $lookup + $unwind. condition(element): extra correlation, in the inner pipeline.
            /// </summary>
            private Val Start(Pipeline p, TypeDef type, Func<Pipeline, string, BsonValue>? condition)
            {
                var id = NewId(type.Name);
                if (!p.Started)
                {
                    var (collection, stages) = Source(type);
                    p.Started = true;
                    p.Collection = collection;
                    foreach (var s in stages) p.Stages.Add(s);
                    p.Stages.Add(new BsonDocument("$replaceRoot", new BsonDocument("newRoot", new BsonDocument(id, "$$ROOT"))));
                    var binding = new Binding(id, p);
                    if (condition is not null) Match(p, condition(p, "$" + id));
                    // (the documents are now { id: document }: their fields are $id.field)
                    return new EntityRef(type, binding);
                }

                var sub = new Pipeline(p);
                var (subCollection, subStages) = Source(type);
                sub.Started = true;
                sub.Collection = subCollection;
                foreach (var s in subStages) sub.Stages.Add(s);
                if (condition is not null) Match(sub, condition(sub, "$$ROOT"));
                p.Stages.Add(Lookup(sub, id));
                p.Stages.Add(new BsonDocument("$unwind", "$" + id));
                return new EntityRef(type, new Binding(id, p));
            }

            /// <summary>Collection and stages that yield one document per instance of a type (embedded: the element, with _owner).</summary>
            private (string Collection, List<BsonDocument> Stages) Source(TypeDef type)
            {
                var storage = map.Types[type];
                if (storage.Kind == MongoTypeKind.Document)
                {
                    var stages = new List<BsonDocument>();
                    if (storage.Conditions.Count > 0)
                        stages.Add(new BsonDocument("$match", new BsonDocument(storage.Conditions.Select(c => new BsonElement(c.Field, c.Value)))));
                    return (storage.Collection, stages);
                }
                // Weak entity: unwind the owner's array; each element gets its owner's identity as _owner.
                var (collection, ownerStages) = Source(storage.Owner!);
                var field = storage.ArrayField!;
                ownerStages.Add(new BsonDocument("$unwind", "$" + field));
                ownerStages.Add(new BsonDocument("$replaceRoot", new BsonDocument("newRoot",
                    new BsonDocument("$mergeObjects", new BsonArray { "$" + field, new BsonDocument("_owner", IdentityOf(storage.Owner!, "$")) }))));
                return (collection, ownerStages);
            }

            /// <summary>Identity of a document of a type, as references hold it: _id, or { _owner, partial key } for an embedded one.</summary>
            private BsonValue IdentityOf(TypeDef type, string root)
            {
                var storage = map.Types[type];
                var prefix = root == "$" ? "$" : root + ".";   // "$": the current document; "$x" / "$$x" / "$$ROOT": below it
                if (storage.Kind == MongoTypeKind.Document) return prefix + "_id";
                var identity = new BsonDocument("_owner", prefix + "_owner");
                foreach (var pk in storage.PartialKeys) identity[pk] = prefix + pk;
                return identity;
            }

            private BsonValue Identity(EntityRef e, Pipeline p) => IdentityOf(e.Type, p.Ref(e.Binding));

            /// <summary>Instances of target whose identity equals e's field (a reference e holds): one, or a collection.</summary>
            private Val Lookup(EntityRef e, TypeDef target, string field, bool many, object step)
            {
                BsonValue Matches(Pipeline inner, string root) =>
                    new BsonDocument("$eq", new BsonArray { IdentityOf(target, root), Ref(e, field, inner) });
                return many ? new Many(q => Start(q, target, Matches)) : Single(e, target, Matches, step);
            }

            /// <summary>Instances of target whose field references e (the reference is on the other side).</summary>
            private Val Referencing(EntityRef e, TypeDef target, string field, bool many, object step)
            {
                BsonValue Matches(Pipeline inner, string root) =>
                    new BsonDocument("$eq", new BsonArray { $"{root}.{field}", IdentityOf(e.Type, inner.Ref(e.Binding)) });
                return many ? new Many(q => Start(q, target, Matches)) : Single(e, target, Matches, step);
            }

            /// <summary>M:N, from the holder: the instances whose identity is in e's array of references.</summary>
            private Val ReferencedBy(EntityRef e, TypeDef target, string field) =>
                new Many(q => Start(q, target, (inner, root) => new BsonDocument("$in", new BsonArray
                {
                    IdentityOf(target, root),
                    new BsonDocument("$ifNull", new BsonArray { Ref(e, field, inner), new BsonArray() })
                })));

            /// <summary>M:N, from the other side: the instances whose array of references contains e.</summary>
            private Val Containing(EntityRef e, TypeDef target, string field, bool many, object step)
            {
                BsonValue Matches(Pipeline inner, string root) => new BsonDocument("$in", new BsonArray
                {
                    IdentityOf(e.Type, inner.Ref(e.Binding)),
                    new BsonDocument("$ifNull", new BsonArray { $"{root}.{field}", new BsonArray() })
                });
                return many ? new Many(q => Start(q, target, Matches)) : Single(e, target, Matches, step);
            }

            /// <summary>Single-valued navigation: a $lookup of one document into a field of e's pipeline, then $first.</summary>
            private Val Single(EntityRef e, TypeDef target, Func<Pipeline, string, BsonValue> matches, object step)
            {
                var owner = e.Binding.Owner;
                if (owner.Navigations.TryGetValue((e.Binding, step), out var known)) return known;
                var id = NewId(target.Name);
                var sub = new Pipeline(owner);
                var (collection, stages) = Source(target);
                sub.Started = true;
                sub.Collection = collection;
                foreach (var s in stages) sub.Stages.Add(s);
                Match(sub, matches(sub, "$$ROOT"));
                sub.Stages.Add(new BsonDocument("$limit", 1));
                owner.Stages.Add(Lookup(sub, id));
                owner.Stages.Add(new BsonDocument("$set", new BsonDocument(id, new BsonDocument("$first", "$" + id))));
                return owner.Navigations[(e.Binding, step)] = new EntityRef(target, new Binding(id, owner));
            }

            /// <summary>
            /// An array inside a document (embedded weak entities, a multivalued attribute) as a collection:
            /// $documents in a sub-pipeline, or $set + $unwind when joined into a pipeline that already has documents.
            /// </summary>
            private Val Embedded(Binding owner, string field, TypeDef? weak)
            {
                return new Many(p =>
                {
                    var id = NewId(weak?.Name ?? "v");
                    var source = $"{p.Ref(owner)}.{field}";
                    // Each element: the weak entity's fields with _owner, or { v: value }.
                    BsonValue element = weak is not null
                        ? new BsonDocument("$mergeObjects", new BsonArray { "$$this", new BsonDocument("_owner", IdentityOf(map.Types[weak].Owner!, p.Ref(owner))) })
                        : new BsonDocument("v", "$$this");
                    var mapped = new BsonDocument("$map", new BsonDocument
                    {
                        ["input"] = new BsonDocument("$ifNull", new BsonArray { source, new BsonArray() }),
                        ["in"] = element
                    });
                    if (!p.Started)
                    {
                        p.Started = true;
                        p.Stages.Add(new BsonDocument("$documents", mapped));
                        p.Stages.Add(new BsonDocument("$replaceRoot", new BsonDocument("newRoot", new BsonDocument(id, "$$ROOT"))));
                    }
                    else
                    {
                        p.Stages.Add(new BsonDocument("$set", new BsonDocument(id, mapped)));
                        p.Stages.Add(new BsonDocument("$unwind", "$" + id));
                    }
                    var binding = new Binding(id, p);
                    return weak is not null ? new EntityRef(weak, binding) : new Scalar($"{p.Ref(binding)}.v");
                });
            }

            private static BsonDocument Lookup(Pipeline sub, string field)
            {
                var let = new BsonDocument();
                foreach (var (name, binding) in sub.Lets) let[name] = sub.Parent!.Ref(binding);
                var lookup = new BsonDocument();
                if (sub.Collection is not null) lookup["from"] = sub.Collection;
                if (let.ElementCount > 0) lookup["let"] = let;
                lookup["pipeline"] = sub.Stages;
                lookup["as"] = field;
                return new BsonDocument("$lookup", lookup);
            }

            // -------------------------------------------------------------- helpers

            private bool IsDate(AttributeDef a) => (a.Type?.Domain?.Type.Builtin ?? a.Type?.Builtin) is BuiltinType.Date or BuiltinType.DateTime;

            private static IEnumerable<TypeDef> Lineage(TypeDef type)
            {
                for (var t = type; t is not null; t = t.SubtypeOf?.Super) yield return t;
            }

            private static IEnumerable<AttributeDef> Attributes(TypeDef type) => Lineage(type).Reverse().SelectMany(t => t.Attributes);

            private string NewId(string name) => $"{char.ToLowerInvariant(name.FirstOrDefault(char.IsLetter, 'v'))}{++_ids}";

            private static string Unit(string unit) => unit.ToLowerInvariant().TrimEnd('s') switch
            {
                "month" => "month",
                "year" => "year",
                "hour" => "hour",
                "minute" => "minute",
                _ => "day"
            };

            private static BsonValue Literal(ERLQParser.LiteralContext l)
            {
                if (l.STRING() is { } s) return new BsonString(ErldModelBuilder.Unquote(s.GetText()));
                if (l.TRUE() is not null) return BsonBoolean.True;
                if (l.FALSE() is not null) return BsonBoolean.False;
                if (l.NULL() is not null) return BsonNull.Value;
                if (l.INT() is { } i) return long.Parse(i.GetText(), CultureInfo.InvariantCulture) is var n && n is >= int.MinValue and <= int.MaxValue ? new BsonInt32((int)n) : new BsonInt64(n);
                return new BsonDouble(double.Parse(l.DECIMAL().GetText(), CultureInfo.InvariantCulture));
            }

            private static string LikeToRegex(string like)
            {
                var sb = new System.Text.StringBuilder("^");
                for (var i = 0; i < like.Length; i++)
                {
                    var ch = like[i];
                    if (ch == '%') sb.Append(".*");
                    else if (ch == '_') sb.Append('.');
                    else if (ch == '[' && like.IndexOf(']', i) is var end and > 0) { sb.Append(like[i..(end + 1)]); i = end; }
                    else sb.Append(System.Text.RegularExpressions.Regex.Escape(ch.ToString()));
                }
                return sb.Append('$').ToString();
            }

            private static string Script(string? collection, BsonArray stages)
            {
                var settings = new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson, Indent = true, IndentChars = "  " };
                var target = collection is null ? "db" : $"db.getCollection(\"{collection}\")";
                return $"{target}.aggregate({stages.ToJson(settings)})".Replace("\r\n", "\n");
            }

            private static string N(ERLQParser.NameContext name) =>
                name.QUOTED_ID() is { } q ? q.GetText()[1..^1] : name.GetText();

            private static string Text(ParserRuleContext ctx) =>
                string.Join(" ", ctx.Start.InputStream.GetText(Interval.Of(ctx.Start.StartIndex, ctx.Stop?.StopIndex ?? ctx.Start.StopIndex))
                    .Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

            private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
        }
    }
}
