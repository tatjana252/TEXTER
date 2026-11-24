using MongoDB.Bson;
using Texter.Erl.Mongo;
using Texter.Erl.Semantics;
using Xunit.Abstractions;

namespace Texter.Tests
{
    [Collection("MongoDB")]
    public class MongoTests(ITestOutputHelper output)
    {
        private static readonly string SamplesDir = Path.Combine(AppContext.BaseDirectory, "Samples");

        private MongoSchemaGenerator.MongoSchemaResult Schema(string erld)
        {
            var analysis = ErldModelBuilder.Analyze(erld);
            Assert.True(analysis.Succeeded, string.Join("\n", analysis.Errors.Select(e => $"{e.Line}:{e.Column} {e.Message}")));
            var result = MongoSchemaGenerator.Generate(analysis.Model!);
            output.WriteLine(result.Script);
            return result;
        }

        private static async Task<string> LibraryErldAsync()
        {
            var llm = new Texter.Logic.Llm.FakeLlmClient(Microsoft.Extensions.Options.Options.Create(new Texter.Logic.Llm.LlmOptions { FakeResponsesDir = Path.Combine(SamplesDir, "fake") }));
            var extractor = new Texter.Logic.Extraction.LlmConceptualModelExtractor(llm,
                new Texter.Logic.Traceability.ConfidenceClassifier(Microsoft.Extensions.Options.Options.Create(new Texter.Logic.Traceability.ConfidenceOptions())));
            var text = await File.ReadAllTextAsync(Path.Combine(SamplesDir, "library-requirements.txt"));
            return Texter.Logic.Erld.ErldGenerator.Generate((await extractor.ExtractAsync(text)).Model, "Library");
        }

        [Fact]
        public async Task Library_aggregation_without_key_has_the_pair_as_id()
        {
            var result = Schema(await LibraryErldAsync());
            Assert.Equal(new[] { "Member", "Book", "Employee", "Section", "Loan" }, result.Map.Collections.Select(c => c.Name));
            var loanId = result.Map.Collections.Single(c => c.Name == "Loan").Schema["properties"]["_id"].AsBsonDocument;
            Assert.Equal(new[] { "member", "copy" }, loanId["required"].AsBsonArray.Select(v => v.AsString));
            // A reference to an embedded Copy: its owner's _id and its partial key.
            Assert.Equal(new[] { "_owner", "copyNumber" }, loanId["properties"]["copy"]["required"].AsBsonArray.Select(v => v.AsString));
            Assert.Equal("array", result.Map.Collections.Single(c => c.Name == "Book").Schema["properties"]["copies"]["bsonType"].AsString);
            output.WriteLine(Texter.Logic.Mongo.MongoSampleDataGenerator.Schema(result.Map));
        }

        [Fact]
        public async Task Library_sample_data_passes_the_validators_and_answers_queries()
        {
            var analysis = ErldModelBuilder.Analyze(await LibraryErldAsync());
            var model = analysis.Model!;
            var schema = MongoSchemaGenerator.Generate(model);
            var llm = new Texter.Logic.Llm.FakeLlmClient(Microsoft.Extensions.Options.Options.Create(new Texter.Logic.Llm.LlmOptions { FakeResponsesDir = Path.Combine(SamplesDir, "fake") }));
            var data = await new Texter.Logic.Mongo.MongoSampleDataGenerator(llm).GenerateAsync("", schema);
            var store = new Texter.Logic.Mongo.MongoStore(Microsoft.Extensions.Options.Options.Create(new Texter.Logic.Mongo.MongoOptions()));
            var database = "texter_test_" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                await store.DeployAsync(database, schema, dropExisting: true);
                var load = await store.LoadAsync(schema, data, database);
                Assert.Empty(load.Rejected);
                Assert.Equal(6, load.Tables.Single(t => t.Name == "Loan").Rows);

                async Task<List<List<string?>>> Run(string query)
                {
                    var translation = ErlqMongoTranslator.Translate(query, model, schema.Map);
                    Assert.True(translation.Query is not null, string.Join("; ", translation.Errors.Select(e => e.Message)));
                    output.WriteLine(translation.Query!.Script);
                    return (await store.QueryAsync(schema, translation.Query, database)).Rows;
                }

                // Through the aggregation to an embedded copy, and from it to its book.
                var loans = await Run("from l in Loan where l.member.name = 'Ana Petrović' select l.copy.book.title, l.copy.copyNumber order by l.copy.book.title");
                Assert.Equal(new[] { "Clean Code|1", "Na Drini ćuprija|1", "Na Drini ćuprija|2" }, loans.Select(r => $"{r[0]}|{r[1]}"));
                // A subtype selects its documents by the discriminator.
                Assert.Equal(3, (await Run("Librarian")).Count);
                // Multivalued attribute as a collection.
                var phones = await Run("from m in Member select m.name, card(m.phoneNumber) as n order by m.name");
                Assert.Equal("Ana Petrović:2", $"{phones[0][0]}:{phones[0][1]}");
            }
            catch (Texter.Logic.Sandbox.SandboxException e) when (e.Message.Contains("not available"))
            {
                output.WriteLine("MongoDB is not available: " + e.Message);
            }
            finally
            {
                try { await store.DropDatabaseAsync(database); } catch (Texter.Logic.Sandbox.SandboxException) { }
            }
        }

        [Fact]
        public void Preduzece_embeds_weak_entities_and_references_the_rest()
        {
            var result = Schema(File.ReadAllText(Path.Combine(SamplesDir, "preduzece.erld")));
            var collections = result.Map.Collections.Select(c => c.Name).ToList();

            // Isplata is weak: embedded in Radnik, no collection of its own.
            Assert.Equal(new[] { "RadnoMesto", "Sluzba", "Radnik", "Projekat", "Ucestvuje", "Rukovodi" }, collections);
            var radnik = result.Map.Collections.Single(c => c.Name == "Radnik").Schema;
            var props = radnik["properties"].AsBsonDocument;
            Assert.Equal("array", props["isplate"]["bsonType"].AsString);
            Assert.Equal("int", props["radnoMesto"]["bsonType"].AsString); // reference to RadnoMesto._id
            Assert.Contains("isplate.datum", result.Map.Collections.Single(c => c.Name == "Radnik").DateFields);

            // Enum and value constraints.
            var iznos = props["isplate"]["items"]["properties"]["iznos"].AsBsonDocument;
            Assert.Equal(0, iznos["minimum"].ToInt32());
            Assert.True(iznos["exclusiveMinimum"].AsBoolean);
            Assert.Contains("db.createCollection(\"Radnik\"", result.Script);
        }
    }
}
