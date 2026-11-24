using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Texter.Erl.Ddl;

namespace Texter.Logic.Sandbox
{
    public class SandboxOptions
    {
        public const string SectionName = "Sandbox";

        /// <summary>SQL Server where TEXTER creates one database per DDL (LocalDB by default).</summary>
        public string ConnectionString { get; set; } = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=60";

        public int QueryTimeoutSeconds { get; set; } = 15;
        public int MaxRows { get; set; } = 500;
    }

    public class SandboxException(string message, Exception? inner = null) : Exception(message, inner);

    public record TableCount(string Name, int Rows);

    /// <param name="Server">SQL Server instance (data source), e.g. (localdb)\MSSQLLocalDB.</param>
    public record SandboxInfo(string Server, string Database, List<TableCount> Tables);

    public record LoadResult(List<TableCount> Tables, List<string> Rejected);

    public record QueryResult(List<string> Columns, List<List<string?>> Rows, bool Truncated);

    /// <summary>
    /// Step 4's physical store. Either the database the user created in step 3 (by name, on the Deploy server),
    /// or a temporary one built from the DDL (its name derived from the DDL, so the same DDL reuses it).
    /// Queries run in a transaction that is always rolled back.
    /// </summary>
    public class SandboxDatabase(IOptions<SandboxOptions> options, IOptions<DeployOptions>? deploy = null)
    {
        private readonly SandboxOptions _options = options.Value;
        private readonly DeployOptions _deploy = deploy?.Value ?? new DeployOptions();

        /// <summary>The instance the temporary databases live on, as SSMS would show it.</summary>
        public string Server => new SqlConnectionStringBuilder(_options.ConnectionString).DataSource;

        /// <summary>Named database (step 3): the Deploy server; none: the temporary database's server.</summary>
        private string Connection(string? database) => database is null ? _options.ConnectionString : _deploy.ConnectionString;

        public static string DatabaseName(string ddl) =>
            "texter_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ddl)))[..16].ToLowerInvariant();

        /// <summary>
        /// The database to work in. Named: the database created in step 3, which must exist and have the tables of
        /// the current DDL. Not named: the temporary database, created from the DDL unless it already exists.
        /// </summary>
        public async Task<SandboxInfo> EnsureAsync(string ddl, RelationalMap map, string? database = null, CancellationToken ct = default)
        {
            if (database is not null) return await CheckNamedAsync(database.Trim(), map, ct);

            var name = DatabaseName(ddl);
            await using (var master = await OpenAsync(_options.ConnectionString, null, ct))
            {
                var exists = await ScalarAsync(master, "SELECT COUNT(*) FROM sys.databases WHERE name = @n", ct, ("@n", name)) is int n && n > 0;
                if (!exists)
                {
                    await ExecAsync(master, $"CREATE DATABASE [{name}]", ct);
                    try
                    {
                        await using var db = await OpenAsync(_options.ConnectionString, name, ct);
                        await ExecAsync(db, ddl, ct);
                    }
                    catch (SqlException e)
                    {
                        await DropAsync(master, name, ct);
                        throw new SandboxException($"The DDL could not be executed: {e.Message}", e);
                    }
                }
            }
            return new SandboxInfo(Server, name, await CountsAsync(_options.ConnectionString, name, map, ct));
        }

        private async Task<SandboxInfo> CheckNamedAsync(string database, RelationalMap map, CancellationToken ct)
        {
            var server = new SqlConnectionStringBuilder(_deploy.ConnectionString).DataSource;
            if (!DatabaseDeployer.IsValidName(database)) throw new SandboxException($"'{database}' is not a valid database name.");

            await using (var master = await OpenAsync(_deploy.ConnectionString, null, ct))
                if (await ScalarAsync(master, "SELECT DB_ID(@n)", ct, ("@n", database)) is DBNull or null)
                    throw new SandboxException($"Database '{database}' does not exist on {server}. Create it in step 3 (Create database).");

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var db = await OpenAsync(_deploy.ConnectionString, database, ct))
            {
                await using var list = new SqlCommand("SELECT name FROM sys.tables", db);
                await using var reader = await list.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) existing.Add(reader.GetString(0));
            }
            var missing = map.Tables.Where(t => !existing.Contains(t.Name)).Select(t => t.Name).ToList();
            if (missing.Count > 0)
                throw new SandboxException($"Database '{database}' does not have the tables of the current model (missing: {string.Join(", ", missing)}). " +
                    "Create it again in step 3, with \"drop existing tables\" if it already has older ones.");

