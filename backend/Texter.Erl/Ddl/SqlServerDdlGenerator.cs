using System.Text;
using Texter.Erl.Semantics;

namespace Texter.Erl.Ddl
{
    /// <summary>
    /// TEXTER step 3: ERLD semantic model -> SQL Server DDL.
    ///
    /// Mapping rules:
    ///  - entity, aggregation, category -> table; a whole IS-A hierarchy -> one table (the root's), with a
    ///    discriminator column (disjoint) or one bit column per subtype (overlapping), and CHECKs that keep the
    ///    subtype columns consistent with it;
    ///  - key -> primary key (no key: surrogate IDENTITY); weak entity -> owner key + partial key;
    ///  - relationship with an upper bound 1 on one side -> foreign key on that side (NOT NULL when the lower bound
    ///    is 1); 1:1 -> unique foreign key; M:N -> junction table; aggregation -> table with both foreign keys;
    ///  - multivalued attribute -> table (owner key, value); composite attribute -> one column per part;
    ///  - value constraints, enums, domains, local checks, defaults, derived attributes -> CHECK, DEFAULT, computed
    ///    column, when the row alone can evaluate them;
    ///  - on delete / on update actions (mapping actions, structural rules) -> referential actions; restrict is the
    ///    default. SQL Server rejects cycles and multiple cascade paths: such actions fall back to NO ACTION.
    /// What a table cannot enforce on its own (lower bounds on the "many" side, transition checks, navigation,
    /// named constraints, value rules) is listed at the end of the script as needing a trigger.
    /// </summary>
    public static class SqlServerDdlGenerator
    {
        public static DdlResult Generate(ErldModel model, bool includeAttribution = true) =>
            new Builder(model, includeAttribution).Build();

        public record DdlResult(string Sql, List<string> Warnings);

        // ------------------------------------------------------------------ relational schema

        private sealed class Table(string name, string kind)
        {
            public string Name { get; } = name;
            public string Kind { get; } = kind;
            public string Attribution { get; set; } = "";
            public List<Column> Columns { get; } = new();
            public List<string> PrimaryKey { get; } = new();
            public List<(string Name, List<string> Columns)> Uniques { get; } = new();
            public List<(string Name, List<string> Columns)> FilteredUniques { get; } = new();
            public List<(string Name, string Sql, string? Comment)> Checks { get; } = new();
            public List<ForeignKey> ForeignKeys { get; } = new();
            public List<string> Comments { get; } = new();

            public bool Has(string column) => Columns.Any(c => ErldModel.Same(c.Name, column));
        }

        private sealed class Column(string name, string type)
        {
            public string Name { get; set; } = name;
            public string Type { get; set; } = type;
            public bool Nullable { get; set; }
            public (int Seed, int Increment)? Identity { get; set; }
            public string? Default { get; set; }
            public string? Computed { get; set; }
            public string Attribution { get; set; } = "";
            public string? Comment { get; set; }
        }

        private sealed class ForeignKey
        {
            public string Name { get; set; } = default!;
            public Table Child { get; set; } = default!;
            public Table Parent { get; set; } = default!;
            public List<string> Columns { get; } = new();
            public List<string> ParentColumns { get; } = new();
            public StructuralAction OnDelete { get; set; } = StructuralAction.Restrict;
            public StructuralAction OnUpdate { get; set; } = StructuralAction.Restrict;
            public RelationshipDef? Relationship { get; set; }
            public string? Comment { get; set; }
        }

        /// <summary>A key column of a table, as other tables copy it into their foreign keys.</summary>
        private record KeyColumn(string Name, string Type);

        // ------------------------------------------------------------------ builder

        private sealed class Builder(ErldModel model, bool includeAttribution)
        {
            private readonly Dictionary<TypeDef, Table> _tables = new();
            private readonly Dictionary<TypeDef, List<KeyColumn>> _keys = new();
            private readonly HashSet<TypeDef> _keyInProgress = new();
            private readonly List<Table> _order = new();
            private readonly List<string> _manual = new();
            private readonly List<string> _warnings = new();
            private readonly HashSet<string> _constraintNames = new(StringComparer.OrdinalIgnoreCase);

            public DdlResult Build()
            {
                var roots = model.Types.Where(t => t.SubtypeOf is null).ToList();
                foreach (var type in roots) _tables[type] = NewTable(type.Name, type.Kind switch
                {
                    TypeKind.WeakEntity => "weak entity",
                    TypeKind.Aggregation => "aggregation",
                    TypeKind.Category => "category",
                    _ => HasSubtypes(type) ? "entity with its IS-A hierarchy" : "entity"
                } + (type.DisplayName is { } display ? $" \"{display}\"" : ""), type.Annotations);

                foreach (var type in roots) PrimaryKey(type);
                foreach (var type in roots) Attributes(type);
                foreach (var spec in model.Specializations.Where(s => s.Super.SubtypeOf is null)) Hierarchy(spec, parentCondition: null);
                foreach (var r in model.Relationships) Relationship(r);
                foreach (var category in model.Categories) Category(category);
                ApplyRules();
                CheckCascadePaths();
                return new DdlResult(Render(), _warnings);
            }

