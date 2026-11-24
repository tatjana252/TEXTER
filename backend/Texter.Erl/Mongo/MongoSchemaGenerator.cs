using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using Texter.Erl.Grammar;
using Texter.Erl.Semantics;

namespace Texter.Erl.Mongo
{
    /// <summary>
    /// TEXTER step 3 for MongoDB: ERLD semantic model -> collections with $jsonSchema validators and unique indexes.
    ///
    /// Mapping rules (embedding by ownership):
    ///  - entity, aggregation, category -> collection; _id is the key (a composite key: an object of its parts;
    ///    no key: a surrogate int); a whole IS-A hierarchy -> the root's collection, with the discriminator field
    ///    (disjoint) or a bool per subtype (overlapping);
    ///  - weak entity -> array of embedded documents in its owner (field: the owner's role); referenced from
    ///    elsewhere by { _owner: owner's _id, partial key };
    ///  - multivalued attribute -> array of values; composite attribute -> embedded object;
    ///  - relationship with an upper bound 1 on one side -> a reference field (the _id of the other side) on that
    ///    side, as the foreign key in SQL Server; M:N -> an array of references in mapping 1's domain;
    ///    aggregation -> its own collection with a reference per participant (without its own key: _id is the pair);
    ///  - types, lengths, enums, domains and simple value constraints -> $jsonSchema; key and unique -> _id and
    ///    unique indexes.
    /// MongoDB has no foreign keys: references, lower bounds, cascades and check expressions are listed at the end
    /// of the script as not enforced.
    /// </summary>
    public static partial class MongoSchemaGenerator
    {
        /// <param name="Script">mongosh script (for reading, copying, running by hand).</param>
        /// <param name="NotEnforced">What the database itself does not check.</param>
        public record MongoSchemaResult(string Script, MongoMap Map, List<string> NotEnforced, List<string> Warnings);

        public static MongoSchemaResult Generate(ErldModel model, bool dropExisting = false) =>
            new Builder(model).Build(dropExisting);

        private sealed class Builder(ErldModel model)
        {
            private readonly MongoMap _map = new();
            private readonly List<string> _manual = new();
            private readonly List<string> _warnings = new();

            /// <summary>Extra fields of a type's document or element: references it holds, embedded arrays.</summary>
            private readonly Dictionary<TypeDef, List<(string Name, Func<BsonDocument> Schema, bool Required)>> _extra = new();
            private readonly Dictionary<TypeDef, List<BsonDocument>> _indexes = new();

            public MongoSchemaResult Build(bool dropExisting)
            {
                // 1. Where each type lives.
                foreach (var type in model.Types)
                {
                    if (type.Kind == TypeKind.WeakEntity && Identifying(type) is { } r)
                    {
                        var ownerMapping = r.Mappings.First(m => m.Domain != type);
                        var field = ownerMapping.Role ?? LowerFirst(type.Name) + "s";
                        _map.Types[type] = new MongoTypeStorage("", MongoTypeKind.Embedded, field, ownerMapping.Domain,
                            type.PartialKeys.Select(a => a.Name).ToList(), new List<(string, BsonValue)>());
                        _map.Relationships[r] = new MongoRelationshipStorage { Kind = MongoRelationshipKind.Embedded, Holder = ownerMapping, Field = field };
                        if (model.Relationships.Count(x => x.IsIdentifying && x.Mappings.Any(m => m.Domain == type)) > 1)
                            _warnings.Add($"Weak entity '{type.Name}' has several owners; it is embedded in '{ownerMapping.Domain.Name}' only.");
                    }
                    else
                        _map.Types[type] = new MongoTypeStorage(Root(type).Name, MongoTypeKind.Document, null, null, new List<string>(), Conditions(type));
                }
                // Embedded types live in the collection of their (root) owner.
                foreach (var type in model.Types.Where(t => _map.Types[t].Kind == MongoTypeKind.Embedded))
                    _map.Types[type] = _map.Types[type] with { Collection = CollectionOf(type) };

                // 2. Attribute paths.
                foreach (var type in model.Types) Paths(type);

                // 3. Relationships: references, arrays of references, aggregation collections.
                foreach (var r in model.Relationships) Relationship(r);
                foreach (var category in model.Categories) Category(category);
                Notes();

                // 4. Collections.
                foreach (var root in model.Types.Where(t => t.SubtypeOf is null && _map.Types[t].Kind == MongoTypeKind.Document))
                {
                    var schema = DocumentSchema(root, isRoot: true);
                    var dates = new List<string>();
                    DatePaths(schema, "", dates);
                    _map.Collections.Add(new MongoCollectionInfo(root.Name, schema, _indexes.GetValueOrDefault(root) ?? new(), dates));
                }
                return new MongoSchemaResult(Render(dropExisting), _map, _manual.Distinct().ToList(), _warnings);
            }

