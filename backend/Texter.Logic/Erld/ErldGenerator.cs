using System.Text;
using System.Text.RegularExpressions;
using Texter.Model.Conceptual;
using Attribute = Texter.Model.Conceptual.Attribute;

namespace Texter.Logic.Erld
{
    /// <summary>
    /// TEXTER step 2: conceptual model -> ERLD source.
    /// Uses the short notation where possible and the long one where a mapping needs its own annotation
    /// (identifying relationships, assumed bounds). Attribution is kept in @source / @confidence annotations.
    /// </summary>
    public static partial class ErldGenerator
    {
        private const string Indent = "    ";

        // ERLD/ERLCommon keywords that cannot be used as plain names (the rest are soft keywords).
        private static readonly HashSet<string> HardKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "model", "domain", "enum", "entity", "as", "unique", "default", "check", "on", "insert", "update", "delete",
            "set", "relationship", "aggregation", "specialization", "of", "category", "constraint",
            "and", "or", "not", "implies", "in", "is", "null", "true", "false", "between", "like",
            "if", "then", "else", "forall", "exists", "for"
        };

        [GeneratedRegex(@"^[\p{L}_][\p{L}\p{N}_#]*$")]
        private static partial Regex IdentifierRegex();

        public static string Generate(ConceptualModel model, string modelName = "Texter", bool includeAttribution = true)
        {
            var writer = new Writer(model, includeAttribution);
            return writer.Write(modelName);
        }

        private sealed class Writer(ConceptualModel model, bool includeAttribution)
        {
            private readonly StringBuilder _sb = new();
            private readonly Dictionary<string, Entity> _entities = model.Entities.ToDictionary(e => e.Id);

            // Ends of relationships: entities and aggregations (an aggregation takes part in other relationships).
            private readonly Dictionary<string, string> _endNames = model.Entities.Select(e => (e.Id, e.Name))
                .Concat(model.Relationships.Where(r => r.IsAggregation).Select(r => (r.Id, r.Name)))
                .ToDictionary(x => x.Id, x => x.Name);
            private readonly HashSet<string> _subtypeIds = model.Generalizations.SelectMany(g => g.SubEntityIds).ToHashSet();

            public string Write(string modelName)
            {
                Line($"model {Name(modelName)};");

                Section("entities");
                foreach (var entity in model.Entities) WriteEntity(entity);

                var relationships = model.Relationships.Where(r => !r.IsAggregation && !UsesAggregation(r)).ToList();
                if (relationships.Count > 0) Section("relationships");
                foreach (var relationship in relationships) WriteRelationship(relationship);

                var aggregations = model.Relationships.Where(r => r.IsAggregation).ToList();
                if (aggregations.Count > 0) Section("aggregations");
                foreach (var aggregation in aggregations) WriteRelationship(aggregation);

                // After the aggregations they refer to.
                var ofAggregations = model.Relationships.Where(r => !r.IsAggregation && UsesAggregation(r)).ToList();
                if (ofAggregations.Count > 0) Section("relationships of aggregations");
                foreach (var relationship in ofAggregations) WriteRelationship(relationship);

                if (model.Generalizations.Count > 0) Section("specializations");
                foreach (var generalization in model.Generalizations) WriteSpecialization(generalization);

                return _sb.ToString();
            }

            private void WriteEntity(Entity entity)
            {
                Annotations(entity);
                var weak = entity.Kind == EntityKind.Weak ? "weak " : "";
                var header = $"{weak}entity {Name(entity.Name)}{DisplayName(entity.DisplayName, entity.Name)}";
                if (entity.Attributes.Count == 0)
                {
                    Line(header + " { }");
                    return;
                }

                Line(header + " {");
                foreach (var attribute in entity.Attributes)
                    WriteAttribute(attribute, entity.Kind == EntityKind.Weak, isSubtype: _subtypeIds.Contains(entity.Id));
                Line("}");
            }

            private void WriteAttribute(Attribute attribute, bool inWeakEntity, bool isSubtype = false)
            {
                // Subtypes inherit the supertype's key.
                var modifier = !attribute.IsIdentifier || isSubtype ? "" : inWeakEntity ? "partial key " : "key ";
                var optional = attribute.IsOptional ? " optional" : "";
                Line($"{Indent}{InlineAnnotations(attribute)}{modifier}{Name(attribute.Name)} : {Type(attribute)}{optional};");
            }

