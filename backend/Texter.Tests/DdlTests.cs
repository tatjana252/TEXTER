using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Texter.Erl.Ddl;
using Texter.Erl.Semantics;
using Texter.Logic.Erld;
using Texter.Logic.Extraction;
using Texter.Logic.Llm;
using Texter.Logic.Sandbox;
using Texter.Logic.Traceability;
using Xunit.Abstractions;

namespace Texter.Tests
{
    [Collection("LocalDB")]
    public class DdlTests(ITestOutputHelper output)
    {
        private static readonly string SamplesDir = Path.Combine(AppContext.BaseDirectory, "Samples");

        private string Ddl(string erld)
        {
            var analysis = ErldModelBuilder.Analyze(erld);
            Assert.True(analysis.Succeeded, string.Join("\n", analysis.Errors.Select(e => $"{e.Line}:{e.Column} {e.Message}")));
            var result = SqlServerDdlGenerator.Generate(analysis.Model!);
            output.WriteLine(result.Sql);
            foreach (var w in analysis.Warnings.Select(w => w.Message).Concat(result.Warnings)) output.WriteLine("WARNING " + w);
            return result.Sql;
        }

        private static async Task<string> LibraryErldAsync()
        {
            var llm = new FakeLlmClient(Options.Create(new LlmOptions { FakeResponsesDir = Path.Combine(SamplesDir, "fake") }));
            var extractor = new LlmConceptualModelExtractor(llm, new ConfidenceClassifier(Options.Create(new ConfidenceOptions())));
            var text = await File.ReadAllTextAsync(Path.Combine(SamplesDir, "library-requirements.txt"));
            return ErldGenerator.Generate((await extractor.ExtractAsync(text)).Model, "Library");
        }

        [Fact]
        public void Preduzece_runs_on_sql_server()
        {
            var sql = Ddl(File.ReadAllText(Path.Combine(SamplesDir, "preduzece.erld")));
            var db = LocalDb.Run(sql, output);
            if (db is null) return;

            Assert.Equal(new[] { "rId", "iId" }, db.PrimaryKey("Isplata"));
            Assert.Contains(("Radnik", "rmId", "RadnoMesto"), db.ForeignKeys);
            Assert.Contains(("Isplata", "rId", "Radnik"), db.ForeignKeys);
            Assert.Equal("CASCADE", db.DeleteAction("Isplata", "Radnik"));
            Assert.Equal(new[] { "ucId" }, db.PrimaryKey("Ucestvuje"));
            Assert.True(db.IsIdentity("Radnik", "rId"));
            Assert.Contains("[klasa] AS CAST(CASE WHEN", sql);
            Assert.Contains("DATEADD(day, 15, [od])", sql);
            Assert.Contains("-- rule ProveraMesecnogLimita", sql);
        }

        [Fact]
        public async Task Library_runs_on_sql_server()
        {
            var sql = Ddl(await LibraryErldAsync());
            var db = LocalDb.Run(sql, output);
            if (db is null) return;

            // Weak entity: owner key + partial key.
            Assert.Equal(new[] { "isbn", "copyNumber" }, db.PrimaryKey("Copy"));
            // Multivalued attribute: its own table.
            Assert.Equal(new[] { "membershipNumber", "phoneNumber" }, db.PrimaryKey("MemberPhoneNumber"));
            // IS-A: one table for the hierarchy, with a discriminator; no tables for the subtypes.
            Assert.Contains("tip", db.Columns("Employee"));
            Assert.DoesNotContain("Librarian", db.Tables);
            // Librarian manages a section: the subtype's foreign key lives in the hierarchy table.
            Assert.Contains(("Employee", "sectionId", "Section"), db.ForeignKeys);
            // M:N aggregation with attributes.
            Assert.Equal(new[] { "membershipNumber", "isbn", "copyNumber" }, db.PrimaryKey("Loan"));
        }