            // -------------------------------------------------------------- placement

            private RelationshipDef? Identifying(TypeDef weak) =>
                model.Relationships.FirstOrDefault(r => r.IsIdentifying && r.Mappings.Any(m => m.Domain == weak) && r.Mappings.Any(m => m.Domain != weak));

            private static TypeDef Root(TypeDef type)
            {
                while (type.SubtypeOf is not null) type = type.SubtypeOf.Super;
                return type;
            }

            /// <summary>Collection of a type: its root's, or (embedded) its owner's.</summary>
            private string CollectionOf(TypeDef type)
            {
                for (var seen = 0; seen < 16; seen++)
                {
                    var storage = _map.Types[type];
                    if (storage.Kind == MongoTypeKind.Document) return Root(type).Name;
                    type = storage.Owner!;
                }
                throw new InvalidOperationException("Ownership cycle.");
            }

            /// <summary>Subtype: discriminator value or flag of each specialization up to the root.</summary>
            private List<(string, BsonValue)> Conditions(TypeDef type)
            {
                var result = new List<(string, BsonValue)>();
                for (var t = type; t.SubtypeOf is { } spec; t = spec.Super)
                {
                    if (spec.IsDisjoint)
                    {
                        var value = spec.Subtypes.First(s => s.Type == t).Value;
                        result.Insert(0, (Discriminator(spec), value is null ? new BsonString(t.Name) : Literal(value) ?? new BsonString(t.Name)));
                    }
                    else result.Insert(0, ("je" + t.Name, BsonBoolean.True));
                }
                return result;
            }

            private string Discriminator(SpecializationDef spec) =>
                spec.Discriminator ?? (model.Specializations.Count(s => Root(s.Super) == Root(spec.Super)) > 1 ? $"tip{spec.Super.Name}" : "tip");

            private void Paths(TypeDef type)
            {
                var keys = type.Kind == TypeKind.WeakEntity ? new List<AttributeDef>() : type.Keys.ToList();
                foreach (var a in type.Attributes)
                {
                    if (a.Type?.IsMultivalued == true) { _map.Arrays[a] = a.Name; continue; }
                    var prefix = keys.Count == 1 && keys[0] == a ? "_id" : keys.Contains(a) ? $"_id.{a.Name}" : a.Name;
                    if (a.IsComposite)
                        foreach (var part in a.Parts) _map.Fields[part] = $"{prefix}.{part.Name}";
                    else if (!a.IsDerived)
                        _map.Fields[a] = prefix;
                }
            }

