using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Texter.Erl.Grammar;

namespace Texter.Erl.Semantics
{
    /// <summary>Errors stop code generation; warnings do not.</summary>
    public record ErldAnalysis(ErldModel? Model, List<ErlSyntaxError> Errors, List<ErlSyntaxError> Warnings)
    {
        public bool Succeeded => Model is not null && Errors.Count == 0;
    }

    /// <summary>
    /// ERLD source -> semantic model. Pass 1 collects the named things (domains, enums, entities, aggregations,
    /// categories); pass 2 resolves types, relationship ends, specializations and rules, and checks the model.
    /// </summary>
    public class ErldModelBuilder
    {
        private static readonly Dictionary<string, BuiltinType> Builtins = new(StringComparer.OrdinalIgnoreCase)
        {
            ["tinyint"] = BuiltinType.TinyInt, ["smallint"] = BuiltinType.SmallInt, ["int"] = BuiltinType.Int,
            ["long"] = BuiltinType.Long, ["decimal"] = BuiltinType.Decimal, ["money"] = BuiltinType.Money,
            ["float"] = BuiltinType.Float, ["bool"] = BuiltinType.Bool, ["char"] = BuiltinType.Char,
            ["string"] = BuiltinType.String, ["text"] = BuiltinType.Text, ["date"] = BuiltinType.Date,
            ["time"] = BuiltinType.Time, ["datetime"] = BuiltinType.DateTime, ["uuid"] = BuiltinType.Uuid
        };

        private readonly ErldModel _model = new();
        private readonly List<ErlSyntaxError> _errors = new();
        private readonly List<ErlSyntaxError> _warnings = new();

        public static ErldAnalysis Analyze(string source)
        {
            var (tree, syntaxErrors) = ErldSyntax.Parse(source);
            if (syntaxErrors.Count > 0) return new ErldAnalysis(null, syntaxErrors, new());

            var builder = new ErldModelBuilder();
            builder.Build(tree);
            return new ErldAnalysis(builder._model, builder._errors, builder._warnings);
        }

        private void Build(ERLDParser.ErldFileContext file)
        {
            if (file.modelDecl() is { } m) _model.Name = Name(m.name());
            var declarations = file.declaration();

            foreach (var d in declarations) Collect(d);
            foreach (var d in declarations) Resolve(d);
            Check();
        }

        // ------------------------------------------------------------------ pass 1

        private void Collect(ERLDParser.DeclarationContext d)
        {
            var body = d.declarationBody();
            var annotations = Annotations(d.annotation());

            if (body.domainDecl() is { } dom)
                Add(_model.Domains, new DomainDef { Name = Name(dom.name()), Pos = Pos(dom), Annotations = annotations }, "domain");
            else if (body.enumDecl() is { } en)
            {
                var def = new EnumDef { Name = Name(en.name()), Pos = Pos(en), Annotations = annotations };
                foreach (var v in en.enumValue()) def.Values.Add((Name(v.name()), v.literal()));
                Add(_model.Enums, def, "enumeration");
            }
            else if (body.entityDecl() is { } ent)
                AddType(new TypeDef
                {
                    Name = Name(ent.name()), Pos = Pos(ent), Annotations = annotations,
                    Kind = ent.WEAK() is not null ? TypeKind.WeakEntity : TypeKind.Entity,
                    DisplayName = DisplayName(ent.displayName())
                });
            else if (body.aggregationDecl() is { } agg)
                AddType(new TypeDef
                {
                    Name = Name(agg.name()), Pos = Pos(agg), Annotations = annotations,
                    Kind = TypeKind.Aggregation, DisplayName = DisplayName(agg.displayName())
                });
            else if (body.categoryDecl() is { } cat)
                AddType(new TypeDef { Name = Name(cat.name(0)), Pos = Pos(cat), Annotations = annotations, Kind = TypeKind.Category });
        }

        private void AddType(TypeDef type)
        {
            if (_model.Domains.Any(d => ErldModel.Same(d.Name, type.Name)) || _model.Enums.Any(e => ErldModel.Same(e.Name, type.Name)))
                Error(type.Pos, $"'{type.Name}' is already declared as a domain or enumeration.");
            Add(_model.Types, type, "entity, aggregation or category");
        }