            private Table NewTable(string name, string kind, Annotations annotations)
            {
                var table = new Table(UniqueTableName(name), kind) { Attribution = Attribution(annotations) };
                _order.Add(table);
                return table;
            }

            private string UniqueTableName(string name)
            {
                var candidate = name;
                for (var i = 2; _order.Any(t => ErldModel.Same(t.Name, candidate)); i++) candidate = $"{name}{i}";
                return candidate;
            }

            private bool HasSubtypes(TypeDef type) => model.Specializations.Any(s => s.Super == type);

            private TypeDef Root(TypeDef type)
            {
                while (type.SubtypeOf is not null) type = type.SubtypeOf.Super;
                return type;
            }

            private Table TableOf(TypeDef type) => _tables[Root(type)];

            // -------------------------------------------------------------- keys

            /// <summary>Primary key columns of a root type's table (computed once; owners and participants first).</summary>
            private List<KeyColumn> PrimaryKey(TypeDef type)
            {
                type = Root(type);
                if (_keys.TryGetValue(type, out var known)) return known;
                if (!_keyInProgress.Add(type))
                {
                    _warnings.Add($"'{type.Name}' depends on itself for its key; a surrogate key is used.");
                    return Surrogate(type);
                }

                var table = _tables[type];
                var keys = new List<KeyColumn>();
                switch (type.Kind)
                {
                    case TypeKind.WeakEntity:
                        foreach (var r in model.Relationships.Where(r => r.IsIdentifying && r.Mappings.Any(m => m.Domain == type)))
                        {
                            var ownerMapping = r.Mappings.First(m => m.Domain != type);
                            var owner = ownerMapping.Domain;
                            var fk = AddForeignKey(table, owner, ownerMapping.Role ?? owner.Name, nullable: false, r, isKey: true);
                            fk.OnDelete = Action(ownerMapping, "delete");
                            fk.OnUpdate = Action(ownerMapping, "update");
                            keys.AddRange(fk.Columns.Select(c => new KeyColumn(c, table.Columns.First(x => x.Name == c).Type)));
                        }
                        keys.AddRange(KeyAttributes(table, type.PartialKeys));
                        if (keys.Count == 0) return Surrogate(type);
                        break;

                    case TypeKind.Aggregation when !type.Keys.Any():
                        var rel = type.Relationship!;
                        var fks = rel.Mappings.Select(m => (Mapping: m, Fk: ParticipantKey(table, rel, m))).ToList();
                        // X -> Y [..1]: each X takes part at most once, so X alone identifies the instance.
                        var single = fks.Where(x => !x.Mapping.IsMany).ToList();
                        var pk = single.Count > 0 ? single[0].Fk.Columns : fks.SelectMany(x => x.Fk.Columns).ToList();
                        keys.AddRange(pk.Select(c => new KeyColumn(c, table.Columns.First(x => x.Name == c).Type)));
                        foreach (var other in single.Skip(1)) table.Uniques.Add((ConstraintName("UQ", table.Name, other.Fk.Parent.Name), other.Fk.Columns));
                        break;

                    case TypeKind.Aggregation:
                        keys.AddRange(KeyAttributes(table, type.Keys));
                        var r2 = type.Relationship!;
                        var participants = r2.Mappings.Select(m => (Mapping: m, Fk: ParticipantKey(table, r2, m))).ToList();
                        var one = participants.Where(x => !x.Mapping.IsMany).ToList();
                        if (one.Count > 0)
                            foreach (var p in one) table.Uniques.Add((ConstraintName("UQ", table.Name, p.Fk.Parent.Name), p.Fk.Columns));
                        else
                            table.Uniques.Add((ConstraintName("UQ", table.Name, "Pair"), participants.SelectMany(x => x.Fk.Columns).ToList()));
                        break;

                    default:
                        keys.AddRange(KeyAttributes(table, type.Keys));
                        if (keys.Count == 0) return Surrogate(type);
                        break;
                }

                table.PrimaryKey.AddRange(keys.Select(k => k.Name));
                _keyInProgress.Remove(type);
                return _keys[type] = keys;
            }

            private List<KeyColumn> Surrogate(TypeDef type)
            {
                var table = _tables[type];
                var name = FreeColumnName(table, LowerFirst(type.Name) + "Id");
                table.Columns.Insert(0, new Column(name, "int") { Identity = (1, 1), Comment = "surrogate key (no key in ERLD)" });
                table.PrimaryKey.Clear();
                table.PrimaryKey.Add(name);
                _keyInProgress.Remove(type);
                return _keys[type] = new List<KeyColumn> { new(name, "int") };
            }

            private IEnumerable<KeyColumn> KeyAttributes(Table table, IEnumerable<AttributeDef> keys)
            {
                foreach (var key in keys)
                    foreach (var column in AttributeColumns(table, key, nullable: false))
                        yield return new KeyColumn(column.Name, column.Type);
            }