            private void Relationship(RelationshipDef r)
            {
                if (r.IsIdentifying && _map.Relationships.ContainsKey(r)) { Bounds(r); return; }

                if (r.Aggregation is { } aggregation)
                {
                    var storage = new MongoRelationshipStorage { Kind = MongoRelationshipKind.AggregationCollection };
                    var ownKey = aggregation.Keys.Any();
                    foreach (var m in r.Mappings)
                    {
                        var name = FreeField(aggregation, LowerFirst(m.Domain.Name));
                        storage.EndFields[m] = ownKey ? name : $"_id.{name}";
                        var target = m.Domain;
                        if (ownKey) Extra(aggregation, name, () => IdSchema(target), required: true);
                    }
                    _map.Relationships[r] = storage;
                    if (ownKey)
                    {
                        var single = r.Mappings.Where(m => !m.IsMany).ToList();
                        if (single.Count > 0)
                            foreach (var m in single) Index(aggregation, $"UQ_{aggregation.Name}_{m.Domain.Name}", new BsonDocument(storage.EndFields[m], 1), unique: true);
                        else
                            Index(aggregation, $"UQ_{aggregation.Name}_Pair", new BsonDocument(r.Mappings.Select(m => new BsonElement(storage.EndFields[m], 1))), unique: true);
                    }
                    Bounds(r);
                    return;
                }

                var (m1, m2) = (r.Mapping1, r.Mapping2);
                if (m1.IsMany && m2.IsMany)
                {
                    var field = FreeField(m1.Domain, m1.Role ?? LowerFirst(m1.CoDomainName));
                    var target = m1.CoDomain;
                    Extra(m1.Domain, field, () => new BsonDocument { ["bsonType"] = "array", ["items"] = IdSchema(target) }, required: false);
                    _map.Relationships[r] = new MongoRelationshipStorage { Kind = MongoRelationshipKind.ReferenceArray, Holder = m1, Field = field };
                    Bounds(r);
                    return;
                }

                // As the foreign key in SQL Server: on the side that maps to at most one instance.
                var holder = !m1.IsMany && m2.IsMany ? m1
                    : m1.IsMany && !m2.IsMany ? m2
                    : m1.Lower == 1 || m2.Lower != 1 ? m1 : m2;
                var other = holder == m1 ? m2 : m1;
                var refField = FreeField(holder.Domain, holder.Role ?? LowerFirst(holder.CoDomainName));
                var refTarget = holder.CoDomain;
                Extra(holder.Domain, refField, () => IdSchema(refTarget), required: holder.Lower == 1 && holder.Domain.SubtypeOf is null);
                _map.Relationships[r] = new MongoRelationshipStorage { Kind = MongoRelationshipKind.Reference, Holder = holder, Field = refField };
                if (!other.IsMany && _map.Types[holder.Domain].Kind == MongoTypeKind.Document)
                    Index(Root(holder.Domain), $"UQ_{Root(holder.Domain).Name}_{refField}", new BsonDocument(refField, 1), unique: true, sparse: true);
                Bounds(r);
            }

            private void Bounds(RelationshipDef r)
            {
                foreach (var m in r.Mappings)
                {
                    if (m.Lower >= 1 && m.IsMany)
                        Manual($"{m.DomainName} -> {m.CoDomainName} [{m.Lower}..{(m.Upper < 0 ? "*" : m.Upper.ToString())}] ({r.Name}): every {m.DomainName} needs at least {m.Lower} {m.CoDomainName}.");
                    if (m.Upper > 1)
                        Manual($"{m.DomainName} -> {m.CoDomainName} [{m.Lower}..{m.Upper}] ({r.Name}): at most {m.Upper}.");
                    foreach (var (ev, action, _) in m.Actions.Where(a => a.Action != StructuralAction.Restrict))
                        if (!(r.IsIdentifying && ev == "delete" && action == StructuralAction.Cascade))
                            Manual($"{m.DomainName} -> {m.CoDomainName} ({r.Name}): on {ev} {action.ToString().ToLowerInvariant()} (application logic).");
                }
            }

            private void Category(CategoryDef category)
            {
                foreach (var member in category.Members)
                {
                    var target = category.Type;
                    var field = FreeField(member, LowerFirst(category.Name));
                    Extra(member, field, () => IdSchema(target), required: false);
                }
                Manual($"category {category.Name}: every {category.Name} belongs to {(category.IsTotal ? "exactly" : "at most")} one of {string.Join(", ", category.Members.Select(m => m.Name))}.");
            }