        private void Add<T>(List<T> list, T element, string what) where T : ErldElement
        {
            if (list.Any(e => ErldModel.Same(e.Name, element.Name)))
                Error(element.Pos, $"Duplicate {what} '{element.Name}'.");
            else
                list.Add(element);
        }

        // ------------------------------------------------------------------ pass 2

        private void Resolve(ERLDParser.DeclarationContext d)
        {
            var body = d.declarationBody();
            if (body.domainDecl() is { } dom) ResolveDomain(dom);
            else if (body.entityDecl() is { } ent) ResolveMembers(Type(ent.name()), ent.entityMember());
            else if (body.relationshipDecl() is { } rel) ResolveRelationship(rel, Annotations(d.annotation()));
            else if (body.aggregationDecl() is { } agg) ResolveAggregation(agg);
            else if (body.specializationDecl() is { } spec) ResolveSpecialization(spec, Annotations(d.annotation()));
            else if (body.categoryDecl() is { } cat) ResolveCategory(cat);
            else if (body.constraintDecl() is { } con)
                _model.Constraints.Add(new ConstraintDef
                {
                    Name = Name(con.name()), Pos = Pos(con), Expression = con.expression(), Text = Source(con)
                });
            else if (body.ruleDecl() is { } rule) ResolveRule(rule);
        }

        private void ResolveDomain(ERLDParser.DomainDeclContext dom)
        {
            var def = _model.Domains.FirstOrDefault(x => ErldModel.Same(x.Name, Name(dom.name())));
            if (def is null || def.Pos != Pos(dom)) return; // duplicate, already reported
            def.Type = TypeRef(dom.dataType(), allowCustom: false);
            foreach (var vc in dom.valueConstraint()) def.Constraints.Add(new ValueConstraintDef { Syntax = vc });
        }

        private void ResolveMembers(TypeDef? type, IEnumerable<ERLDParser.EntityMemberContext> members)
        {
            if (type is null) return;
            foreach (var member in members)
            {
                var body = member.entityMemberBody();
                if (body.attributeDecl() is { } attr)
                    AddAttribute(type.Attributes, Attribute(attr, Annotations(member.annotation())), type.Name);
                else if (body.uniqueDecl() is { } unique)
                {
                    var names = unique.name().Select(Name).ToList();
                    foreach (var n in names.Where(n => !type.Attributes.Any(a => ErldModel.Same(a.Name, n))))
                        Error(Pos(unique), $"unique ({string.Join(", ", names)}) in '{type.Name}': unknown attribute '{n}'.");
                    type.Uniques.Add(names);
                }
                else if (body.localConstraint() is { } check)
                    type.Checks.Add(new LocalCheckDef
                    {
                        Name = check.name() is { } n ? Name(n) : $"check{type.Checks.Count + 1}",
                        Pos = Pos(check),
                        Expression = check.expression(),
                        Message = Message(check.withClause()),
                        Annotations = Annotations(member.annotation())
                    });
            }
        }

        private void AddAttribute(List<AttributeDef> list, AttributeDef attribute, string owner)
        {
            if (list.Any(a => ErldModel.Same(a.Name, attribute.Name)))
                Error(attribute.Pos, $"Duplicate attribute '{attribute.Name}' in '{owner}'.");
            else
                list.Add(attribute);
        }

        private AttributeDef Attribute(ERLDParser.AttributeDeclContext decl, Annotations annotations)
        {
            var attribute = new AttributeDef { Pos = Pos(decl), Annotations = annotations };
            switch (decl)
            {
                case ERLDParser.SimpleAttributeContext s:
                    attribute.Name = Name(s.name());
                    Modifiers(attribute, s.attrModifier());
                    attribute.Type = TypeRef(s.dataType(), allowCustom: true);
                    Options(attribute, s.attrOption());
                    break;
                case ERLDParser.CompositeAttributeContext c:
                    attribute.Name = Name(c.name());
                    Modifiers(attribute, c.attrModifier());
                    foreach (var part in c.attributeDecl()) AddAttribute(attribute.Parts, Attribute(part, new Annotations()), attribute.Name);
                    Options(attribute, c.attrOption());
                    break;
                case ERLDParser.DerivedAttributeContext dv:
                    attribute.Name = Name(dv.name());
                    attribute.IsDerived = true;
                    attribute.Type = TypeRef(dv.dataType(), allowCustom: true);
                    attribute.DerivedExpression = dv.expression();
                    Options(attribute, dv.attrOption());
                    break;
            }
            return attribute;
        }

