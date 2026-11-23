using Texter.Erl.Grammar;

namespace Texter.Erl.Semantics
{
    /// <summary>
    /// Semantic model of an ERLD file: names and types resolved, short notation expanded into the long one.
    /// Expressions stay as parse trees; generators (DDL, later ERLQ) translate them.
    /// </summary>
    public class ErldModel
    {
        public string Name { get; set; } = "Model";
        public List<DomainDef> Domains { get; } = new();
        public List<EnumDef> Enums { get; } = new();

        /// <summary>Entities, aggregations and categories: everything that has instances and attributes.</summary>
        public List<TypeDef> Types { get; } = new();

        public List<RelationshipDef> Relationships { get; } = new();
        public List<SpecializationDef> Specializations { get; } = new();
        public List<CategoryDef> Categories { get; } = new();
        public List<ConstraintDef> Constraints { get; } = new();
        public List<RuleDef> Rules { get; } = new();

        public TypeDef? FindType(string name) => Types.FirstOrDefault(t => Same(t.Name, name));

        public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Position in the ERLD source, for messages.</summary>
    public record SourcePos(int Line, int Column);

    /// <summary>@name(args) annotations, e.g. @source(S1, S2) @confidence(stated).</summary>
    public class Annotations : Dictionary<string, List<string>>
    {
        public Annotations() : base(StringComparer.OrdinalIgnoreCase) { }

        /// <summary>The TEXTER attribution as written in ERLD: "@source(S1, S2) @confidence(stated)", or empty.</summary>
        public string Attribution =>
            string.Join(" ", new[] { "source", "confidence" }
                .Where(ContainsKey)
                .Select(k => $"@{k}({string.Join(", ", this[k])})"));
    }

    public abstract class ErldElement
    {
        public string Name { get; set; } = default!;
        public SourcePos Pos { get; set; } = new(0, 0);
        public Annotations Annotations { get; set; } = new();
    }

    public class DomainDef : ErldElement
    {
        public TypeRef Type { get; set; } = default!;
        public List<ValueConstraintDef> Constraints { get; } = new();
    }

    public class EnumDef : ErldElement
    {
        /// <summary>Stored values: the literal when given (PLATA = 'P'), otherwise the name.</summary>
        public List<(string Name, ERLDParser.LiteralContext? Literal)> Values { get; } = new();
    }

    public enum BuiltinType
    {
        None, TinyInt, SmallInt, Int, Long, Decimal, Money, Float, Bool, Char, String, Text, Date, Time, DateTime, Uuid
    }

    /// <summary>Type of an attribute or domain: a built-in type, a domain or an enumeration.</summary>
    public class TypeRef
    {
        public string Name { get; set; } = default!;
        public int? Size { get; set; }
        public int? Scale { get; set; }
        public bool IsMultivalued { get; set; }

        public BuiltinType Builtin { get; set; }
        public DomainDef? Domain { get; set; }
        public EnumDef? Enum { get; set; }
    }

    /// <summary>between / like / in / compare / check(...) on a single value ("value").</summary>
    public class ValueConstraintDef
    {
        public ERLDParser.ValueConstraintContext Syntax { get; set; } = default!;
        public string? Message { get; set; }
        public string Text => Syntax.GetText();
    }

    public class AttributeDef : ErldElement
    {
        public TypeRef? Type { get; set; }
        public bool IsKey { get; set; }
        public bool IsPartialKey { get; set; }
        public bool IsUnique { get; set; }
        public bool IsOptional { get; set; }

        public bool IsDerived { get; set; }
        public ERLDParser.ExpressionContext? DerivedExpression { get; set; }

        /// <summary>auto / auto(seed, increment).</summary>
        public (int Seed, int Increment)? Auto { get; set; }
        public ERLDParser.ExpressionContext? Default { get; set; }
        public string? Comment { get; set; }
        public List<ValueConstraintDef> Constraints { get; } = new();

        /// <summary>on update check (...) – transition constraints (need a trigger).</summary>
        public List<string> TransitionChecks { get; } = new();

        /// <summary>Parts of a composite attribute (adresa { ulica; grad; }).</summary>
        public List<AttributeDef> Parts { get; } = new();
        public bool IsComposite => Parts.Count > 0;
    }