            private void WriteRelationship(Relationship relationship)
            {
                var (m1, m2) = (relationship.Mapping1, relationship.Mapping2);
                var keyword = relationship.IsAggregation ? "aggregation"
                    : relationship.IsWeak ? "identifying relationship"
                    : "relationship";
                var header = $"{keyword} {Name(relationship.Name)}{DisplayName(relationship.DisplayName, relationship.Name)}";
                var longForm = relationship.IsWeak || m1.IsAssumed || m2.IsAssumed;

                Annotations(relationship);
                if (longForm)
                {
                    Line(header + " {");
                    WriteMapping(m1, relationship);
                    WriteMapping(m2, relationship);
                    foreach (var attribute in relationship.Attributes) WriteAttribute(attribute, inWeakEntity: false);
                    Line("}");
                }
                else if (relationship.IsAggregation)
                {
                    Line(header + ":");
                    Line($"{Indent}{RelEnd(m1)} <-> {RelEnd(m2)}");
                    Line("{");
                    foreach (var attribute in relationship.Attributes) WriteAttribute(attribute, inWeakEntity: false);
                    Line("}");
                }
                else
                {
                    Line(header + ":");
                    Line($"{Indent}{RelEnd(m1)} <-> {RelEnd(m2)};");
                }
            }

            private void WriteMapping(Mapping mapping, Relationship relationship)
            {
                var assumed = mapping.IsAssumed && includeAttribution ? "@assumed " : "";
                // The weak entity exists only with its owner: deleting the owner deletes it.
                var cascade = relationship.IsWeak && _entities.TryGetValue(mapping.CoDomainEntityId, out var coDomain) && coDomain.Kind == EntityKind.Weak ? " on delete cascade" : "";
                var role = mapping.Role is { Length: > 0 } r ? $" as {Name(r)}" : "";
                Line($"{Indent}{assumed}{EntityName(mapping.DomainEntityId)} -> {EntityName(mapping.CoDomainEntityId)} {Bounds(mapping)}{role}{cascade};");
            }

            private string RelEnd(Mapping mapping)
            {
                var role = mapping.Role is { Length: > 0 } r ? "." + Name(r) : "";
                return $"{EntityName(mapping.DomainEntityId)}{role} {Bounds(mapping)}";
            }

            private void WriteSpecialization(Generalization generalization)
            {
                Annotations(generalization);
                var options = $"{(generalization.IsDisjoint ? "disjoint" : "overlapping")}, {(generalization.IsTotal ? "total" : "partial")}";
                var subtypes = string.Join(", ", generalization.SubEntityIds.Select(EntityName));
                Line($"specialization of {EntityName(generalization.SuperEntityId)} ({options}) {{ {subtypes} }}");
            }

            private void Annotations(ModelElement element)
            {
                var annotations = InlineAnnotations(element);
                if (annotations.Length > 0) Line(annotations.TrimEnd());
            }

            private string InlineAnnotations(ModelElement element)
            {
                if (!includeAttribution) return "";
                var source = element.SentenceIds.Count > 0 ? $"@source({string.Join(", ", element.SentenceIds)}) " : "";
                return $"{source}@confidence({element.Confidence.ToString().ToLowerInvariant()}) ";
            }

            private string EntityName(string id) => Name(_endNames[id]);

            private bool UsesAggregation(Relationship r) =>
                !_entities.ContainsKey(r.Mapping1.DomainEntityId) || !_entities.ContainsKey(r.Mapping2.DomainEntityId);

            private void Section(string title)
            {
                Line();
                Line($"// ---------- {title} ----------");
                Line();
            }

            private void Line(string text = "") => _sb.Append(text).Append('\n');
        }

        private static string Bounds(Mapping mapping) =>
            $"[{mapping.LowerBound}..{(mapping.UpperBound == Mapping.Many ? "*" : mapping.UpperBound.ToString())}]";

        private static string Type(Attribute attribute)
        {
            var type = attribute.Type.ToLowerInvariant() switch
            {
                "string" => $"string({attribute.Length ?? 50})",
                "char" => $"char({attribute.Length ?? 1})",
                "decimal" => "decimal(18,2)",
                var t => t
            };
            return attribute.IsMultivalued ? type + "[]" : type;
        }

        private static string DisplayName(string? displayName, string name) =>
            string.IsNullOrWhiteSpace(displayName) || displayName == name ? "" : $" as \"{displayName.Replace("\"", "")}\"";

        /// <summary>Plain identifier when possible, otherwise a quoted name ("in", "radno mesto").</summary>
        public static string Name(string name) =>
            IdentifierRegex().IsMatch(name) && !HardKeywords.Contains(name) ? name : $"\"{name.Replace("\"", "")}\"";
    }
}