        private static void Modifiers(AttributeDef attribute, IEnumerable<ERLDParser.AttrModifierContext> modifiers)
        {
            foreach (var m in modifiers)
            {
                if (m.PARTIAL() is not null) attribute.IsPartialKey = true;
                else if (m.KEY() is not null) attribute.IsKey = true;
                else if (m.UNIQUE() is not null) attribute.IsUnique = true;
            }
        }

        private void Options(AttributeDef attribute, IEnumerable<ERLDParser.AttrOptionContext> options)
        {
            foreach (var option in options)
            {
                switch (option)
                {
                    case ERLDParser.OptionalOptionContext: attribute.IsOptional = true; break;
                    case ERLDParser.RequiredOptionContext: attribute.IsOptional = false; break;
                    case ERLDParser.ValueOptionContext v:
                        attribute.Constraints.Add(new ValueConstraintDef { Syntax = v.valueConstraint(), Message = Message(v.withClause()) });
                        break;
                    case ERLDParser.TransitionOptionContext t: attribute.TransitionChecks.Add(Source(t)); break;
                    case ERLDParser.AutoOptionContext a:
                        var ints = a.INT();
                        attribute.Auto = ints.Length == 2 ? (int.Parse(ints[0].GetText()), int.Parse(ints[1].GetText())) : (1, 1);
                        break;
                    case ERLDParser.DefaultOptionContext d: attribute.Default = d.expression(); break;
                    case ERLDParser.CommentOptionContext c: attribute.Comment = Unquote(c.STRING().GetText()); break;
                }
            }
        }

        private TypeRef TypeRef(ERLDParser.DataTypeContext ctx, bool allowCustom)
        {
            var ints = ctx.INT();
            var type = new TypeRef
            {
                Name = Name(ctx.name()),
                Size = ints.Length > 0 ? int.Parse(ints[0].GetText()) : null,
                Scale = ints.Length > 1 ? int.Parse(ints[1].GetText()) : null,
                IsMultivalued = ctx.MULTI() is not null
            };

            if (Builtins.TryGetValue(type.Name, out var builtin)) type.Builtin = builtin;
            else if (allowCustom && _model.Domains.FirstOrDefault(d => ErldModel.Same(d.Name, type.Name)) is { } domain) type.Domain = domain;
            else if (allowCustom && _model.Enums.FirstOrDefault(e => ErldModel.Same(e.Name, type.Name)) is { } en) type.Enum = en;
            else Error(Pos(ctx), $"Unknown type '{type.Name}'.");
            return type;
        }

        private void ResolveRelationship(ERLDParser.RelationshipDeclContext rel, Annotations annotations)
        {
            var def = new RelationshipDef
            {
                Name = Name(rel.name()), Pos = Pos(rel), Annotations = annotations,
                DisplayName = DisplayName(rel.displayName()),
                IsIdentifying = rel.IDENTIFYING() is not null
            };
            if (!Mappings(def, rel.mappingDecl(), rel.shortRelationship())) return;

            if (_model.Relationships.Any(r => ErldModel.Same(r.Name, def.Name)) || _model.FindType(def.Name) is not null)
                Error(def.Pos, $"Duplicate name '{def.Name}'.");
            _model.Relationships.Add(def);
        }

        private void ResolveAggregation(ERLDParser.AggregationDeclContext agg)
        {
            var type = Type(agg.name());
            if (type is null || type.Pos != Pos(agg)) return;

            var def = new RelationshipDef
            {
                Name = type.Name, Pos = type.Pos, Annotations = type.Annotations,
                DisplayName = type.DisplayName, Aggregation = type
            };
            type.Relationship = def;
            ResolveMembers(type, agg.entityMember());
            if (Mappings(def, agg.mappingDecl(), agg.shortRelationship())) _model.Relationships.Add(def);
        }

