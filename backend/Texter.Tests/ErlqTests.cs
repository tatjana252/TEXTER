using System.Text.Json;
using Microsoft.Extensions.Options;
using Texter.Erl.Ddl;
using Texter.Erl.Query;
using Texter.Erl.Semantics;
using Texter.Logic.Sandbox;
using Xunit.Abstractions;

namespace Texter.Tests
{
    /// <summary>ERLQ on preduzece.erld, executed on LocalDB with known data.</summary>
    [Collection("LocalDB")]
    public class ErlqTests(ITestOutputHelper output) : IAsyncLifetime
    {
        private static readonly string SamplesDir = Path.Combine(AppContext.BaseDirectory, "Samples");
        private readonly SandboxDatabase _sandbox = new(Options.Create(new SandboxOptions()));
        private ErldModel _model = default!;
        private SqlServerDdlGenerator.DdlResult _ddl = default!;
        private bool _available = true;

        private const string Data = """
            {
              "RadnoMesto": [ { "rmId": 1, "naziv": "ŠEF" }, { "rmId": 2, "naziv": "REFERENT" } ],
              "Sluzba": [ { "sId": 1, "naziv": "IT" }, { "sId": 2, "naziv": "RAČUNOVODSTVO" } ],
              "Radnik": [
                { "rId": 100, "ime": "Ana", "rmId": 1, "sId": 1 },
                { "rId": 102, "ime": "Marko", "rmId": 2, "sId": 1 },
                { "rId": 104, "ime": "Jelena", "rmId": 2, "sId": 2 }
              ],
              "Projekat": [
                { "pId": 1, "naziv": "PR0001", "budzet": 900 },
                { "pId": 2, "naziv": "PR0002", "budzet": 7000 }
              ],
              "Isplata": [
                { "rId": 100, "iId": 1, "datum": "2026-01-31", "iznos": 300, "vrsta": "PLATA" },
                { "rId": 100, "iId": 2, "datum": "2026-02-28", "iznos": 150, "vrsta": "BONUS" },
                { "rId": 102, "iId": 1, "datum": "2026-01-31", "iznos": 80, "vrsta": "PLATA" }
              ],
              "Ucestvuje": [
                { "ucId": 100, "rId": 100, "pId": 1, "od": "2026-01-01", "do": null },
                { "ucId": 101, "rId": 104, "pId": 2, "od": "2026-01-01", "do": "2026-03-01" }
              ],
              "Rukovodi": [ { "rkId": 100, "rId": 100, "pId": 1, "datumImenovanja": "2026-01-05" } ]
            }
            """;

        public async Task InitializeAsync()
        {
            _model = ErldModelBuilder.Analyze(File.ReadAllText(Path.Combine(SamplesDir, "preduzece.erld"))).Model!;
            _ddl = SqlServerDdlGenerator.Generate(_model);
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, JsonElement>>>>(Data)!;
                var load = await _sandbox.LoadAsync(_ddl.Sql, _ddl.Map, data);
                Assert.Empty(load.Rejected);
            }
            catch (SandboxException e)
            {
                output.WriteLine("LocalDB is not available, queries are only translated: " + e.Message);
                _available = false;
            }
        }

        public async Task DisposeAsync()
        {
            if (_available) await _sandbox.DropAsync(_ddl.Sql);
        }

        private async Task<List<List<string?>>?> Run(string query)
        {
            var translation = ErlqTranslator.Translate(query, _model, _ddl.Map);
            Assert.True(translation.Sql is not null, string.Join("\n", translation.Errors.Select(e => $"{e.Line}:{e.Column} {e.Message}")));
            output.WriteLine(query);
            output.WriteLine(translation.Sql);
            output.WriteLine("");
            if (!_available) return null;
            return (await _sandbox.QueryAsync(_ddl.Sql, _ddl.Map, translation.Sql!)).Rows;
        }

        private static List<string> Col(List<List<string?>> rows, int i = 0) => rows.Select(r => r[i] ?? "NULL").ToList();

        [Fact]
        public async Task Short_form_with_filter_and_navigation()
        {
            var rows = await Run("Radnik[sluzba.naziv = 'IT'].ime");
            if (rows is null) return;
            Assert.Equal(new[] { "Ana", "Marko" }, Col(rows).Order());
        }

        [Fact]
        public async Task Card_of_a_role_counts_related_instances()
        {
            var rows = await Run("from r in Radnik select r.ime, card(r.isplate) as broj order by r.ime");
            if (rows is null) return;
            Assert.Equal(new[] { "Ana:2", "Jelena:0", "Marko:1" }, rows.Select(r => $"{r[0]}:{r[1]}"));
        }