            private void Notes()
            {
                if (model.Relationships.Any(r => !r.IsIdentifying))
                    Manual("References (fields holding another document's _id) are not checked: MongoDB has no foreign keys.");
                foreach (var type in model.Types)
                {
                    foreach (var a in type.Attributes)
                    {
                        if (a.IsDerived) Manual($"{type.Name}.{a.Name}: derived attribute, not stored (computed in queries).");
                        foreach (var t in a.TransitionChecks) Manual($"{type.Name}.{a.Name}: {OneLine(t)} (transition check).");
                    }
                    foreach (var check in type.Checks) Manual($"{type.Name}: check {check.Name}: {OneLine(ErldModelBuilder.Source(check.Expression))}.");
                    if (_map.Types[type].Kind == MongoTypeKind.Embedded && type.PartialKeys.Any())
                        Manual($"{type.Name}: partial key ({string.Join(", ", type.PartialKeys.Select(p => p.Name))}) unique within one {_map.Types[type].Owner!.Name} (not checked inside an array).");
                }
                foreach (var c in model.Constraints) Manual($"constraint {c.Name}: {OneLine(ErldModelBuilder.Source(c.Expression))}.");
                foreach (var rule in model.Rules) Manual($"{OneLine(rule.Text)}");
            }

            // -------------------------------------------------------------- schemas

            /// <summary>Schema of a document (root) or of an embedded element, with subtypes and embedded types.</summary>
            private BsonDocument DocumentSchema(TypeDef type, bool isRoot)
            {
                var properties = new BsonDocument();
                var required = new List<string>();

                if (isRoot && _map.Types[type].Kind == MongoTypeKind.Document)
                {
                    properties["_id"] = IdSchema(type);
                    required.Add("_id");
                }
                AddMembers(type, properties, required, optional: false);

                // Subtypes: their fields are optional in the shared collection; anyOf ties them to the discriminator.
                var alternatives = new BsonArray();
                foreach (var spec in model.Specializations.Where(s => s.Super == type)) Subtypes(spec, properties, alternatives);

                var schema = new BsonDocument { ["bsonType"] = "object" };
                if (required.Count > 0) schema["required"] = new BsonArray(required);
                schema["properties"] = properties;
                if (alternatives.Count > 0) schema["anyOf"] = alternatives;
                return schema;
            }

            private void AddMembers(TypeDef type, BsonDocument properties, List<string> required, bool optional)
            {
                var keys = type.Kind == TypeKind.WeakEntity ? new List<AttributeDef>() : type.Keys.ToList();
                foreach (var a in type.Attributes.Where(a => !a.IsDerived && !keys.Contains(a)))
                {
                    properties[a.Name] = AttributeSchema(a, type);
                    if (!optional && !a.IsOptional && a.Type?.IsMultivalued != true) required.Add(a.Name);
                    if (a.IsUnique && _map.Types[type].Kind == MongoTypeKind.Document)
                        Index(Root(type), $"UQ_{Root(type).Name}_{a.Name}", new BsonDocument(a.Name, 1), unique: true, sparse: optional || a.IsOptional);
                }
                foreach (var unique in type.Uniques.Where(_ => _map.Types[type].Kind == MongoTypeKind.Document))
                    Index(Root(type), $"UQ_{Root(type).Name}_{string.Join("_", unique)}",
                        new BsonDocument(unique.Select(n => new BsonElement(PathOf(type, n), 1))), unique: true, sparse: optional);
                foreach (var (name, schema, req) in _extra.GetValueOrDefault(type) ?? new())
                {
                    properties[name] = schema();
                    if (req && !optional) required.Add(name);
                }
                // Weak entities owned by this type: arrays of embedded documents.
                foreach (var weak in model.Types.Where(t => _map.Types[t] is { Kind: MongoTypeKind.Embedded } s && s.Owner == type))
                    properties[_map.Types[weak].ArrayField!] = new BsonDocument
                    {
                        ["bsonType"] = "array",
                        ["description"] = $"weak entity {weak.Name}",
                        ["items"] = DocumentSchema(weak, isRoot: false)
                    };
            }