            /// <summary>Foreign key of an aggregation (or junction) table to one participant.</summary>
            private ForeignKey ParticipantKey(Table table, RelationshipDef r, MappingDef m)
            {
                // Column names follow the role that leads to this participant (the inverse mapping's role).
                var inverse = r.Mappings.First(x => x != m);
                var fk = AddForeignKey(table, m.Domain, inverse.Role ?? m.Domain.Name, nullable: false, r, isKey: false);
                // An action on the mapping of the participant applies to the links (the rows of this table).
                fk.OnDelete = Action(m, "delete");
                fk.OnUpdate = Action(m, "update");
                return fk;
            }

            // -------------------------------------------------------------- attributes

            private void Attributes(TypeDef type)
            {
                var table = _tables[type];
                foreach (var a in type.Attributes.Where(a => !a.IsKey && !a.IsPartialKey))
                    Attribute(table, type, a, forceNullable: false);
                Checks(table, type);
                foreach (var u in type.Uniques)
                    table.Uniques.Add((ConstraintName("UQ", table.Name, string.Join("_", u)), u.SelectMany(n => ColumnsOf(table, type, n)).ToList()));
            }

            /// <summary>Adds an attribute (columns, or a table for a multivalued one). Subtype attributes are nullable.</summary>
            private List<Column> Attribute(Table table, TypeDef owner, AttributeDef a, bool forceNullable)
            {
                if (a.Type?.IsMultivalued == true)
                {
                    MultivaluedTable(table, owner, a);
                    return new List<Column>();
                }
                if (a.IsDerived)
                {
                    var column = new Column(FreeColumnName(table, a.Name), SqlType(a.Type)) { Attribution = Attribution(a.Annotations) };
                    var expr = SqlExpression.Translate(a.DerivedExpression!, n => ColumnSql(table, owner, n));
                    if (expr is null)
                    {
                        Manual($"{owner.Name}.{a.Name}: derived attribute '{ErldModelBuilder.Source(a.DerivedExpression!).Trim()}' needs other rows (view or trigger); not generated.");
                        return new List<Column>();
                    }
                    column.Computed = $"CAST({expr} AS {column.Type})";
                    table.Columns.Add(column);
                    return new List<Column> { column };
                }

                var columns = AttributeColumns(table, a, forceNullable || a.IsOptional);
                if (a.IsUnique) table.Uniques.Add((ConstraintName("UQ", table.Name, a.Name), columns.Select(c => c.Name).ToList()));
                foreach (var t in a.TransitionChecks) Manual($"{owner.Name}.{a.Name}: {t.Trim()} (transition check, needs a trigger).");
                return columns;
            }

            /// <summary>Columns of a simple or composite attribute, with type, default, identity and value checks.</summary>
            private List<Column> AttributeColumns(Table table, AttributeDef a, bool nullable, string prefix = "")
            {
                if (a.IsComposite)
                    return a.Parts.SelectMany(p => AttributeColumns(table, p, nullable || p.IsOptional, prefix + (prefix == "" ? a.Name : UpperFirst(a.Name)))).ToList();

                var name = FreeColumnName(table, prefix == "" ? a.Name : prefix + UpperFirst(a.Name));
                var column = new Column(name, SqlType(a.Type))
                {
                    Nullable = nullable,
                    Identity = a.Auto,
                    Attribution = Attribution(a.Annotations),
                    Comment = a.Comment
                };
                if (a.Default is not null)
                {
                    column.Default = SqlExpression.Translate(a.Default, _ => null);
                    if (column.Default is null) Manual($"{table.Name}.{name}: default '{ErldModelBuilder.Source(a.Default).Trim()}' is not a constant expression; not generated.");
                }
                table.Columns.Add(column);
                ValueChecks(table, column, a);
                return new List<Column> { column };
            }

