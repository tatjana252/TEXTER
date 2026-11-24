namespace Texter.Model.GoJs
{
    /// <summary>GoJS link template names (must match frontend/src/diagram/templates.js).</summary>
    public static class LinkCategory
    {
        public const string Attribute = "attribute";
        public const string Mapping = "mapping";
        public const string Generalization = "generalization";

        /// <summary>Identifying relationship: an arrow from the owner to its weak entity (PMER notation, no diamond).</summary>
        public const string Identifying = "identifying";
    }
}
