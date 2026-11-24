using MongoDB.Bson;
using Texter.Erl.Semantics;

namespace Texter.Erl.Mongo
{
    /// <summary>
    /// Where the MongoDB schema put each part of the ERLD model. ERLQ uses it to translate navigation into
    /// $lookup / embedded paths; sample data uses the collections and their document schemas.
    /// </summary>
    public class MongoMap
    {
        public Dictionary<TypeDef, MongoTypeStorage> Types { get; } = new();

        /// <summary>
        /// Path of a simple attribute inside its document (also of each part of a composite one): "naziv",
        /// "adresa.ulica"; a single-attribute key is "_id", a part of a composite key "_id.part".
        /// </summary>
        public Dictionary<AttributeDef, string> Fields { get; } = new();

        /// <summary>Multivalued attributes: arrays of values, at this path.</summary>
        public Dictionary<AttributeDef, string> Arrays { get; } = new();

        public Dictionary<RelationshipDef, MongoRelationshipStorage> Relationships { get; } = new();

        public List<MongoCollectionInfo> Collections { get; } = new();
    }

    public enum MongoTypeKind
    {
        /// <summary>Documents of their own collection (a subtype: documents of its root's collection).</summary>
        Document,

        /// <summary>A weak entity: elements of an array inside its owner's documents.</summary>
        Embedded
    }

    /// <param name="Collection">Collection of the type, or of the owner for an embedded type.</param>
    /// <param name="ArrayField">Embedded: the owner's array field holding the elements.</param>
    /// <param name="Owner">Embedded: the owner type.</param>
    /// <param name="PartialKeys">Embedded: fields of the partial key inside an element.</param>
    /// <param name="Conditions">Subtype: field = value that select its documents (discriminator, flags).</param>
    public record MongoTypeStorage(
        string Collection,
        MongoTypeKind Kind,
        string? ArrayField,
        TypeDef? Owner,
        IReadOnlyList<string> PartialKeys,
        IReadOnlyList<(string Field, BsonValue Value)> Conditions);

    public enum MongoRelationshipKind
    {
        /// <summary>A reference field in the holder's documents (or elements), holding the co-domain's _id.</summary>
        Reference,

        /// <summary>M:N: an array of references in the documents of mapping 1's domain.</summary>
        ReferenceArray,

        /// <summary>A weak entity embedded in its owner (identifying relationship).</summary>
        Embedded,

        /// <summary>The aggregation's own collection, with a reference field per participant.</summary>
        AggregationCollection
    }

    public class MongoRelationshipStorage
    {
        public MongoRelationshipKind Kind { get; set; }

        /// <summary>Reference / ReferenceArray: the mapping whose domain's documents hold the field.</summary>
        public MappingDef? Holder { get; set; }

        /// <summary>Reference / ReferenceArray: the field (path) holding the reference(s).</summary>
        public string? Field { get; set; }

        /// <summary>AggregationCollection: for each mapping, the field referencing that mapping's domain.</summary>
        public Dictionary<MappingDef, string> EndFields { get; } = new();
    }

    /// <param name="Schema">$jsonSchema of the documents (also used to ask the LLM for sample data).</param>
    /// <param name="DateFields">Paths of date / datetime fields ("datum", "isplate.datum"): sample data strings become dates.</param>
    public record MongoCollectionInfo(string Name, BsonDocument Schema, List<BsonDocument> Indexes, IReadOnlyList<string> DateFields);
}