        [Fact]
        public async Task Canned_sample_data_loads_into_the_library()
        {
            var erld = await LibraryErldAsync();
            var ddl = SqlServerDdlGenerator.Generate(ErldModelBuilder.Analyze(erld).Model!);
            var llm = new FakeLlmClient(Options.Create(new LlmOptions { FakeResponsesDir = Path.Combine(SamplesDir, "fake") }));
            var data = await new SampleDataGenerator(llm).GenerateAsync("", ddl.Sql, ddl.Map);
            var sandbox = new SandboxDatabase(Options.Create(new SandboxOptions()));
            try
            {
                var result = await sandbox.LoadAsync(ddl.Sql, ddl.Map, data);
                Assert.Empty(result.Rejected);
                Assert.Equal(6, result.Tables.Single(t => t.Name == "Loan").Rows);
            }
            catch (SandboxException e)
            {
                output.WriteLine("LocalDB is not available: " + e.Message);
                return;
            }
            finally
            {
                try { await sandbox.DropAsync(ddl.Sql); } catch (SandboxException) { }
            }
        }

        [Fact]
        public void Aggregation_in_a_relationship_runs_on_sql_server()
        {
            const string erld = """
                model Clinic;
                entity Vet { key licence : string(20); }
                entity Pet { key petId : int auto; name : string(30); }
                weak entity ExamSpot { partial key number : int; }
                entity Room { key roomId : int; }
                identifying relationship Has {
                    Room -> ExamSpot [1..*] as spots on delete cascade;
                    ExamSpot -> Room [1..1] as room;
                }
                aggregation Examination: Vet.pets [0..*] <-> Pet.vets [0..*] {
                    date : date;
                    price : money >= 0 default 0;
                }
                relationship TakesPlaceAt {
                    Examination -> ExamSpot [1..1] as spot;
                    ExamSpot -> Examination [0..*] as examinations;
                }
                """;
            var sql = Ddl(erld);
            var db = LocalDb.Run(sql, output);
            if (db is null) return;

            Assert.Equal(new[] { "licence", "petId" }, db.PrimaryKey("Examination"));
            Assert.Contains(("Examination", "roomId", "ExamSpot"), db.ForeignKeys);
            Assert.Contains(("Examination", "number", "ExamSpot"), db.ForeignKeys);
            Assert.Contains("-- Room -> ExamSpot [1..*] (Has): every Room needs at least 1 ExamSpot (needs a trigger).", sql);
        }

        [Fact]
        public void Second_cascade_path_falls_back_to_no_action()
        {
            const string erld = """
                model M;
                entity A { key aId : int; }
                entity B { key bId : int; }
                entity C { key cId : int; }
                relationship AB { A -> B [0..*] on delete cascade; B -> A [1..1]; }
                relationship AC { A -> C [0..*] on delete cascade; C -> A [1..1]; }
                relationship CB { C -> B [0..*] on delete cascade; B -> C [1..1]; }
                """;
            var analysis = ErldModelBuilder.Analyze(erld);
            var result = SqlServerDdlGenerator.Generate(analysis.Model!);
            output.WriteLine(result.Sql);

            Assert.Single(result.Warnings, w => w.Contains("multiple cascade paths"));
            var db = LocalDb.Run(result.Sql, output);
            if (db is null) return;
            Assert.Equal(2, db.ForeignKeys.Count(f => db.DeleteAction(f.Table, f.Parent) == "CASCADE"));
        }

        [Fact]
        public void Overlapping_and_nested_specializations_run_on_sql_server()
        {
            const string erld = """
                model Osobe;
                entity Osoba { key jmbg : char(13); ime : string(30); }
                entity Student { indeks : string(10); }
                entity Zaposleni { plata : money > 0; }
                entity Nastavnik { zvanje : string(20) optional; }
                specialization of Osoba (overlapping, partial) { Student, Zaposleni }
                specialization of Zaposleni (disjoint, total) discriminator vrsta { Nastavnik = 'N' }
                """;
            var sql = Ddl(erld);
            var db = LocalDb.Run(sql, output);
            if (db is null) return;

            Assert.Equal(new[] { "jmbg", "ime", "jeStudent", "jeZaposleni", "indeks", "plata", "vrsta", "zvanje" }, db.Columns("Osoba"));
            Assert.Single(db.Tables);
        }