        /// <summary>Long notation: two mapping blocks. Short notation: A.role [l..u] &lt;-&gt; B.role [l..u].</summary>
        private bool Mappings(RelationshipDef def, ERLDParser.MappingDeclContext[] mappings, ERLDParser.ShortRelationshipContext? shortForm)
        {
            if (mappings.Length == 2)
            {
                def.Mapping1 = Mapping(mappings[0]);
                def.Mapping2 = Mapping(mappings[1]);
            }
            else if (shortForm is not null)
            {
                var (left, right) = (shortForm.relEnd(0), shortForm.relEnd(1));
                def.Mapping1 = Mapping(left, Name(right.name(0)));
                def.Mapping2 = Mapping(right, Name(left.name(0)));
            }
            else return false;

            var ok = true;
            foreach (var m in def.Mappings)
            {
                ok &= ResolveEnd(m, m.DomainName, end => m.Domain = end);
                ok &= ResolveEnd(m, m.CoDomainName, end => m.CoDomain = end);
            }
            if (!ok) return false;

            if (!ErldModel.Same(def.Mapping1.DomainName, def.Mapping2.CoDomainName) || !ErldModel.Same(def.Mapping1.CoDomainName, def.Mapping2.DomainName))
            {
                Error(def.Pos, $"The two mappings of '{def.Name}' must be inverse (A -> B and B -> A).");
                return false;
            }
            if (def.Aggregation is not null && def.Mappings.Any(m => m.Domain == def.Aggregation))
            {
                Error(def.Pos, $"Aggregation '{def.Name}' cannot take part in its own relationship.");
                return false;
            }
            return true;
        }

        private bool ResolveEnd(MappingDef mapping, string name, Action<TypeDef> set)
        {
            var type = _model.FindType(name);
            if (type is null)
            {
                Error(mapping.Pos, $"Unknown entity or aggregation '{name}'.");
                return false;
            }
            set(type);
            return true;
        }

        private MappingDef Mapping(ERLDParser.MappingDeclContext m)
        {
            var mapping = new MappingDef
            {
                Pos = Pos(m), Annotations = Annotations(m.annotation()),
                DomainName = Name(m.name(0)), CoDomainName = Name(m.name(1)),
                Role = m.name().Length > 2 ? Name(m.name(2)) : null
            };
            mapping.Name = mapping.Role ?? mapping.CoDomainName;
            Cardinality(mapping, m.cardinality());
            Actions(mapping, m.mappingAction());
            return mapping;
        }

        private MappingDef Mapping(ERLDParser.RelEndContext end, string coDomain)
        {
            var mapping = new MappingDef
            {
                Pos = Pos(end), DomainName = Name(end.name(0)), CoDomainName = coDomain,
                Role = end.name().Length > 1 ? Name(end.name(1)) : null
            };
            mapping.Name = mapping.Role ?? coDomain;
            Cardinality(mapping, end.cardinality());
            Actions(mapping, end.mappingAction());
            return mapping;
        }

        private void Cardinality(MappingDef mapping, ERLDParser.CardinalityContext c)
        {
            int Bound(ERLDParser.BoundContext b) => b.INT() is { } i ? int.Parse(i.GetText()) : MappingDef.Many;
            mapping.Lower = Bound(c.bound(0));
            mapping.Upper = Bound(c.bound(1));
            if (mapping.Lower == MappingDef.Many)
                Error(mapping.Pos, "The lower bound cannot be *.");
            else if (mapping.Upper != MappingDef.Many && mapping.Lower > mapping.Upper)
                Error(mapping.Pos, $"Lower bound {mapping.Lower} is greater than upper bound {mapping.Upper}.");
            else if (mapping.Upper == 0)
                Error(mapping.Pos, "The upper bound must be at least 1.");
        }

        private static void Actions(MappingDef mapping, IEnumerable<ERLDParser.MappingActionContext> actions)
        {
            foreach (var a in actions)
            {
                var (action, procedure) = Structural(a.structuralAction());
                mapping.Actions.Add((a.structuralEvent().GetText().ToLowerInvariant(), action, procedure));
            }
        }

        private static (StructuralAction, string?) Structural(ERLDParser.StructuralActionContext a) =>
            a.CASCADE() is not null ? (StructuralAction.Cascade, null)
            : a.NULL() is not null ? (StructuralAction.SetNull, null)
            : a.DEFAULT() is not null ? (StructuralAction.SetDefault, null)
            : a.EXEC() is not null ? (StructuralAction.Exec, Name(a.name()))
            : (StructuralAction.Restrict, null);