            /// <summary>CHECKs from the attribute's value constraints, its domain and its enumeration.</summary>
            private void ValueChecks(Table table, Column column, AttributeDef a)
            {
                var q = Q(column.Name);
                var constraints = new List<ValueConstraintDef>(a.Constraints);
                if (a.Type?.Domain is { } domain) constraints.InsertRange(0, domain.Constraints);

                foreach (var c in constraints)
                {
                    var sql = SqlExpression.Translate(c.Syntax, q, _ => null);
                    if (sql is null) Manual($"{table.Name}.{column.Name}: '{ErldModelBuilder.Source(c.Syntax).Trim()}' cannot be checked in SQL Server (needs a trigger).");
                    else AddCheck(table, column.Name, column.Nullable ? $"{q} IS NULL OR ({sql})" : sql, c.Message);
                }
                if (a.Type?.Enum is { } en)
                {
                    var values = string.Join(", ", en.Values.Select(v => v.Literal is { } l ? SqlExpression.Literal(l) : $"N'{v.Name.Replace("'", "''")}'"));
                    AddCheck(table, column.Name, column.Nullable ? $"{q} IS NULL OR {q} IN ({values})" : $"{q} IN ({values})", null);
                }
            }

            private void AddCheck(Table table, string what, string sql, string? message) =>
                table.Checks.Add((ConstraintName("CK", table.Name, what), sql, message));

            /// <summary>Local checks of an entity: a CHECK when the row alone decides, otherwise a trigger.</summary>
            private void Checks(Table table, TypeDef type)
            {
                foreach (var check in type.Checks)
                {
                    var sql = SqlExpression.Translate(check.Expression, n => ColumnSql(table, type, n));
                    if (sql is null) Manual($"{type.Name}: check {check.Name}: {ErldModelBuilder.Source(check.Expression).Trim()} (needs other rows, a trigger).");
                    else AddCheck(table, check.Name, sql, check.Message);
                }
            }

            private void MultivaluedTable(Table ownerTable, TypeDef owner, AttributeDef a)
            {
                var table = NewTable(owner.Name + UpperFirst(a.Name), $"multivalued attribute {owner.Name}.{a.Name}", a.Annotations);
                var fk = new ForeignKey { Name = ConstraintName("FK", table.Name, ownerTable.Name), Child = table, Parent = ownerTable, OnDelete = StructuralAction.Cascade };
                foreach (var key in PrimaryKey(owner))
                {
                    table.Columns.Add(new Column(key.Name, key.Type));
                    fk.Columns.Add(key.Name);
                    fk.ParentColumns.Add(key.Name);
                }
                table.ForeignKeys.Add(fk);
                var single = new AttributeDef { Name = a.Name, Type = new TypeRef { Name = a.Type!.Name, Builtin = a.Type.Builtin, Domain = a.Type.Domain, Enum = a.Type.Enum, Size = a.Type.Size, Scale = a.Type.Scale } };
                single.Constraints.AddRange(a.Constraints);
                var value = AttributeColumns(table, single, nullable: false);
                table.PrimaryKey.AddRange(fk.Columns.Concat(value.Select(v => v.Name)));
            }

            // -------------------------------------------------------------- IS-A

            /// <summary>
            /// Subtypes go into the root's table. Disjoint: one discriminator column; overlapping: a bit per subtype.
            /// Subtype columns are nullable, and a CHECK ties them to the discriminator.
            /// </summary>
            private void Hierarchy(SpecializationDef spec, string? parentCondition)
            {
                var table = TableOf(spec.Super);
                var conditions = new Dictionary<TypeDef, string>();

                if (spec.IsDisjoint)
                {
                    var name = spec.Discriminator ?? (model.Specializations.Count(s => Root(s.Super) == Root(spec.Super)) > 1 ? $"tip{spec.Super.Name}" : "tip");
                    var values = spec.Subtypes.Select(s => s.Value is { } v ? SqlExpression.Literal(v) : $"N'{s.Type.Name}'").ToList();
                    var existing = table.Columns.FirstOrDefault(c => ErldModel.Same(c.Name, name));
                    var column = existing ?? new Column(FreeColumnName(table, name), $"nvarchar({Math.Max(1, values.Max(v => v.Length - 3))})")
                    {
                        Comment = $"discriminator of {spec.Super.Name}: {string.Join(", ", spec.Subtypes.Select(s => s.Type.Name))}",
                        Attribution = Attribution(spec.Annotations)
                    };
                    if (existing is null) table.Columns.Add(column);
                    // Total: every instance of the supertype (when it exists in this branch) is in a subtype.
                    column.Nullable = !spec.IsTotal || parentCondition is not null;
                    var q = Q(column.Name);
                    AddCheck(table, column.Name, $"{q} IS NULL OR {q} IN ({string.Join(", ", values)})", null);
                    if (parentCondition is not null)
                        AddCheck(table, column.Name + "Branch", spec.IsTotal
                            ? $"({parentCondition} AND {q} IS NOT NULL) OR (NOT ({parentCondition}) AND {q} IS NULL)"
                            : $"{parentCondition} OR {q} IS NULL", null);
                    for (var i = 0; i < spec.Subtypes.Count; i++) conditions[spec.Subtypes[i].Type] = $"{q} = {values[i]}";
                }
                else
                {
                    var flags = new List<string>();
                    foreach (var (sub, _) in spec.Subtypes)
                    {
                        var column = new Column(FreeColumnName(table, "je" + sub.Name), "bit")
                        {
                            Default = "0",
                            Comment = $"instance is a {sub.Name} (overlapping specialization of {spec.Super.Name})",
                            Attribution = Attribution(spec.Annotations)
                        };
                        table.Columns.Add(column);
                        flags.Add($"{Q(column.Name)} = 1");
                        conditions[sub] = $"{Q(column.Name)} = 1";
                        if (parentCondition is not null) AddCheck(table, column.Name + "Branch", $"{parentCondition} OR {Q(column.Name)} = 0", null);
                    }
                    if (spec.IsTotal)
                    {
                        var any = string.Join(" OR ", flags);
                        AddCheck(table, spec.Super.Name + "Total", parentCondition is null ? any : $"NOT ({parentCondition}) OR {any}", null);
                    }
                }

                foreach (var (sub, _) in spec.Subtypes)
                {
                    var condition = conditions[sub];
                    foreach (var a in sub.Attributes.Where(a => !a.IsKey && !a.IsPartialKey))
                    {
                        var columns = Attribute(table, sub, a, forceNullable: true);
                        foreach (var c in columns.Where(c => c.Computed is null))
                        {
                            var q = Q(c.Name);
                            // Required in the subtype: present exactly when the row is of that subtype.
                            AddCheck(table, c.Name, a.IsOptional
                                ? $"{condition} OR {q} IS NULL"
                                : $"({condition} AND {q} IS NOT NULL) OR (NOT ({condition}) AND {q} IS NULL)", null);
                        }
                    }
                    Checks(table, sub);
                    foreach (var nested in model.Specializations.Where(s => s.Super == sub)) Hierarchy(nested, condition);
                }
            }

            // -------------------------------------------------------------- relationships

            private void Relationship(RelationshipDef r)
            {
                if (r.IsIdentifying || r.IsAggregation)
                {
                    // Weak keys and aggregation tables are built with the keys; only the bounds are left.
                    foreach (var m in r.Mappings)
                    {
                        LowerBound(m, r);
                        UpperBound(m, r);
                    }
                    return;
                }

                var (m1, m2) = (r.Mapping1, r.Mapping2);
                if (m1.IsMany && m2.IsMany)
                {
                    Junction(r);
                    return;
                }

                // The foreign key goes into the table of the side that maps to at most one instance.
                // 1:1: the side whose lower bound is 1 (it must have the other), else the first mapping.
                var holder = !m1.IsMany && m2.IsMany ? m1
                    : m1.IsMany && !m2.IsMany ? m2
                    : m1.Lower == 1 || m2.Lower != 1 ? m1 : m2;
                var other = holder == m1 ? m2 : m1;

                var table = TableOf(holder.Domain);
                var fk = AddForeignKey(table, holder.CoDomain, holder.Role ?? holder.CoDomain.Name, nullable: holder.Lower == 0, r, isKey: false);
                fk.OnDelete = Action(other, "delete");
                fk.OnUpdate = Action(other, "update");
                SubtypeComment(fk, holder.Domain, holder.CoDomain);

                // Subtype column inside a hierarchy table: a required FK is only required for rows of that subtype.
                if (holder.Domain.SubtypeOf is not null && holder.Lower == 1)
                {
                    foreach (var c in fk.Columns) table.Columns.First(x => x.Name == c).Nullable = true;
                    Manual($"{holder.Domain.Name} -> {holder.CoDomain.Name} [1..1] ({r.Name}): required only for {holder.Domain.Name} rows of {table.Name}; not enforced.");
                }

                if (!other.IsMany)
                {
                    if (holder.Lower == 0 || holder.Domain.SubtypeOf is not null) table.FilteredUniques.Add((ConstraintName("UX", table.Name, string.Join("_", fk.Columns)), fk.Columns));
                    else table.Uniques.Add((ConstraintName("UQ", table.Name, string.Join("_", fk.Columns)), fk.Columns));
                }

                LowerBound(other, r);
                UpperBound(holder, r);
                UpperBound(other, r);
            }

            /// <summary>M:N without attributes: a table with the two foreign keys as primary key.</summary>
            private void Junction(RelationshipDef r)
            {
                var table = NewTable(r.Name, "M:N relationship", r.Annotations);
                var fks = r.Mappings.Select(m => ParticipantKey(table, r, m)).ToList();
                table.PrimaryKey.AddRange(fks.SelectMany(f => f.Columns));
                foreach (var m in r.Mappings)
                {
                    LowerBound(m, r);
                    UpperBound(m, r);
                    SubtypeComment(fks[m == r.Mapping1 ? 0 : 1], m.CoDomain, m.Domain);
                }
            }

            private void SubtypeComment(ForeignKey fk, params TypeDef[] ends)
            {
                foreach (var end in ends.Where(e => e.SubtypeOf is not null))
                    fk.Comment = (fk.Comment is null ? "" : fk.Comment + "; ") + $"{end.Name} is a subtype stored in {TableOf(end).Name}: its discriminator is not checked here";
            }

            private void LowerBound(MappingDef m, RelationshipDef r)
            {
                if (m.Lower >= 1 && m.IsMany)
                    Manual($"{m.DomainName} -> {m.CoDomainName} [{m.Lower}..{Bound(m.Upper)}] ({r.Name}): every {m.DomainName} needs at least {m.Lower} {m.CoDomainName} (needs a trigger).");
            }

            private void UpperBound(MappingDef m, RelationshipDef r)
            {
                if (m.Upper > 1)
                    Manual($"{m.DomainName} -> {m.CoDomainName} [{m.Lower}..{m.Upper}] ({r.Name}): at most {m.Upper} (needs a trigger).");
            }

            /// <summary>Foreign key from <paramref name="table"/> to the table of <paramref name="target"/>.</summary>
            private ForeignKey AddForeignKey(Table table, TypeDef target, string role, bool nullable, RelationshipDef r, bool isKey)
            {
                var parent = TableOf(target);
                var keys = PrimaryKey(target);
                var fk = new ForeignKey { Child = table, Parent = parent, Relationship = r };
                // The same table referenced twice, or a key name already used here: prefix the role.
                var prefix = table.ForeignKeys.Any(f => f.Parent == parent) || parent == table || keys.Any(k => table.Has(k.Name));
                foreach (var key in keys)
                {
                    var name = FreeColumnName(table, prefix ? LowerFirst(role) + UpperFirst(key.Name) : key.Name);
                    table.Columns.Add(new Column(name, key.Type)
                    {
                        Nullable = nullable,
                        Attribution = Attribution(r.Annotations),
                        Comment = $"{r.Name} -> {target.Name}" + (isKey ? " (part of the key)" : "")
                    });
                    fk.Columns.Add(name);
                    fk.ParentColumns.Add(key.Name);
                }
                fk.Name = ConstraintName("FK", table.Name, prefix ? role : parent.Name);
                table.ForeignKeys.Add(fk);
                return fk;
            }

            /// <summary>Action on the co-domain when the domain is deleted / updated (from the mapping).</summary>
            private static StructuralAction Action(MappingDef m, string @event) =>
                m.Actions.Where(a => a.Event == @event).Select(a => a.Action).DefaultIfEmpty(StructuralAction.Restrict).First();

            // -------------------------------------------------------------- categories

            /// <summary>Category: its own table; each member's table gets an optional unique foreign key to it.</summary>
            private void Category(CategoryDef category)
            {
                foreach (var member in category.Members)
                {
                    var table = TableOf(member);
                    var fk = new ForeignKey { Child = table, Parent = _tables[category.Type], Comment = $"{member.Name} is a {category.Name}" };
                    foreach (var key in PrimaryKey(category.Type))
                    {
                        var name = FreeColumnName(table, LowerFirst(category.Name) + UpperFirst(key.Name));
                        table.Columns.Add(new Column(name, key.Type) { Nullable = true, Comment = $"category {category.Name}" });
                        fk.Columns.Add(name);
                        fk.ParentColumns.Add(key.Name);
                    }
                    fk.Name = ConstraintName("FK", table.Name, category.Name);
                    table.ForeignKeys.Add(fk);
                    table.FilteredUniques.Add((ConstraintName("UX", table.Name, category.Name), fk.Columns));
                }
                Manual($"category {category.Name}: every {category.Name} belongs to {(category.IsTotal ? "exactly" : "at most")} one of {string.Join(", ", category.Members.Select(m => m.Name))} (needs a trigger).");
            }

            // -------------------------------------------------------------- rules

            /// <summary>
            /// Structural rules (rule R on E for delete where Rel do cascade) set the referential action of Rel's
            /// foreign keys to E. Value rules and named constraints need triggers.
            /// </summary>
            private void ApplyRules()
            {
                foreach (var rule in model.Rules)
                {
                    var target = model.FindType(rule.Target.Split('.')[0]);
                    var relationship = rule.WhereText is { } w ? model.Relationships.FirstOrDefault(r => ErldModel.Same(r.Name, w.Trim())) : null;
                    if (rule.Structural is { } action && target is not null && relationship is not null && action != StructuralAction.Exec)
                    {
                        var fks = _order.SelectMany(t => t.ForeignKeys).Where(f => f.Relationship == relationship && f.Parent == TableOf(target)).ToList();
                        foreach (var fk in fks)
                        {
                            if (rule.Events.Contains("delete")) fk.OnDelete = action;
                            if (rule.Events.Contains("update")) fk.OnUpdate = action;
                            fk.Comment = (fk.Comment is null ? "" : fk.Comment + "; ") + $"rule {rule.Name}" + (rule.Message is null ? "" : $": {rule.MessageKind} '{rule.Message}'");
                        }
                        if (fks.Count > 0) continue;
                    }
                    Manual($"{OneLine(rule.Text)} (needs a trigger).");
                }
                foreach (var c in model.Constraints) Manual($"constraint {c.Name}: {OneLine(ErldModelBuilder.Source(c.Expression))} (needs a trigger).");
            }

            /// <summary>
            /// SQL Server rejects a foreign key whose cascading action makes a cycle or a second cascade path between
            /// two tables. Such actions fall back to NO ACTION, with a warning.
            /// </summary>
            private void CheckCascadePaths()
            {
                foreach (var @event in new[] { "delete", "update" })
                {
                    var edges = new List<(Table From, Table To)>();
                    foreach (var fk in _order.SelectMany(t => t.ForeignKeys))
                    {
                        var action = @event == "delete" ? fk.OnDelete : fk.OnUpdate;
                        if (action is StructuralAction.Restrict or StructuralAction.Exec) continue;

                        var reach = Reachability(edges);
                        var ancestors = _order.Where(t => t == fk.Parent || reach[t].Contains(fk.Parent)).ToList();
                        var descendants = reach[fk.Child].Append(fk.Child).ToList();
                        var conflict = fk.Parent == fk.Child
                            || reach[fk.Child].Contains(fk.Parent)
                            || ancestors.Any(a => descendants.Any(d => a != d && reach[a].Contains(d)));
                        if (!conflict && action == StructuralAction.SetNull && fk.Columns.Any(c => !fk.Child.Columns.First(x => x.Name == c).Nullable))
                        {
                            _warnings.Add($"{fk.Name}: on {@event} set null needs nullable columns; restrict is used.");
                            conflict = true;
                        }
                        if (conflict)
                        {
                            if (@event == "delete") fk.OnDelete = StructuralAction.Restrict; else fk.OnUpdate = StructuralAction.Restrict;
                            var why = $"on {@event} {Words(action)} is not allowed by SQL Server here (cycle or multiple cascade paths); NO ACTION is used";
                            fk.Comment = (fk.Comment is null ? "" : fk.Comment + "; ") + why;
                            _warnings.Add($"{fk.Name}: {why}.");
                            Manual($"{fk.Child.Name} -> {fk.Parent.Name}: on {@event} {Words(action)} (needs a trigger).");
                            continue;
                        }
                        edges.Add((fk.Parent, fk.Child));
                    }
                }
            }

            private Dictionary<Table, HashSet<Table>> Reachability(List<(Table From, Table To)> edges)
            {
                var reach = _order.ToDictionary(t => t, _ => new HashSet<Table>());
                foreach (var t in _order)
                {
                    var stack = new Stack<Table>(edges.Where(e => e.From == t).Select(e => e.To));
                    while (stack.Count > 0)
                    {
                        var next = stack.Pop();
                        if (reach[t].Add(next)) foreach (var e in edges.Where(e => e.From == next)) stack.Push(e.To);
                    }
                }
                return reach;
            }

            // -------------------------------------------------------------- render

            private string Render()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"-- SQL Server DDL for model {model.Name}, generated by TEXTER from ERLD.");
                sb.AppendLine("-- Tables first, then foreign keys, unique indexes and what needs a trigger.");

                foreach (var table in _order)
                {
                    sb.AppendLine();
                    if (table.Attribution.Length > 0) sb.AppendLine($"-- {table.Attribution}");
                    sb.AppendLine($"-- {table.Kind}");
                    sb.AppendLine($"CREATE TABLE {Q(table.Name)} (");
                    var lines = new List<(string Sql, string? Comment)>();
                    foreach (var c in table.Columns) lines.Add((ColumnSql(c), ColumnComment(c)));
                    if (table.PrimaryKey.Count > 0)
                        lines.Add(($"CONSTRAINT {Q(ConstraintName("PK", table.Name))} PRIMARY KEY ({List(table.PrimaryKey)})", null));
                    foreach (var (name, columns) in table.Uniques.DistinctBy(u => string.Join(",", u.Columns)))
                        if (!columns.SequenceEqual(table.PrimaryKey)) lines.Add(($"CONSTRAINT {Q(name)} UNIQUE ({List(columns)})", null));
                    foreach (var (name, sql, message) in table.Checks)
                        lines.Add(($"CONSTRAINT {Q(name)} CHECK ({sql})", message is null ? null : $"err '{message}'"));

                    for (var i = 0; i < lines.Count; i++)
                    {
                        var comma = i < lines.Count - 1 ? "," : "";
                        sb.Append("    ").Append(lines[i].Sql).Append(comma);
                        if (lines[i].Comment is { Length: > 0 } comment) sb.Append(" -- ").Append(OneLine(comment));
                        sb.AppendLine();
                    }
                    sb.AppendLine(");");
                }

                var fks = _order.SelectMany(t => t.ForeignKeys).ToList();
                if (fks.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("-- ---------- foreign keys ----------");
                    foreach (var fk in fks)
                    {
                        sb.AppendLine();
                        if (fk.Comment is not null) sb.AppendLine($"-- {OneLine(fk.Comment)}");
                        sb.Append($"ALTER TABLE {Q(fk.Child.Name)} ADD CONSTRAINT {Q(fk.Name)} FOREIGN KEY ({List(fk.Columns)}) REFERENCES {Q(fk.Parent.Name)} ({List(fk.ParentColumns)})");
                        if (fk.OnDelete != StructuralAction.Restrict) sb.Append($" ON DELETE {Words(fk.OnDelete).ToUpperInvariant()}");
                        if (fk.OnUpdate != StructuralAction.Restrict) sb.Append($" ON UPDATE {Words(fk.OnUpdate).ToUpperInvariant()}");
                        sb.AppendLine(";");
                    }
                }

                var filtered = _order.SelectMany(t => t.FilteredUniques.Select(u => (Table: t, u.Name, u.Columns))).ToList();
                if (filtered.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("-- ---------- unique when present (a UNIQUE constraint would allow only one NULL) ----------");
                    foreach (var (table, name, columns) in filtered)
                        sb.AppendLine($"CREATE UNIQUE INDEX {Q(name)} ON {Q(table.Name)} ({List(columns)}) WHERE {string.Join(" AND ", columns.Select(c => $"{Q(c)} IS NOT NULL"))};");
                }

                if (_manual.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("-- ---------- not enforced by this script (need triggers or application logic) ----------");
                    foreach (var line in _manual.Distinct()) sb.AppendLine($"-- {line}");
                }
                // LF on every platform: the editor counts lines in what the textarea holds.
                return sb.ToString().Replace("\r\n", "\n");
            }