        [Fact]
        public void Script_with_drop_runs_again_in_the_same_database()
        {
            var model = ErldModelBuilder.Analyze(File.ReadAllText(Path.Combine(SamplesDir, "preduzece.erld"))).Model!;
            var sql = SqlServerDdlGenerator.Generate(model, dropExisting: true).Sql;
            Assert.Contains("DROP TABLE IF EXISTS [RadnoMesto], [Sluzba], [Radnik]", sql);

            // In between, someone else's table points at one of ours: its foreign key must not block the drop.
            var db = LocalDb.Run(sql, output,
                "CREATE TABLE [Tudja] ([id] int PRIMARY KEY, [rId] int REFERENCES [Radnik] ([rId]));",
                sql);
            if (db is null) return;

            Assert.Contains("Radnik", db.Tables);
            Assert.Contains("Tudja", db.Tables);
            Assert.DoesNotContain(db.ForeignKeys, f => f.Table == "Tudja");
        }

        [Fact]
        public async Task Deploy_creates_the_database_and_reports_existing_tables()
        {
            var model = ErldModelBuilder.Analyze(File.ReadAllText(Path.Combine(SamplesDir, "preduzece.erld"))).Model!;
            var deployer = new DatabaseDeployer(Options.Create(new DeployOptions()));
            var name = "Texter Deploy " + Guid.NewGuid().ToString("N")[..8];
            try
            {
                var first = await deployer.DeployAsync(name, SqlServerDdlGenerator.Generate(model).Sql);
                Assert.True(first.Created);
                Assert.Equal(7, first.Tables.Count);

                // Again without drop: the tables exist, and the message says what to do.
                var again = await Assert.ThrowsAsync<SandboxException>(() => deployer.DeployAsync(name, SqlServerDdlGenerator.Generate(model).Sql));
                Assert.Contains("drop existing tables", again.Message);

                var replaced = await deployer.DeployAsync(name, SqlServerDdlGenerator.Generate(model, dropExisting: true).Sql);
                Assert.False(replaced.Created);
                Assert.Equal(7, replaced.Tables.Count);
            }
            catch (SandboxException e) when (e.Message.Contains("network-related") || e.Message.Contains("LocalDB"))
            {
                output.WriteLine("LocalDB is not available: " + e.Message);
            }
            finally
            {
                LocalDb.Drop(name);
            }
        }

        [Theory]
        [InlineData("Biblioteka", true)]
        [InlineData("Radna baza_2", true)]
        [InlineData("Školska-baza", true)]
        [InlineData("1baza", false)]
        [InlineData("a]; DROP DATABASE x; --", false)]
        [InlineData("", false)]
        public void Database_names_are_checked(string name, bool valid) =>
            Assert.Equal(valid, DatabaseDeployer.IsValidName(name));

        [Fact]
        public void Semantic_errors_have_positions()
        {
            const string erld = """
                model M;
                entity A { key id : int; x : Nepoznat; }
                weak entity W { partial key n : int; }
                relationship R: A.bs [0..*] <-> B.a [1..1];
                """;
            var analysis = ErldModelBuilder.Analyze(erld);

            Assert.False(analysis.Succeeded);
            Assert.Contains(analysis.Errors, e => e.Line == 2 && e.Message.Contains("Unknown type 'Nepoznat'"));
            Assert.Contains(analysis.Errors, e => e.Line == 3 && e.Message.Contains("no identifying relationship"));
            Assert.Contains(analysis.Errors, e => e.Line == 4 && e.Message.Contains("Unknown entity or aggregation 'B'"));
        }