        [Fact]
        public async Task Range_over_a_role_joins_and_limit_takes_the_top()
        {
            var rows = await Run("""
                from r in Radnik, i in r.isplate
                where i.iznos > 100
                select r.ime, i.iznos
                order by i.iznos desc
                limit 1
                """);
            if (rows is null) return;
            Assert.Equal(new[] { "Ana:300" }, rows.Select(r => $"{r[0]}:{r[1]}"));
        }

        [Fact]
        public async Task Exists_and_forall_over_collections()
        {
            var exists = await Run("from p in Projekat where exists u in p.ucesnici : u.sluzba.naziv = 'IT' select p.naziv");
            var forall = await Run("from r in Radnik where forall i in r.isplate : i.iznos >= 100 select r.ime");
            if (exists is null || forall is null) return;
            Assert.Equal(new[] { "PR0001" }, Col(exists));
            Assert.Equal(new[] { "Ana", "Jelena" }, Col(forall).Order()); // Jelena has no payments: vacuously true
        }

        [Fact]
        public async Task Aggregation_reaches_its_participants_and_participants_reach_it()
        {
            var rows = await Run("from u in Ucestvuje select u.radnik.ime, u.projekat.naziv, u.od order by u.radnik.ime");
            var counts = await Run("from r in Radnik select r.ime, card(r.ucestvuje) as n order by r.ime");
            if (rows is null || counts is null) return;
            Assert.Equal(new[] { "Ana|PR0001", "Jelena|PR0002" }, rows.Select(r => $"{r[0]}|{r[1]}"));
            Assert.Equal(new[] { "Ana:1", "Jelena:1", "Marko:0" }, counts.Select(r => $"{r[0]}:{r[1]}"));
        }

        [Fact]
        public async Task Group_by_a_navigation_with_count()
        {
            var rows = await Run("from r in Radnik group by r.sluzba.naziv select r.sluzba.naziv as sluzba, count(r) as broj order by sluzba");
            if (rows is null) return;
            Assert.Equal(new[] { "IT:2", "RAČUNOVODSTVO:1" }, rows.Select(r => $"{r[0]}:{r[1]}"));
        }

        [Fact]
        public async Task Entity_in_select_expands_to_its_attributes()
        {
            var translation = ErlqTranslator.Translate("from p in Projekat where p.budzet > 1000 select p", _model, _ddl.Map);
            Assert.Equal(new[] { "p.pId", "p.naziv", "p.budzet", "p.klasa" }, translation.Columns);
            var rows = await Run("from p in Projekat where p.budzet > 1000 select p");
            if (rows is null) return;
            Assert.Equal(new[] { "2", "PR0002", "7000", "1" }, rows.Single().Select(v => v!.Trim()));
        }

        [Fact]
        public void Errors_have_positions()
        {
            var unknown = ErlqTranslator.Translate("from r in Radnik\nselect r.plata", _model, _ddl.Map);
            Assert.Null(unknown.Sql);
            Assert.Contains(unknown.Errors, e => e.Line == 2 && e.Message.Contains("no attribute or role 'plata'"));

            var collection = ErlqTranslator.Translate("from r in Radnik select r.isplate", _model, _ddl.Map);
            Assert.Contains(collection.Errors, e => e.Message.Contains("is a collection"));

            var syntax = ErlqTranslator.Translate("from r in select", _model, _ddl.Map);
            Assert.NotEmpty(syntax.Errors);
        }

        [Fact]
        public void Examples_are_generated_and_translate()
        {
            var examples = ErlqExamples.For(_model, _ddl.Map);
            foreach (var e in examples) output.WriteLine(e + "\n");
            Assert.True(examples.Count >= 3);
        }

        [Fact]
        public void Sample_data_schema_has_every_table_and_column()
        {
            using var schema = JsonDocument.Parse(Texter.Logic.Sandbox.SampleDataGenerator.Schema(_ddl.Map));
            var tables = schema.RootElement.GetProperty("properties");
            Assert.Equal(_ddl.Map.Tables.Count, tables.EnumerateObject().Count());
            var radnik = tables.GetProperty("Radnik").GetProperty("items").GetProperty("properties");
            Assert.Equal("integer", radnik.GetProperty("rId").GetProperty("type").GetString());
            // Computed columns are not generated.
            Assert.False(tables.GetProperty("Projekat").GetProperty("items").GetProperty("properties").TryGetProperty("klasa", out _));
        }
    }
}
