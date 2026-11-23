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
    /// Step 4's physical store: a SQL Server database built from the DDL of step 3, holding sample data.
    /// The database name is derived from the DDL, so the same DDL reuses the same database.
    /// Queries run in a transaction that is always rolled back.
    /// </summary>
    public class SandboxDatabase(IOptions<SandboxOptions> options)
    {
        private readonly SandboxOptions _options = options.Value;

        /// <summary>The instance the sandbox databases live on, as SSMS would show it.</summary>
        public string Server => new SqlConnectionStringBuilder(_options.ConnectionString).DataSource;

        public static string DatabaseName(string ddl) =>
            "texter_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ddl)))[..16].ToLowerInvariant();

        /// <summary>Creates the database from the DDL unless it already exists.</summary>
        public async Task<SandboxInfo> EnsureAsync(string ddl, RelationalMap map, CancellationToken ct = default)
        {
            var name = DatabaseName(ddl);
            await using (var master = await OpenAsync(null, ct))
            {
                var exists = await ScalarAsync(master, "SELECT COUNT(*) FROM sys.databases WHERE name = @n", ct, ("@n", name)) is int n && n > 0;
                if (!exists)
                {
                    await ExecAsync(master, $"CREATE DATABASE [{name}]", ct);
                    try
                    {
                        await using var db = await OpenAsync(name, ct);
                        await ExecAsync(db, ddl, ct);
                    }
                    catch (SqlException e)
                    {
                        await DropAsync(master, name, ct);
                        throw new SandboxException($"The DDL could not be executed: {e.Message}", e);
                    }
                }
            }
            return new SandboxInfo(Server, name, await CountsAsync(name, map, ct));
        }

        /// <summary>
        /// Replaces all rows with the given data. Rows are inserted parents first; a row that violates a constraint
        /// is retried after the others and finally reported, not silently dropped.
        /// </summary>
        public async Task<LoadResult> LoadAsync(string ddl, RelationalMap map, Dictionary<string, List<Dictionary<string, JsonElement>>> data, CancellationToken ct = default)
        {
            var info = await EnsureAsync(ddl, map, ct);
            await using var db = await OpenAsync(info.Database, ct);
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
            return new LoadResult(await CountsAsync(info.Database, map, ct), rejected);
        }

        public async Task<QueryResult> QueryAsync(string ddl, RelationalMap map, string sql, CancellationToken ct = default)
        {
            var info = await EnsureAsync(ddl, map, ct);
            await using var db = await OpenAsync(info.Database, ct);
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

        public Task<QueryResult> TableAsync(string ddl, RelationalMap map, string table, CancellationToken ct = default)
        {
            var known = map.Tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase))
                ?? throw new SandboxException($"Unknown table '{table}'.");
            return QueryAsync(ddl, map, $"SELECT TOP ({_options.MaxRows}) * FROM {Q(known.Name)}", ct);
        }

        /// <summary>Drops the database of this DDL, if it exists.</summary>
        public async Task DropAsync(string ddl, CancellationToken ct = default)
        {
            await using var master = await OpenAsync(null, ct);
            await DropAsync(master, DatabaseName(ddl), ct);
        }

        // ------------------------------------------------------------------ helpers

        private async Task<List<TableCount>> CountsAsync(string database, RelationalMap map, CancellationToken ct)
        {
            await using var db = await OpenAsync(database, ct);
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

        private async Task<SqlConnection> OpenAsync(string? database, CancellationToken ct)
        {
            var builder = new SqlConnectionStringBuilder(_options.ConnectionString);
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