        private void ResolveSpecialization(ERLDParser.SpecializationDeclContext spec, Annotations annotations)
        {
            // specialization [Name] of Super (...) [discriminator d] { ... }
            var of = spec.OF().Symbol.TokenIndex;
            var disc = spec.DISCRIMINATOR()?.Symbol.TokenIndex ?? int.MaxValue;
            string? specName = null, superName = null, discriminator = null;
            foreach (var n in spec.name())
            {
                var index = n.Start.TokenIndex;
                if (index < of) specName = Name(n);
                else if (index > disc) discriminator = Name(n);
                else superName = Name(n);
            }

            var super = superName is null ? null : _model.FindType(superName);
            if (super is null)
            {
                Error(Pos(spec), $"Unknown supertype '{superName}'.");
                return;
            }

            var def = new SpecializationDef
            {
                Name = specName ?? $"{super.Name}Specialization", Pos = Pos(spec), Annotations = annotations,
                Super = super, Discriminator = discriminator,
                IsDisjoint = !spec.specOption().Any(o => o.OVERLAPPING() is not null),
                IsTotal = spec.specOption().Any(o => o.TOTAL() is not null)
            };
            foreach (var s in spec.subtype())
            {
                var sub = _model.FindType(Name(s.name()));
                if (sub is null) { Error(Pos(s), $"Unknown subtype '{Name(s.name())}'."); continue; }
                if (sub == super) { Error(Pos(s), $"'{sub.Name}' cannot be its own subtype."); continue; }
                if (sub.SubtypeOf is not null) { Error(Pos(s), $"'{sub.Name}' is already a subtype of '{sub.SubtypeOf.Super.Name}'."); continue; }
                if (sub.Kind != TypeKind.Entity) { Error(Pos(s), $"Subtype '{sub.Name}' must be an ordinary entity."); continue; }
                sub.SubtypeOf = def;
                def.Subtypes.Add((sub, s.literal()));
            }
            _model.Specializations.Add(def);
        }

        private void ResolveCategory(ERLDParser.CategoryDeclContext cat)
        {
            var type = Type(cat.name(0));
            if (type is null || type.Pos != Pos(cat)) return;

            var def = new CategoryDef { Name = type.Name, Pos = type.Pos, Type = type, IsTotal = cat.TOTAL() is not null };
            foreach (var n in cat.name().Skip(1))
            {
                if (_model.FindType(Name(n)) is { } member) def.Members.Add(member);
                else Error(Pos(n), $"Unknown entity '{Name(n)}' in category '{type.Name}'.");
            }
            type.Category = def;
            ResolveMembers(type, cat.entityMember());
            _model.Categories.Add(def);
        }

        private void ResolveRule(ERLDParser.RuleDeclContext rule)
        {
            var action = rule.ruleAction();
            var def = new RuleDef
            {
                Name = Name(rule.name(0)), Pos = Pos(rule), Target = rule.path().GetText(),
                Where = rule.expression(), WhereText = rule.expression() is { } w ? Source(w) : null,
                Action = Source(action), Text = Source(rule)
            };
            if (action.structuralAction() is { } s) def.Structural = Structural(s).Item1;
            def.Events.AddRange(rule.ruleEvent().Select(e => e.GetText().ToLowerInvariant()));
            if (rule.withClause() is { } with)
            {
                def.MessageKind = with.messageKind().GetText().ToLowerInvariant();
                def.Message = Message(with);
            }
            if (_model.FindType(def.Target.Split('.')[0]) is null)
                Error(def.Pos, $"Rule '{def.Name}': unknown entity '{def.Target}'.");
            _model.Rules.Add(def);
        }

        // ------------------------------------------------------------------ checks

