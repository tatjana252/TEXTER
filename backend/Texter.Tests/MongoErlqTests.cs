using System.Text.Json;
using Microsoft.Extensions.Options;
using Texter.Erl.Mongo;
using Texter.Erl.Semantics;
using Texter.Logic.Mongo;
using Texter.Logic.Sandbox;
using Xunit.Abstractions;

namespace Texter.Tests
{
    /// <summary>
    /// ERLQ on preduzece.erld translated to aggregation pipelines; with a MongoDB server, executed on the same data
    /// as the SQL Server tests, and the answers must be the same.
    /// </summary>
    [Collection("MongoDB")]
    public class MongoErlqTests(ITestOutputHelper output) : IAsyncLifetime
    {
        private static readonly string SamplesDir = Path.Combine(AppContext.BaseDirectory, "Samples");
        private readonly MongoStore _store = new(Options.Create(new MongoOptions()));
        private readonly string _database = "texter_test_" + Guid.NewGuid().ToString("N")[..8];
        private ErldModel _model = default!;
        private MongoSchemaGenerator.MongoSchemaResult _schema = default!;
        private bool _available = true;

        private const string Data = """
            {
              "RadnoMesto": [ { "_id": 1, "naziv": "ŠEF" }, { "_id": 2, "naziv": "REFERENT" } ],
              "Sluzba": [ { "_id": 1, "naziv": "IT" }, { "_id": 2, "naziv": "RAČUNOVODSTVO" } ],
              "Radnik": [
                { "_id": 100, "ime": "Ana", "radnoMesto": 1, "sluzba": 1, "isplate": [
                    { "iId": 1, "datum": "2026-01-31", "iznos": 300, "vrsta": "PLATA" },
                    { "iId": 2, "datum": "2026-02-28", "iznos": 150, "vrsta": "BONUS" } ] },
                { "_id": 102, "ime": "Marko", "radnoMesto": 2, "sluzba": 1, "isplate": [
                    { "iId": 1, "datum": "2026-01-31", "iznos": 80, "vrsta": "PLATA" } ] },
                { "_id": 104, "ime": "Jelena", "radnoMesto": 2, "sluzba": 2, "isplate": [] }
              ],
              "Projekat": [ { "_id": 1, "naziv": "PR0001", "budzet": 900 }, { "_id": 2, "naziv": "PR0002", "budzet": 7000 } ],
              "Ucestvuje": [
                { "_id": 100, "radnik": 100, "projekat": 1, "od": "2026-01-01", "do": null },
                { "_id": 101, "radnik": 104, "projekat": 2, "od": "2026-01-01", "do": "2026-03-01" }
              ],
              "Rukovodi": [ { "_id": 100, "radnik": 100, "projekat": 1, "datumImenovanja": "2026-01-05" } ]
            }
            """;

        public async Task InitializeAsync()
        {
            _model = ErldModelBuilder.Analyze(File.ReadAllText(Path.Combine(SamplesDir, "preduzece.erld"))).Model!;
            _schema = MongoSchemaGenerator.Generate(_model);
            try
            {
                await _store.DeployAsync(_database, _schema, dropExisting: true);
                var data = JsonSerializer.Deserialize<Dictionary<string, List<JsonElement>>>(Data)!;
                var load = await _store.LoadAsync(_schema, data, _database);
                Assert.Empty(load.Rejected);
            }
            catch (SandboxException e) when (e.Message.Contains("not available"))
            {
                output.WriteLine("MongoDB is not available, queries are only translated: " + e.Message);
                _available = false;
            }
        }

        public async Task DisposeAsync()
        {
            if (_available) await _store.DropDatabaseAsync(_database);
        }

        private async Task<List<List<string?>>?> Run(string query)
        {
            var translation = ErlqMongoTranslator.Translate(query, _model, _schema.Map);
            Assert.True(translation.Query is not null, string.Join("\n", translation.Errors.Select(e => $"{e.Line}:{e.Column} {e.Message}")));
            output.WriteLine(query);
            output.WriteLine(translation.Query!.Script);
            output.WriteLine("");
            if (!_available) return null;
            return (await _store.QueryAsync(_schema, translation.Query, _database)).Rows;
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
        public async Task Card_of_embedded_weak_entities()
        {
            var rows = await Run("from r in Radnik select r.ime, card(r.isplate) as broj order by r.ime");
            if (rows is null) return;
            Assert.Equal(new[] { "Ana:2", "Jelena:0", "Marko:1" }, rows.Select(r => $"{r[0]}:{r[1]}"));
        }

        [Fact]
        public async Task Range_over_embedded_array_with_limit()
        {
            var rows = await Run("from r in Radnik, i in r.isplate where i.iznos > 100 select r.ime, i.iznos order by i.iznos desc limit 1");
            if (rows is null) return;
            Assert.Equal(new[] { "Ana:300" }, rows.Select(r => $"{r[0]}:{r[1]}"));
        }

        [Fact]
        public async Task Exists_and_forall()
        {
            var exists = await Run("from p in Projekat where exists u in p.ucesnici : u.sluzba.naziv = 'IT' select p.naziv");
            var forall = await Run("from r in Radnik where forall i in r.isplate : i.iznos >= 100 select r.ime");
            if (exists is null || forall is null) return;
            Assert.Equal(new[] { "PR0001" }, Col(exists));
            Assert.Equal(new[] { "Ana", "Jelena" }, Col(forall).Order());
        }

        [Fact]
        public async Task Aggregation_both_ways()
        {
            var rows = await Run("from u in Ucestvuje select u.radnik.ime, u.projekat.naziv, u.od order by u.radnik.ime");
            var counts = await Run("from r in Radnik select r.ime, card(r.ucestvuje) as n order by r.ime");
            if (rows is null || counts is null) return;
            Assert.Equal(new[] { "Ana|PR0001|2026-01-01", "Jelena|PR0002|2026-01-01" }, rows.Select(r => $"{r[0]}|{r[1]}|{r[2]}"));
            Assert.Equal(new[] { "Ana:1", "Jelena:1", "Marko:0" }, counts.Select(r => $"{r[0]}:{r[1]}"));
        }

        [Fact]
        public async Task Group_by_with_count()
        {
            var rows = await Run("from r in Radnik group by r.sluzba.naziv select r.sluzba.naziv as sluzba, count(r) as broj order by sluzba");
            if (rows is null) return;
            Assert.Equal(new[] { "IT:2", "RAČUNOVODSTVO:1" }, rows.Select(r => $"{r[0]}:{r[1]}"));
        }

        [Fact]
        public async Task Dates_compare_with_string_literals_and_durations()
        {
            var rows = await Run("from u in Ucestvuje where u.do is null or u.do >= u.od + 15 days select u.radnik.ime order by u.radnik.ime");
            var since = await Run("from r in Radnik, i in r.isplate where i.datum >= '2026-02-01' select r.ime, i.iznos");
            if (rows is null || since is null) return;
            Assert.Equal(new[] { "Ana", "Jelena" }, Col(rows));
            Assert.Equal(new[] { "Ana:150" }, since.Select(r => $"{r[0]}:{r[1]}"));
        }

        [Fact]
        public void Errors_have_positions()
        {
            var derived = ErlqMongoTranslator.Translate("from p in Projekat select p.klasa", _model, _schema.Map);
            Assert.Contains(derived.Errors, e => e.Message.Contains("derived attribute"));
            var unknown = ErlqMongoTranslator.Translate("from r in Radnik\nselect r.plata", _model, _schema.Map);
            Assert.Contains(unknown.Errors, e => e.Line == 2 && e.Message.Contains("no attribute or role 'plata'"));
        }
    }
}
