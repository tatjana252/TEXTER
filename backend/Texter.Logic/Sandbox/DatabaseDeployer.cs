using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Texter.Logic.Sandbox
{
    public class DeployOptions
    {
        public const string SectionName = "Deploy";

        /// <summary>SQL Server where step 3 creates the user's databases (the instance SSMS connects to).</summary>
        public string ConnectionString { get; set; } = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=60";
    }

    /// <param name="Created">The database did not exist and was created.</param>
    /// <param name="Tables">Tables in the database after the script.</param>
    public record DeployResult(string Database, string Server, bool Created, List<string> Tables);

    /// <summary>
    /// Step 3: creates a database with the user's name (unless it exists) and runs the DDL in it, so that the schema
    /// can be opened in SSMS without copying the script.
    /// </summary>
    public partial class DatabaseDeployer(IOptions<DeployOptions> options)
    {
        private readonly DeployOptions _options = options.Value;

        // Letters (also č ć ž š đ), digits, _ - and spaces; not starting with a space or a digit-only name.
        [GeneratedRegex(@"^[\p{L}_][\p{L}\p{N}_\- ]{0,127}$")]
        private static partial Regex NameRegex();

        public static bool IsValidName(string name) => NameRegex().IsMatch(name) && !name.EndsWith(' ');

        public async Task<DeployResult> DeployAsync(string database, string ddl, CancellationToken ct = default)
        {
            database = database.Trim();
            if (!IsValidName(database))
                throw new SandboxException("The database name may contain letters, digits, _, - and spaces (at most 128 characters), and must start with a letter.");

            var builder = new SqlConnectionStringBuilder(_options.ConnectionString);
            var server = builder.DataSource;
            bool created;
            try
            {
                await using (var master = await OpenAsync(builder, null, ct))
                {
                    await using var exists = new SqlCommand("SELECT DB_ID(@name)", master);
                    exists.Parameters.AddWithValue("@name", database);
                    created = await exists.ExecuteScalarAsync(ct) is DBNull or null;
                    if (created)
                    {
                        await using var create = new SqlCommand($"CREATE DATABASE {Q(database)}", master) { CommandTimeout = 120 };
                        await create.ExecuteNonQueryAsync(ct);
                    }
                }

                await using var db = await OpenAsync(builder, database, ct);
                await using (var run = new SqlCommand(ddl, db) { CommandTimeout = 120 })
                    await run.ExecuteNonQueryAsync(ct);

                var tables = new List<string>();
                await using var list = new SqlCommand("SELECT name FROM sys.tables ORDER BY name", db);
                await using var reader = await list.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
                return new DeployResult(database, server, created, tables);
            }
            catch (SqlException e) when (e.Number == 2714)
            {
                throw new SandboxException($"Database '{database}' already has these tables ({e.Message}). Turn on \"drop existing tables\" to replace them, or choose another name.", e);
            }
            catch (SqlException e)
            {
                throw new SandboxException($"SQL Server ({server}): {e.Message}", e);
            }
        }

        private static async Task<SqlConnection> OpenAsync(SqlConnectionStringBuilder builder, string? database, CancellationToken ct)
        {
            var b = new SqlConnectionStringBuilder(builder.ConnectionString);
            if (database is not null) b.InitialCatalog = database;
            var connection = new SqlConnection(b.ConnectionString);
            await connection.OpenAsync(ct);
            return connection;
        }

        private static string Q(string name) => "[" + name.Replace("]", "]]") + "]";
    }
}