        private void Check()
        {
            foreach (var type in _model.Types)
            {
                var identifying = _model.Relationships.Where(r => r.IsIdentifying && r.Mappings.Any(m => m.Domain == type)).ToList();
                if (type.Kind == TypeKind.WeakEntity)
                {
                    if (identifying.Count == 0)
                        Error(type.Pos, $"Weak entity '{type.Name}' has no identifying relationship with its owner.");
                    else if (identifying.Count > 1)
                        Warning(type.Pos, $"Weak entity '{type.Name}' has {identifying.Count} identifying relationships; all owners become part of its key.");
                    if (type.Keys.Any())
                        Warning(type.Pos, $"Weak entity '{type.Name}' has a key; a weak entity is identified by its owner and a partial key.");
                }
                else if (type.PartialKeys.Any())
                    Error(type.Pos, $"'{type.Name}' is not a weak entity, so it cannot have a partial key.");

                if (type.SubtypeOf is not null && type.Keys.Any())
                    Warning(type.Pos, $"Subtype '{type.Name}' declares a key; it inherits the key of '{type.SubtypeOf.Super.Name}', so its own is ignored.");
                if (type.Kind == TypeKind.Entity && type.SubtypeOf is null && !type.Keys.Any())
                    Warning(type.Pos, $"Entity '{type.Name}' has no key; a surrogate key is generated.");

                foreach (var a in type.Attributes.Where(a => a.Auto is not null && (a.Type?.Domain?.Type.Builtin ?? a.Type?.Builtin) is not (BuiltinType.TinyInt or BuiltinType.SmallInt or BuiltinType.Int or BuiltinType.Long)))
                    Error(a.Pos, $"auto needs an integer type ('{a.Name}' in '{type.Name}').");
            }

            foreach (var r in _model.Relationships.Where(r => r.IsIdentifying))
            {
                var weakEnds = r.Mappings.Where(m => m.Domain.Kind == TypeKind.WeakEntity).ToList();
                if (weakEnds.Count == 0)
                    Error(r.Pos, $"Identifying relationship '{r.Name}' does not connect a weak entity.");
                else if (weakEnds.Count == 1 && (weakEnds[0].Lower != 1 || weakEnds[0].Upper != 1))
                    Warning(weakEnds[0].Pos, $"In identifying relationship '{r.Name}' the weak entity '{weakEnds[0].Domain.Name}' should map to its owner [1..1].");
            }

            // Hierarchy cycles: A subtype of B subtype of A.
            foreach (var type in _model.Types)
            {
                var seen = new HashSet<TypeDef>();
                for (var t = type; t?.SubtypeOf is not null; t = t.SubtypeOf.Super)
                {
                    if (!seen.Add(t)) { Error(type.Pos, $"Specialization cycle through '{type.Name}'."); break; }
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private TypeDef? Type(ERLDParser.NameContext name) => _model.FindType(Name(name));

        private static Annotations Annotations(IEnumerable<ERLDParser.AnnotationContext> annotations)
        {
            var result = new Annotations();
            foreach (var a in annotations)
                result[Name(a.name())] = a.annotationArg().Select(arg => arg.literal() is { } l ? Unquote(l.GetText()) : Name(arg.name())).ToList();
            return result;
        }

        public static string Name(ERLDParser.NameContext name) =>
            name.QUOTED_ID() is { } q ? q.GetText()[1..^1] : name.GetText();

        private static string? DisplayName(ERLDParser.DisplayNameContext? d) =>
            d is null ? null : d.STRING() is { } s ? Unquote(s.GetText()) : d.QUOTED_ID().GetText()[1..^1];

        private static string? Message(ERLDParser.WithClauseContext? with) =>
            with?.STRING() is { } s ? Unquote(s.GetText()) : null;

        public static string Unquote(string literal) =>
            literal.Length >= 2 && literal[0] == '\'' ? literal[1..^1].Replace("''", "'") : literal;

        /// <summary>Original source text of a node (with its whitespace).</summary>
        public static string Source(ParserRuleContext ctx) =>
            ctx.Start.InputStream.GetText(Interval.Of(ctx.Start.StartIndex, ctx.Stop?.StopIndex ?? ctx.Start.StopIndex));

        private static SourcePos Pos(ParserRuleContext ctx) => new(ctx.Start.Line, ctx.Start.Column + 1);

        private void Error(SourcePos pos, string message) => _errors.Add(new ErlSyntaxError(pos.Line, pos.Column, message));

        private void Warning(SourcePos pos, string message) => _warnings.Add(new ErlSyntaxError(pos.Line, pos.Column, message));
    }
}
