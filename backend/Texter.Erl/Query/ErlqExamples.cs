using Texter.Erl.Ddl;
using Texter.Erl.Semantics;

namespace Texter.Erl.Query
{
    /// <summary>A few ERLQ queries built from the model, to start from in step 4. Only those that translate are kept.</summary>
    public static class ErlqExamples
    {
        public static List<string> For(ErldModel model, RelationalMap map)
        {
            var candidates = new List<string>();
            var entities = model.Types.Where(t => t.Kind is TypeKind.Entity or TypeKind.WeakEntity).ToList();
            string? Plain(TypeDef t) => Lineage(t).Reverse().SelectMany(x => x.Attributes)
                .FirstOrDefault(a => !a.IsComposite && !a.IsDerived && a.Type?.IsMultivalued != true && !a.IsKey && !a.IsPartialKey)?.Name
                ?? Lineage(t).Reverse().SelectMany(x => x.Attributes).FirstOrDefault(a => !a.IsComposite && a.Type?.IsMultivalued != true)?.Name;
            string Var(TypeDef t) => char.ToLowerInvariant(t.Name[0]).ToString();
            string Role(MappingDef m) => m.Role ?? char.ToLowerInvariant(m.CoDomainName[0]) + m.CoDomainName[1..];

            if (entities.FirstOrDefault() is { } first)
                candidates.Add(first.Name);

            // An entity with a role to many: one value and how many related instances.
            var toMany = model.Relationships.SelectMany(r => r.Mappings).FirstOrDefault(m => m.IsMany && Plain(m.Domain) is not null && m.Domain.Kind != TypeKind.Aggregation);
            if (toMany is not null)
            {
                var v = Var(toMany.Domain);
                candidates.Add($"from {v} in {toMany.Domain.Name}\nselect {v}.{Plain(toMany.Domain)}, card({v}.{Role(toMany)}) as broj\norder by broj desc");
                if (Plain(toMany.CoDomain) is { } related)
                    candidates.Add($"from {v} in {toMany.Domain.Name}\nwhere exists x in {v}.{Role(toMany)} : x.{related} is not null\nselect {v}.{Plain(toMany.Domain)}");
            }

            // Single-valued navigation, used for grouping.
            var toOne = model.Relationships.Where(r => !r.IsAggregation).SelectMany(r => r.Mappings)
                .FirstOrDefault(m => !m.IsMany && Plain(m.CoDomain) is not null && m.Domain.Kind != TypeKind.Aggregation);
            if (toOne is not null)
            {
                var v = Var(toOne.Domain);
                var path = $"{v}.{Role(toOne)}.{Plain(toOne.CoDomain)}";
                candidates.Add($"from {v} in {toOne.Domain.Name}\ngroup by {path}\nselect {path}, count({v}) as broj");
            }

            // An aggregation: its participants and its own attributes.
            if (model.Relationships.FirstOrDefault(r => r.IsAggregation) is { } aggregation)
            {
                var a = Var(aggregation.Aggregation!);
                var parts = aggregation.Mappings.Select(m => (m, Plain(m.Domain))).Where(x => x.Item2 is not null)
                    .Select(x => $"{a}.{char.ToLowerInvariant(x.m.Domain.Name[0])}{x.m.Domain.Name[1..]}.{x.Item2}").ToList();
                var own = aggregation.Aggregation!.Attributes.Where(x => !x.IsKey && map.Columns.ContainsKey(x)).Select(x => $"{a}.{x.Name}");
                candidates.Add($"from {a} in {aggregation.Name}\nselect {string.Join(", ", parts.Concat(own))}");
            }

            // A subtype.
            if (model.Specializations.FirstOrDefault()?.Subtypes.FirstOrDefault().Type is { } sub)
                candidates.Add(sub.Name);

            // A filter in the short form.
            var numeric = entities.SelectMany(t => t.Attributes.Select(a => (t, a)))
                .FirstOrDefault(x => x.a.Type?.Builtin is BuiltinType.Int or BuiltinType.Money or BuiltinType.Decimal or BuiltinType.SmallInt && !x.a.IsKey && !x.a.IsPartialKey);
            if (numeric.t is not null && Plain(numeric.t) is { } shown)
                candidates.Add($"{numeric.t.Name}[{numeric.a.Name} > 0].{shown}");

            return candidates.Distinct().Where(q => ErlqTranslator.Translate(q, model, map).Sql is not null).Take(6).ToList();
        }

        private static IEnumerable<TypeDef> Lineage(TypeDef type)
        {
            for (var t = type; t is not null; t = t.SubtypeOf?.Super) yield return t;
        }
    }
}