    public class LocalCheckDef : ErldElement
    {
        public ERLDParser.ExpressionContext Expression { get; set; } = default!;
        public string? Message { get; set; }
    }

    public enum TypeKind
    {
        Entity,
        WeakEntity,
        /// <summary>Relationship that is also an entity (has attributes, takes part in other relationships).</summary>
        Aggregation,
        /// <summary>Union type over several supertypes.</summary>
        Category
    }

    /// <summary>Entity, weak entity, aggregation or category.</summary>
    public class TypeDef : ErldElement
    {
        public TypeKind Kind { get; set; }
        public string? DisplayName { get; set; }
        public List<AttributeDef> Attributes { get; } = new();
        public List<List<string>> Uniques { get; } = new();
        public List<LocalCheckDef> Checks { get; } = new();

        /// <summary>The relationship an aggregation is.</summary>
        public RelationshipDef? Relationship { get; set; }

        /// <summary>The category declaration of a category.</summary>
        public CategoryDef? Category { get; set; }

        /// <summary>Supertype of a subtype (from a specialization), if any.</summary>
        public SpecializationDef? SubtypeOf { get; set; }

        public IEnumerable<AttributeDef> Keys => Attributes.Where(a => a.IsKey);
        public IEnumerable<AttributeDef> PartialKeys => Attributes.Where(a => a.IsPartialKey);
    }

    public class RelationshipDef : ErldElement
    {
        public string? DisplayName { get; set; }
        public bool IsIdentifying { get; set; }
        public bool IsAggregation => Aggregation is not null;

        /// <summary>For an aggregation: its entity side (attributes, keys, checks).</summary>
        public TypeDef? Aggregation { get; set; }

        public MappingDef Mapping1 { get; set; } = default!;
        public MappingDef Mapping2 { get; set; } = default!;

        public IEnumerable<MappingDef> Mappings => new[] { Mapping1, Mapping2 };
    }

    public enum StructuralAction { Restrict, Cascade, SetNull, SetDefault, Exec }

    /// <summary>Mapping Domain -> CoDomain [Lower..Upper] as Role, with structural actions applied to the co-domain.</summary>
    public class MappingDef : ErldElement
    {
        public string DomainName { get; set; } = default!;
        public string CoDomainName { get; set; } = default!;
        public TypeDef Domain { get; set; } = default!;
        public TypeDef CoDomain { get; set; } = default!;
        public int Lower { get; set; }

        /// <summary>-1 means * (many).</summary>
        public int Upper { get; set; }
        public string? Role { get; set; }

        /// <summary>on delete cascade, on update set null, ... (event on the domain, action on the co-domain).</summary>
        public List<(string Event, StructuralAction Action, string? Procedure)> Actions { get; } = new();

        public bool IsMany => Upper < 0 || Upper > 1;
        public const int Many = -1;
    }

    public class SpecializationDef : ErldElement
    {
        public TypeDef Super { get; set; } = default!;
        public List<(TypeDef Type, ERLDParser.LiteralContext? Value)> Subtypes { get; } = new();
        public bool IsDisjoint { get; set; }
        public bool IsTotal { get; set; }
        public string? Discriminator { get; set; }
    }

    public class CategoryDef : ErldElement
    {
        public TypeDef Type { get; set; } = default!;
        public List<TypeDef> Members { get; } = new();
        public bool IsTotal { get; set; }
    }

    public class ConstraintDef : ErldElement
    {
        public ERLDParser.ExpressionContext Expression { get; set; } = default!;
        public string Text { get; set; } = default!;
    }

    public class RuleDef : ErldElement
    {
        public string Target { get; set; } = default!;
        public List<string> Events { get; } = new();
        public ERLDParser.ExpressionContext? Where { get; set; }
        public string? WhereText { get; set; }

        /// <summary>restrict, cascade, set null, set default, exec p, reject, recalculate.</summary>
        public string Action { get; set; } = default!;
        public StructuralAction? Structural { get; set; }
        public string? MessageKind { get; set; }
        public string? Message { get; set; }
        public string Text { get; set; } = default!;
    }
}