            private string PathOf(TypeDef type, string attribute) =>
                type.Attributes.FirstOrDefault(a => ErldModel.Same(a.Name, attribute)) is { } a && _map.Fields.TryGetValue(a, out var p) ? p : attribute;

            private void Subtypes(SpecializationDef spec, BsonDocument properties, BsonArray alternatives)
            {
                if (spec.IsDisjoint)
                {
                    var field = Discriminator(spec);
                    var values = spec.Subtypes.Select(s => s.Value is { } v ? Literal(v) ?? new BsonString(s.Type.Name) : new BsonString(s.Type.Name)).ToList();
                    if (!properties.Contains(field))
                        properties[field] = new BsonDocument { ["enum"] = new BsonArray(values), ["description"] = $"discriminator of {spec.Super.Name}" };
                    for (var i = 0; i < spec.Subtypes.Count; i++)
                    {
                        var sub = spec.Subtypes[i].Type;
                        var subRequired = new List<string> { field };
                        AddMembers(sub, properties, subRequired, optional: true);
                        subRequired.AddRange(sub.Attributes.Where(a => !a.IsOptional && !a.IsDerived && a.Type?.IsMultivalued != true && !a.IsKey).Select(a => a.Name));
                        alternatives.Add(new BsonDocument
                        {
                            ["properties"] = new BsonDocument(field, new BsonDocument("enum", new BsonArray { values[i] })),
                            ["required"] = new BsonArray(subRequired.Distinct())
                        });
                        foreach (var nested in model.Specializations.Where(s => s.Super == sub))
                        {
                            var ignored = new BsonArray();
                            Subtypes(nested, properties, ignored);
                            Manual($"{nested.Super.Name}: the nested specialization ({string.Join(", ", nested.Subtypes.Select(s => s.Type.Name))}) is not checked by the validator.");
                        }
                    }
                    // Partial: an instance of none of the subtypes has no discriminator.
                    if (!spec.IsTotal) alternatives.Add(new BsonDocument("not", new BsonDocument("required", new BsonArray { field })));
                }
                else
                {
                    foreach (var (sub, _) in spec.Subtypes)
                    {
                        properties["je" + sub.Name] = new BsonDocument { ["bsonType"] = "bool", ["description"] = $"instance is a {sub.Name}" };
                        AddMembers(sub, properties, new List<string>(), optional: true);
                    }
                    Manual($"{spec.Super.Name}: overlapping subtypes ({string.Join(", ", spec.Subtypes.Select(s => s.Type.Name))}): their required fields are not checked.");
                }
            }

            /// <summary>Schema of _id: the key (a composite key: an object), the participants of an aggregation without key, or a surrogate int.</summary>
            private BsonDocument IdSchema(TypeDef type)
            {
                var storage = _map.Types[type];
                if (storage.Kind == MongoTypeKind.Embedded)
                {
                    var properties = new BsonDocument { ["_owner"] = IdSchema(storage.Owner!) };
                    foreach (var pk in type.PartialKeys) properties[pk.Name] = AttributeSchema(pk, type);
                    return new BsonDocument
                    {
                        ["bsonType"] = "object",
                        ["description"] = $"reference to a {type.Name} inside its {storage.Owner!.Name}",
                        ["required"] = new BsonArray(properties.Names),
                        ["properties"] = properties
                    };
                }

                var root = Root(type);
                var keys = root.Keys.ToList();
                if (keys.Count == 1) return AttributeSchema(keys[0], root);
                if (keys.Count > 1)
                    return new BsonDocument
                    {
                        ["bsonType"] = "object",
                        ["required"] = new BsonArray(keys.Select(k => k.Name)),
                        ["properties"] = new BsonDocument(keys.Select(k => new BsonElement(k.Name, AttributeSchema(k, root))))
                    };
                if (root.Relationship is { } r && _map.Relationships.TryGetValue(r, out var agg) && agg.EndFields.Count > 0)
                    return new BsonDocument
                    {
                        ["bsonType"] = "object",
                        ["required"] = new BsonArray(agg.EndFields.Values.Select(f => f["_id.".Length..])),
                        ["properties"] = new BsonDocument(r.Mappings.Select(m => new BsonElement(agg.EndFields[m]["_id.".Length..], IdSchema(m.Domain))))
                    };
                return new BsonDocument { ["bsonType"] = "int", ["description"] = "surrogate key (no key in ERLD)" };
            }

