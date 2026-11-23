using Texter.Erl.Semantics;

namespace Texter.Erl.Ddl
{
    /// <summary>
    /// Where the DDL generator put each part of the ERLD model: tables of types, columns of attributes, how each
    /// relationship is stored. ERLQ uses it to translate navigation into joins; sample data uses the tables.
    /// </summary>
    public class RelationalMap
    {
        public Dictionary<TypeDef, TypeStorage> Types { get; } = new();

        /// <summary>Column of a simple attribute (also of each part of a composite one, and of derived ones).</summary>
        public Dictionary<AttributeDef, string> Columns { get; } = new();

        /// <summary>Table of a multivalued attribute.</summary>
        public Dictionary<AttributeDef, MultivaluedStorage> Multivalued { get; } = new();

        public Dictionary<RelationshipDef, RelationshipStorage> Relationships { get; } = new();

        /// <summary>All tables in creation order (parents of foreign keys are not necessarily first).</summary>
        public List<TableInfo> Tables { get; } = new();
    }

    /// <param name="Table">Table of the type (a subtype lives in its root's table).</param>
    /// <param name="Key">Primary key columns.</param>
    /// <param name="Conditions">For a subtype: column = value conditions that select its rows (discriminator, bit flags).</param>
    public record TypeStorage(string Table, IReadOnlyList<string> Key, IReadOnlyList<(string Column, string Value)> Conditions);

    /// <param name="OwnerColumns">Columns referencing the owner's key (same names as the owner's key).</param>
    public record MultivaluedStorage(string Table, IReadOnlyList<string> OwnerColumns, string ValueColumn);

    public enum RelationshipStorageKind
    {
        /// <summary>Foreign key in the table of <see cref="RelationshipStorage.Holder"/>'s domain, to its co-domain.</summary>
        ForeignKey,

        /// <summary>A table with a foreign key per end: junction table (M:N) or the aggregation's own table.</summary>
        LinkTable
    }

    public class RelationshipStorage
    {
        public RelationshipStorageKind Kind { get; set; }

        /// <summary>ForeignKey: the mapping whose domain's table holds the foreign key.</summary>
        public MappingDef? Holder { get; set; }

        /// <summary>ForeignKey: columns in the holder's table, referencing the co-domain's key (same order).</summary>
        public List<string> Columns { get; } = new();

        /// <summary>LinkTable: the table.</summary>
        public string? Table { get; set; }

        /// <summary>LinkTable: for each mapping, the columns referencing the key of that mapping's domain.</summary>
        public Dictionary<MappingDef, List<string>> EndColumns { get; } = new();
    }

    public record TableInfo(string Name, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<string> PrimaryKey, IReadOnlyList<ForeignKeyInfo> ForeignKeys);

    public record ColumnInfo(string Name, string Type, bool Nullable, bool Identity, bool Computed, bool HasDefault);

    public record ForeignKeyInfo(IReadOnlyList<string> Columns, string ParentTable, IReadOnlyList<string> ParentColumns);
}