        /// <summary>Runs DDL in a fresh LocalDB database and reads the catalog back; null when LocalDB is missing.</summary>
        private sealed class LocalDb
        {
            public List<string> Tables { get; } = new();
            public List<(string Table, string Column, string Parent)> ForeignKeys { get; } = new();
            private readonly Dictionary<string, List<string>> _columns = new();
            private readonly Dictionary<string, List<string>> _keys = new();
            private readonly Dictionary<(string, string), string> _deleteActions = new();
            private readonly HashSet<(string, string)> _identity = new();

            public List<string> Columns(string table) => _columns[table];
            public List<string> PrimaryKey(string table) => _keys[table];
            public string DeleteAction(string table, string parent) => _deleteActions[(table, parent)];
            public bool IsIdentity(string table, string column) => _identity.Contains((table, column));

            /// <param name="more">Further scripts run after the first, in the same database.</param>
            public static LocalDb? Run(string sql, ITestOutputHelper output, params string[] more)
            {
                const string server = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=60";
                var database = "texter_test_" + Guid.NewGuid().ToString("N")[..12];
                SqlConnection master;
                try
                {
                    master = new SqlConnection(server);
                    master.Open();
                }
                catch (SqlException e)
                {
                    output.WriteLine("LocalDB is not available, the DDL was not executed: " + e.Message);
                    return null;
                }

                using (master)
                {
                    Exec(master, $"CREATE DATABASE [{database}]");
                    try
                    {
                        using var db = new SqlConnection(server + $";Database={database}");
                        db.Open();
                        // As in sqlcmd or SSMS with other settings: the script must set what it needs itself.
                        Exec(db, "SET QUOTED_IDENTIFIER OFF; SET ANSI_NULLS OFF; SET ARITHABORT OFF;");
                        Exec(db, sql);
                        foreach (var next in more) Exec(db, next);
                        return Read(db);
                    }
                    finally
                    {
                        SqlConnection.ClearAllPools();
                        Exec(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
                    }
                }
            }

            public static void Drop(string database)
            {
                try
                {
                    using var master = new SqlConnection(@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=60");
                    master.Open();
                    SqlConnection.ClearAllPools();
                    Exec(master, $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}] END");
                }
                catch (SqlException)
                {
                    // LocalDB not available: nothing was created
                }
            }

            private static void Exec(SqlConnection connection, string sql)
            {
                using var command = new SqlCommand(sql, connection);
                command.ExecuteNonQuery();
            }

            private static LocalDb Read(SqlConnection db)
            {
                var result = new LocalDb();
                Query(db, "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS ORDER BY TABLE_NAME, ORDINAL_POSITION", r =>
                {
                    var table = r.GetString(0);
                    if (!result._columns.ContainsKey(table)) { result._columns[table] = new(); result.Tables.Add(table); }
                    result._columns[table].Add(r.GetString(1));
                });
                Query(db, """
                    SELECT t.name, c.name FROM sys.indexes i
                    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    JOIN sys.tables t ON t.object_id = i.object_id
                    WHERE i.is_primary_key = 1 ORDER BY t.name, ic.key_ordinal
                    """, r =>
                {
                    var table = r.GetString(0);
                    if (!result._keys.ContainsKey(table)) result._keys[table] = new();
                    result._keys[table].Add(r.GetString(1));
                });
                Query(db, """
                    SELECT OBJECT_NAME(f.parent_object_id), COL_NAME(fc.parent_object_id, fc.parent_column_id), OBJECT_NAME(f.referenced_object_id), f.delete_referential_action_desc
                    FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = f.object_id
                    """, r =>
                {
                    result.ForeignKeys.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
                    result._deleteActions[(r.GetString(0), r.GetString(2))] = r.GetString(3);
                });
                Query(db, "SELECT OBJECT_NAME(object_id), name FROM sys.identity_columns", r => result._identity.Add((r.GetString(0), r.GetString(1))));
                return result;
            }

            private static void Query(SqlConnection db, string sql, Action<SqlDataReader> row)
            {
                using var command = new SqlCommand(sql, db);
                using var reader = command.ExecuteReader();
                while (reader.Read()) row(reader);
            }
        }
    }
}
