using System.Reflection;

namespace Texter.Logic.Extraction
{
    internal static class EmbeddedResource
    {
        /// <summary>Reads a file embedded from a Resources folder, e.g. Read("eer-extraction.schema.json").</summary>
        public static string Read(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