            private BsonDocument AttributeSchema(AttributeDef a, TypeDef owner)
            {
                if (a.IsComposite)
                {
                    var parts = new BsonDocument(a.Parts.Select(p => new BsonElement(p.Name, AttributeSchema(p, owner))));
                    var req = a.Parts.Where(p => !p.IsOptional).Select(p => p.Name).ToList();
                    var composite = new BsonDocument { ["bsonType"] = "object", ["properties"] = parts };
                    if (req.Count > 0) composite.InsertAt(1, new BsonElement("required", new BsonArray(req)));
                    return composite;
                }

                var schema = TypeSchema(a.Type);
                var constraints = new List<ValueConstraintDef>();
                if (a.Type?.Domain is { } domain) constraints.AddRange(domain.Constraints);
                constraints.AddRange(a.Constraints);
                foreach (var c in constraints)
                    if (!ApplyConstraint(schema, c.Syntax))
                        Manual($"{owner.Name}.{a.Name}: '{OneLine(ErldModelBuilder.Source(c.Syntax))}' is not checked by the validator.");
                if (a.Comment is not null) schema["description"] = a.Comment;
                if (a.Auto is not null) Manual($"{owner.Name}.{a.Name}: auto numbering is up to the application (MongoDB has no identity).");
                if (a.Default is not null) Manual($"{owner.Name}.{a.Name}: default value is up to the application.");

                return a.Type?.IsMultivalued == true ? new BsonDocument { ["bsonType"] = "array", ["items"] = schema } : schema;
            }

            private static BsonDocument TypeSchema(TypeRef? type)
            {
                if (type is null) return new BsonDocument("bsonType", "string");
                if (type.Domain is { } d) return TypeSchema(d.Type);
                if (type.Enum is { } e)
                    return new BsonDocument("enum", new BsonArray(e.Values.Select(v => v.Literal is { } l ? Literal(l) ?? new BsonString(v.Name) : new BsonString(v.Name))));
                var schema = new BsonDocument("bsonType", type.Builtin switch
                {
                    BuiltinType.TinyInt or BuiltinType.SmallInt or BuiltinType.Int => (BsonValue)"int",
                    BuiltinType.Long => new BsonArray { "int", "long" },
                    BuiltinType.Decimal or BuiltinType.Money or BuiltinType.Float => "number",
                    BuiltinType.Bool => "bool",
                    BuiltinType.Date or BuiltinType.DateTime => "date",
                    _ => "string"
                });
                if (type.Builtin is BuiltinType.String or BuiltinType.Char && type.Size is { } size) schema["maxLength"] = size;
                if (type.Builtin == BuiltinType.Char && type.Size is { } fixedSize) schema["minLength"] = fixedSize;
                if (type.Builtin == BuiltinType.Time) schema["pattern"] = "^[0-2][0-9]:[0-5][0-9](:[0-5][0-9])?$";
                return schema;
            }