            private string ColumnSql(Column c)
            {
                if (c.Computed is not null) return $"{Q(c.Name)} AS {c.Computed}";
                var sql = $"{Q(c.Name)} {c.Type}";
                if (c.Identity is { } id) sql += $" IDENTITY({id.Seed}, {id.Increment})";
                sql += c.Nullable ? " NULL" : " NOT NULL";
                if (c.Default is not null) sql += $" DEFAULT {c.Default}";
                return sql;
            }

            private string? ColumnComment(Column c)
            {
                var parts = new[] { includeAttribution ? c.Attribution : "", c.Comment ?? "" }.Where(p => p.Length > 0).ToList();
                return parts.Count == 0 ? null : OneLine(string.Join(" ", parts));
            }

            // -------------------------------------------------------------- names and types

            /// <summary>Column of an attribute of the row (for CHECK / computed expressions), or null.</summary>
            private string? ColumnSql(Table table, TypeDef owner, string attribute)
            {
                var columns = ColumnsOf(table, owner, attribute);
                return columns.Count == 1 ? Q(columns[0]) : null;
            }

            private List<string> ColumnsOf(Table table, TypeDef owner, string attribute)
            {
                var a = owner.Attributes.FirstOrDefault(x => ErldModel.Same(x.Name, attribute));
                if (a is null) return table.Has(attribute) ? new List<string> { table.Columns.First(c => ErldModel.Same(c.Name, attribute)).Name } : new();
                if (a.IsComposite) return table.Columns.Where(c => c.Name.StartsWith(a.Name, StringComparison.OrdinalIgnoreCase)).Select(c => c.Name).ToList();
                return table.Has(a.Name) ? new List<string> { table.Columns.First(c => ErldModel.Same(c.Name, a.Name)).Name } : new();
            }