            return new SandboxInfo(server, database, await CountsAsync(_deploy.ConnectionString, database, map, ct));
        }

        /// <summary>
        /// Replaces all rows with the given data. Rows are inserted parents first; a row that violates a constraint
        /// is retried after the others and finally reported, not silently dropped.
        /// </summary>
        public async Task<LoadResult> LoadAsync(string ddl, RelationalMap map, Dictionary<string, List<Dictionary<string, JsonElement>>> data, string? database = null, CancellationToken ct = default)
        {
            var info = await EnsureAsync(ddl, map, database, ct);
            await using var db = await OpenAsync(Connection(database), info.Database, ct);
            var order = ParentsFirst(map.Tables);

            // Empty the tables, children first; foreign keys without cascade may need a few rounds.
            var pending = order.AsEnumerable().Reverse().ToList();
            for (var round = 0; round < order.Count + 1 && pending.Count > 0; round++)
            {
                var next = new List<TableInfo>();
                foreach (var t in pending)
                {
                    try { await ExecAsync(db, $"DELETE FROM {Q(t.Name)}", ct); }
                    catch (SqlException) { next.Add(t); }
                }
                pending = next;
            }

            var rows = order
                .SelectMany(t => (data.TryGetValue(t.Name, out var r) ? r : new()).Select(row => (Table: t, Row: row)))
                .ToList();
            var errors = new Dictionary<(string, int), string>();
            for (var round = 0; round < 4 && rows.Count > 0; round++)
            {
                var failed = new List<(TableInfo Table, Dictionary<string, JsonElement> Row)>();
                foreach (var (table, row) in rows)
                {
                    try { await InsertAsync(db, table, row, ct); }
                    catch (SqlException e)
                    {
                        failed.Add((table, row));
                        errors[(table.Name, row.GetHashCode())] = e.Message;
                    }
                }
                if (failed.Count == rows.Count) { rows = failed; break; }
                rows = failed;
            }
            var rejected = rows.Select(x => $"{x.Table.Name} {Describe(x.Row)}: {errors[(x.Table.Name, x.Row.GetHashCode())]}").ToList();
            return new LoadResult(await CountsAsync(Connection(database), info.Database, map, ct), rejected);
        }

        public async Task<QueryResult> QueryAsync(string ddl, RelationalMap map, string sql, string? database = null, CancellationToken ct = default)
        {
            var info = await EnsureAsync(ddl, map, database, ct);
            await using var db = await OpenAsync(Connection(database), info.Database, ct);
            await using var transaction = (SqlTransaction)await db.BeginTransactionAsync(ct);
            try
            {
                await using var command = new SqlCommand(sql, db, transaction) { CommandTimeout = _options.QueryTimeoutSeconds };
                await using var reader = await command.ExecuteReaderAsync(ct);
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var result = new List<List<string?>>();
                var truncated = false;
                while (await reader.ReadAsync(ct))
                {
                    if (result.Count >= _options.MaxRows) { truncated = true; break; }
                    result.Add(Enumerable.Range(0, reader.FieldCount).Select(i => Format(reader.GetValue(i))).ToList());
                }
                return new QueryResult(columns, result, truncated);
            }
            catch (SqlException e)
            {
                throw new SandboxException(e.Message, e);
            }
            finally
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        public Task<QueryResult> TableAsync(string ddl, RelationalMap map, string table, string? database = null, CancellationToken ct = default)
        {
            var known = map.Tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase))
                ?? throw new SandboxException($"Unknown table '{table}'.");
            return QueryAsync(ddl, map, $"SELECT TOP ({_options.MaxRows}) * FROM {Q(known.Name)}", database, ct);
        }

        /// <summary>Drops the temporary database of this DDL, if it exists.</summary>
        public async Task DropAsync(string ddl, CancellationToken ct = default)
        {
            await using var master = await OpenAsync(_options.ConnectionString, null, ct);
            await DropAsync(master, DatabaseName(ddl), ct);
        }

        // ------------------------------------------------------------------ helpers

        private async Task<List<TableCount>> CountsAsync(string connection, string database, RelationalMap map, CancellationToken ct)
        {
            await using var db = await OpenAsync(connection, database, ct);
            var counts = new List<TableCount>();
            foreach (var t in map.Tables)
                counts.Add(new TableCount(t.Name, (int)(await ScalarAsync(db, $"SELECT COUNT(*) FROM {Q(t.Name)}", ct))!));
            return counts;
        }

        private static async Task InsertAsync(SqlConnection db, TableInfo table, Dictionary<string, JsonElement> row, CancellationToken ct)
        {
            var columns = table.Columns
                .Where(c => !c.Computed && row.TryGetValue(c.Name, out var v) && !(v.ValueKind == JsonValueKind.Null && (c.HasDefault || c.Identity)))
                .ToList();
            if (columns.Count == 0) return;
            var identity = columns.Any(c => c.Identity);

            var sql = new StringBuilder();
            if (identity) sql.Append($"SET IDENTITY_INSERT {Q(table.Name)} ON; ");
            sql.Append($"INSERT INTO {Q(table.Name)} ({string.Join(", ", columns.Select(c => Q(c.Name)))}) VALUES ({string.Join(", ", columns.Select((_, i) => $"@p{i}"))});");
            if (identity) sql.Append($" SET IDENTITY_INSERT {Q(table.Name)} OFF;");

            await using var command = new SqlCommand(sql.ToString(), db);
            for (var i = 0; i < columns.Count; i++) command.Parameters.AddWithValue($"@p{i}", Value(row[columns[i].Name]));
            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException) when (identity)
            {
                // Leave IDENTITY_INSERT off for the next table (only one table at a time may have it on).
                await using var off = new SqlCommand($"SET IDENTITY_INSERT {Q(table.Name)} OFF", db);
                await off.ExecuteNonQueryAsync(ct);
                throw;
            }
        }

        private static object Value(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when v.TryGetInt64(out var l) => l,
            JsonValueKind.Number => v.GetDecimal(),
            _ => v.ToString()
        };

        private static string? Format(object value) => value switch
        {
            DBNull => null,
            DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.####", CultureInfo.InvariantCulture),
            double f => f.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };

        private static string Describe(Dictionary<string, JsonElement> row) =>
            "{" + string.Join(", ", row.Take(4).Select(kv => $"{kv.Key}: {kv.Value}")) + (row.Count > 4 ? ", …" : "") + "}";

        /// <summary>Tables ordered so that a table comes after the tables its foreign keys reference (cycles kept as they are).</summary>
        public static List<TableInfo> ParentsFirst(IReadOnlyList<TableInfo> tables)
        {
            var result = new List<TableInfo>();
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(TableInfo t)
            {
                if (done.Contains(t.Name) || !visiting.Add(t.Name)) return;
                foreach (var fk in t.ForeignKeys)
                    if (tables.FirstOrDefault(x => string.Equals(x.Name, fk.ParentTable, StringComparison.OrdinalIgnoreCase)) is { } parent && parent != t)
                        Visit(parent);
                done.Add(t.Name);
                result.Add(t);
            }
            foreach (var t in tables) Visit(t);
            return result;
        }

        private static async Task<SqlConnection> OpenAsync(string connectionString, string? database, CancellationToken ct)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (database is not null) builder.InitialCatalog = database;
            var connection = new SqlConnection(builder.ConnectionString);
            try
            {
                await connection.OpenAsync(ct);
                return connection;
            }
            catch (SqlException e)
            {
                await connection.DisposeAsync();
                throw new SandboxException($"SQL Server is not available ({builder.DataSource}): {e.Message}", e);
            }
        }

        private static async Task ExecAsync(SqlConnection connection, string sql, CancellationToken ct)
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(ct);
        }

        private static async Task<object?> ScalarAsync(SqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
        {
            await using var command = new SqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return await command.ExecuteScalarAsync(ct);
        }

        private static async Task DropAsync(SqlConnection master, string name, CancellationToken ct)
        {
            SqlConnection.ClearAllPools();
            await ExecAsync(master, $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}] END", ct);
        }

        private static string Q(string name) => "[" + name.Replace("]", "]]") + "]";
    }
}