            /// <summary>between / compare / in / like with literal values -> minimum, maximum, enum, pattern.</summary>
            private static bool ApplyConstraint(BsonDocument schema, ERLDParser.ValueConstraintContext c)
            {
                var not = c.GetChild(0).GetText().Equals("not", StringComparison.OrdinalIgnoreCase);
                switch (c)
                {
                    case ERLDParser.BetweenConstraintContext b when !not && Constant(b.expression(0)) is { } lo && Constant(b.expression(1)) is { } hi:
                        schema["minimum"] = lo;
                        schema["maximum"] = hi;
                        return true;
                    case ERLDParser.CompareConstraintContext cmp when Constant(cmp.expression()) is { } v:
                        switch (cmp.compOp().GetText())
                        {
                            case ">": schema["minimum"] = v; schema["exclusiveMinimum"] = true; return true;
                            case ">=": schema["minimum"] = v; return true;
                            case "<": schema["maximum"] = v; schema["exclusiveMaximum"] = true; return true;
                            case "<=": schema["maximum"] = v; return true;
                            case "=": schema["enum"] = new BsonArray { v }; return true;
                            case "<>" or "!=": schema["not"] = new BsonDocument("enum", new BsonArray { v }); return true;
                        }
                        return false;
                    case ERLDParser.InConstraintContext i:
                        var values = i.expressionList().expression().Select(Constant).ToList();
                        if (values.Any(x => x is null)) return false;
                        if (not) schema["not"] = new BsonDocument("enum", new BsonArray(values));
                        else schema["enum"] = new BsonArray(values);
                        return true;
                    case ERLDParser.LikeConstraintContext l when !not:
                        schema["pattern"] = LikeToRegex(ErldModelBuilder.Unquote(l.STRING().GetText()));
                        return true;
                    default:
                        return false;
                }
            }

            private static BsonValue? Constant(ERLDParser.ExpressionContext e) => e switch
            {
                ERLDParser.LiteralExprContext l => Literal(l.literal()),
                ERLDParser.NegateExprContext n when Constant(n.expression()) is { } v && v.IsNumeric => v.IsInt32 ? -v.AsInt32 : v.IsInt64 ? -v.AsInt64 : -v.ToDouble(),
                ERLDParser.ParenExprContext p when p.expressionList().expression().Length == 1 => Constant(p.expressionList().expression(0)),
                _ => null
            };

            private static BsonValue? Literal(ERLDParser.LiteralContext l)
            {
                if (l.STRING() is { } s) return new BsonString(ErldModelBuilder.Unquote(s.GetText()));
                if (l.TRUE() is not null) return BsonBoolean.True;
                if (l.FALSE() is not null) return BsonBoolean.False;
                if (l.NULL() is not null) return BsonNull.Value;
                if (l.INT() is { } i) return long.TryParse(i.GetText(), out var n) && n is >= int.MinValue and <= int.MaxValue ? new BsonInt32((int)n) : new BsonInt64(long.Parse(i.GetText()));
                if (l.DECIMAL() is { } d) return new BsonDouble(double.Parse(d.GetText(), CultureInfo.InvariantCulture));
                return null;
            }

            /// <summary>SQL LIKE (% _ [a-z]) -> anchored regular expression.</summary>
            public static string LikeToRegex(string like)
            {
                var sb = new StringBuilder("^");
                for (var i = 0; i < like.Length; i++)
                {
                    var ch = like[i];
                    if (ch == '%') sb.Append(".*");
                    else if (ch == '_') sb.Append('.');
                    else if (ch == '[' && like.IndexOf(']', i) is var end and > 0)
                    {
                        sb.Append(like[i..(end + 1)]);
                        i = end;
                    }
                    else sb.Append(Regex.Escape(ch.ToString()));
                }
                return sb.Append('$').ToString();
            }

            private static void DatePaths(BsonDocument schema, string prefix, List<string> result)
            {
                if (!schema.TryGetValue("properties", out var properties)) return;
                foreach (var p in properties.AsBsonDocument)
                {
                    var path = prefix + p.Name;
                    var field = p.Value.AsBsonDocument;
                    if (field.TryGetValue("bsonType", out var t) && t == "date") result.Add(path);
                    if (field.TryGetValue("items", out var items) && items.IsBsonDocument)
                    {
                        if (items.AsBsonDocument.TryGetValue("bsonType", out var it) && it == "date") result.Add(path);
                        DatePaths(items.AsBsonDocument, path + ".", result);
                    }
                    DatePaths(field, path + ".", result);
                }
                if (schema.TryGetValue("anyOf", out _)) { }
            }