            private string FreeColumnName(Table table, string name)
            {
                var candidate = name;
                for (var i = 2; table.Has(candidate); i++) candidate = $"{name}{i}";
                if (candidate != name) _warnings.Add($"Column '{name}' already exists in {table.Name}; '{candidate}' is used.");
                return candidate;
            }

            private string ConstraintName(string prefix, string table, string? what = null)
            {
                var name = what is null ? $"{prefix}_{table}" : $"{prefix}_{table}_{what}";
                var candidate = name;
                for (var i = 2; !_constraintNames.Add(candidate); i++) candidate = $"{name}{i}";
                return candidate;
            }

            private static string SqlType(TypeRef? type)
            {
                if (type is null) return "nvarchar(255)";
                if (type.Domain is { } d) return SqlType(d.Type);
                if (type.Enum is { } e)
                {
                    var literals = e.Values.Select(v => v.Literal).ToList();
                    if (literals.All(l => l?.INT() is not null)) return "int";
                    var longest = e.Values.Max(v => v.Literal is { } l ? ErldModelBuilder.Unquote(l.GetText()).Length : v.Name.Length);
                    return $"nvarchar({Math.Max(1, longest)})";
                }
                return type.Builtin switch
                {
                    BuiltinType.TinyInt => "tinyint",
                    BuiltinType.SmallInt => "smallint",
                    BuiltinType.Int => "int",
                    BuiltinType.Long => "bigint",
                    BuiltinType.Decimal => $"decimal({type.Size ?? 18}, {type.Scale ?? (type.Size is null ? 2 : 0)})",
                    BuiltinType.Money => "money",
                    BuiltinType.Float => "float",
                    BuiltinType.Bool => "bit",
                    BuiltinType.Char => $"nchar({type.Size ?? 1})",
                    BuiltinType.String => $"nvarchar({type.Size ?? 255})",
                    BuiltinType.Text => "nvarchar(max)",
                    BuiltinType.Date => "date",
                    BuiltinType.Time => "time",
                    BuiltinType.DateTime => "datetime2",
                    BuiltinType.Uuid => "uniqueidentifier",
                    _ => "nvarchar(255)"
                };
            }

            private string Attribution(Annotations annotations) => includeAttribution ? annotations.Attribution : "";

            private void Manual(string line) => _manual.Add(OneLine(line));

            private static string Words(StructuralAction a) => a switch
            {
                StructuralAction.Cascade => "cascade",
                StructuralAction.SetNull => "set null",
                StructuralAction.SetDefault => "set default",
                _ => "no action"
            };

            private static string Bound(int b) => b == MappingDef.Many ? "*" : b.ToString();
            private static string OneLine(string text) => string.Join(" ", text.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
            private static string List(IEnumerable<string> columns) => string.Join(", ", columns.Select(Q));
            public static string Q(string name) => "[" + name.Replace("]", "]]") + "]";
            private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
            private static string UpperFirst(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
        }
    }
}