            // -------------------------------------------------------------- helpers

            private void Extra(TypeDef type, string name, Func<BsonDocument> schema, bool required)
            {
                if (!_extra.TryGetValue(type, out var list)) _extra[type] = list = new();
                list.Add((name, schema, required));
            }

            private string FreeField(TypeDef type, string name)
            {
                var taken = Lineage(type).SelectMany(t => t.Attributes.Select(a => a.Name))
                    .Concat(Lineage(type).SelectMany(t => _extra.GetValueOrDefault(t)?.Select(e => e.Name) ?? Enumerable.Empty<string>()))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var candidate = name;
                for (var i = 2; taken.Contains(candidate) || candidate == "_id"; i++) candidate = $"{name}{i}";
                return candidate;
            }

            private static IEnumerable<TypeDef> Lineage(TypeDef type)
            {
                for (var t = type; t is not null; t = t.SubtypeOf?.Super) yield return t;
            }

            private void Index(TypeDef collectionType, string name, BsonDocument keys, bool unique, bool sparse = false)
            {
                if (!_indexes.TryGetValue(collectionType, out var list)) _indexes[collectionType] = list = new();
                if (list.Any(i => i["name"] == name)) return;
                var index = new BsonDocument { ["key"] = keys, ["name"] = name, ["unique"] = unique };
                // Optional fields: unique only where present (a missing field would otherwise count as one null).
                if (sparse)
                    index["partialFilterExpression"] = new BsonDocument(keys.Names.Select(n => new BsonElement(n, new BsonDocument("$exists", true))));
                list.Add(index);
            }

            private void Manual(string line) => _manual.Add(line);

            // -------------------------------------------------------------- render

            private string Render(bool dropExisting)
            {
                var settings = new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson, Indent = true, IndentChars = "  " };
                var sb = new StringBuilder();
                sb.AppendLine($"// MongoDB schema for model {model.Name}, generated by TEXTER from ERLD (mongosh).");
                sb.AppendLine("// Collections with $jsonSchema validators, then unique indexes, then what MongoDB does not check.");
                sb.AppendLine("// Run it in the database you want, e.g.:  use Biblioteka");
                if (dropExisting)
                {
                    sb.AppendLine();
                    sb.AppendLine("// ---------- drop the collections of this script if they exist (their data is lost) ----------");
                    foreach (var c in _map.Collections) sb.AppendLine($"db.getCollection({Js(c.Name)}).drop();");
                }
                foreach (var c in _map.Collections)
                {
                    sb.AppendLine();
                    var validator = new BsonDocument("$jsonSchema", c.Schema).ToJson(settings);
                    sb.AppendLine($"db.createCollection({Js(c.Name)}, {{");
                    sb.AppendLine("  validator: " + Indent(validator, "  ") + ",");
                    sb.AppendLine("  validationLevel: \"strict\"");
                    sb.AppendLine("});");
                    foreach (var index in c.Indexes)
                    {
                        var options = new BsonDocument { ["name"] = index["name"], ["unique"] = index["unique"] };
                        if (index.Contains("partialFilterExpression")) options["partialFilterExpression"] = index["partialFilterExpression"];
                        var compact = new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson };
                        sb.AppendLine($"db.getCollection({Js(c.Name)}).createIndex({index["key"].AsBsonDocument.ToJson(compact)}, {options.ToJson(compact)});");
                    }
                }
                if (_manual.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("// ---------- not checked by MongoDB (application logic) ----------");
                    foreach (var line in _manual.Distinct()) sb.AppendLine($"// {line}");
                }
                return sb.ToString().Replace("\r\n", "\n");
            }

            private static string Js(string name) => "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            private static string Indent(string text, string indent) => text.Replace("\r\n", "\n").Replace("\n", "\n" + indent);
            private static string OneLine(string text) => string.Join(" ", text.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
            private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
        }
    }
}
