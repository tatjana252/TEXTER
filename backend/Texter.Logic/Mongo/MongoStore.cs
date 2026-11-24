using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using Texter.Erl.Mongo;
using Texter.Logic.Sandbox;

namespace Texter.Logic.Mongo
{
    public class MongoOptions
    {
        public const string SectionName = "Mongo";

        public string ConnectionString { get; set; } = "mongodb://localhost:27017";
        public int QueryTimeoutSeconds { get; set; } = 15;
        public int MaxRows { get; set; } = 500;
    }

    /// <param name="Created">The database did not exist (or had none of the collections) and was created.</param>
    public record MongoDeployResult(string Database, string Server, bool Created, List<string> Collections);

    /// <summary>
    /// The MongoDB side of steps 3 and 4: creates databases with the schema's validators and indexes, loads sample
    /// data, runs aggregation pipelines. A database is either named (created in step 3) or temporary (texter_…,
    /// named after the schema, so the same schema reuses it).
    /// </summary>
    public partial class MongoStore(IOptions<MongoOptions> options)
    {
        private readonly MongoOptions _options = options.Value;
        private MongoClient? _client;

        private MongoClient Client => _client ??= new MongoClient(Settings());

        public string Server => string.Join(",", MongoUrl.Create(_options.ConnectionString).Servers.Select(s => $"{s.Host}:{s.Port}"));

        // MongoDB: no / \ . " $ * < > : | ? or space; at most 63 bytes.
        [GeneratedRegex(@"^[\p{L}_][\p{L}\p{N}_\-]{0,62}$")]
        private static partial Regex NameRegex();

        public static bool IsValidName(string name) => NameRegex().IsMatch(name);

        public static string TemporaryName(string script) =>
            "texter_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))[..16].ToLowerInvariant();

        private MongoClientSettings Settings()
        {
            var settings = MongoClientSettings.FromConnectionString(_options.ConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            settings.ConnectTimeout = TimeSpan.FromSeconds(5);
            return settings;
        }

        // ------------------------------------------------------------------ schema

        /// <summary>Creates the collections (validators) and indexes; existing ones are an error unless dropExisting.</summary>
        public async Task<MongoDeployResult> DeployAsync(string database, MongoSchemaGenerator.MongoSchemaResult schema, bool dropExisting, CancellationToken ct = default)
        {
            database = database.Trim();
            if (!IsValidName(database))
                throw new SandboxException("A MongoDB database name may contain letters, digits, _ and - (no spaces or dots, at most 63 characters), and must start with a letter.");

            return await Guard(async () =>
            {
                var db = Client.GetDatabase(database);
                var existing = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
                var created = !schema.Map.Collections.Any(c => existing.Contains(c.Name));
                var clash = schema.Map.Collections.Where(c => existing.Contains(c.Name)).Select(c => c.Name).ToList();
                if (clash.Count > 0 && !dropExisting)
                    throw new SandboxException($"Database '{database}' already has these collections ({string.Join(", ", clash)}). Turn on \"drop existing collections\" to replace them, or choose another name.");

                foreach (var c in schema.Map.Collections)
                {
                    if (existing.Contains(c.Name)) await db.DropCollectionAsync(c.Name, ct);
                    await db.RunCommandAsync<BsonDocument>(new BsonDocument
                    {
                        ["create"] = c.Name,
                        ["validator"] = new BsonDocument("$jsonSchema", c.Schema),
                        ["validationLevel"] = "strict"
                    }, cancellationToken: ct);
                    if (c.Indexes.Count > 0)
                        await db.RunCommandAsync<BsonDocument>(new BsonDocument
                        {
                            ["createIndexes"] = c.Name,
                            ["indexes"] = new BsonArray(c.Indexes)
                        }, cancellationToken: ct);
                }
                var collections = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
                return new MongoDeployResult(database, Server, created, collections.Order().ToList());
            });
        }

        /// <summary>
        /// The database to work in. Named: created in step 3; it must have the collections of the current schema.
        /// Not named: the temporary one, created from the schema unless it exists.
        /// </summary>
        public async Task<SandboxInfo> EnsureAsync(MongoSchemaGenerator.MongoSchemaResult schema, string? database, CancellationToken ct = default)
        {
            var name = database ?? TemporaryName(schema.Script);
            return await Guard(async () =>
            {
                var db = Client.GetDatabase(name);
                var existing = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
                var missing = schema.Map.Collections.Where(c => !existing.Contains(c.Name)).Select(c => c.Name).ToList();
                if (missing.Count > 0)
                {
                    if (database is not null)
                    {
                        var dbs = await (await Client.ListDatabaseNamesAsync(ct)).ToListAsync(ct);
                        throw new SandboxException(dbs.Contains(database)
                            ? $"Database '{database}' does not have the collections of the current model (missing: {string.Join(", ", missing)}). Create it again in step 3, with \"drop existing collections\" if it has older ones."
                            : $"Database '{database}' does not exist on {Server}. Create it in step 3 (Create database).");
                    }
                    await DeployAsync(name, schema, dropExisting: true, ct);
                }
                return new SandboxInfo(Server, name, await CountsAsync(db, schema, ct));
            });
        }

        // ------------------------------------------------------------------ data

        /// <summary>
        /// Replaces the documents of the schema's collections. Date fields given as strings become dates, null fields
        /// are left out (optional); a document the validator (or a unique index) rejects is reported.
        /// </summary>
        public async Task<LoadResult> LoadAsync(MongoSchemaGenerator.MongoSchemaResult schema, Dictionary<string, List<JsonElement>> data, string? database, CancellationToken ct = default)
        {
            var info = await EnsureAsync(schema, database, ct);
            return await Guard(async () =>
            {
                var db = Client.GetDatabase(info.Database);
                var rejected = new List<string>();
                foreach (var c in schema.Map.Collections)
                {
                    var collection = db.GetCollection<BsonDocument>(c.Name);
                    await collection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, ct);
                    foreach (var json in data.GetValueOrDefault(c.Name) ?? new())
                    {
                        var document = WithoutNulls(BsonDocument.Parse(json.GetRawText()));
                        foreach (var path in c.DateFields) ToDates(document, path.Split('.'));
                        try { await collection.InsertOneAsync(document, cancellationToken: ct); }
                        catch (MongoWriteException e) { rejected.Add($"{c.Name} {Describe(document)}: {Reason(e)}"); }
                    }
                }
                return new LoadResult(await CountsAsync(db, schema, ct), rejected);
            });
        }

        /// <summary>Runs an aggregation; the columns are the query's c0, c1, ... fields.</summary>
        public async Task<QueryResult> QueryAsync(MongoSchemaGenerator.MongoSchemaResult schema, MongoQuery query, string? database, CancellationToken ct = default)
        {
            var info = await EnsureAsync(schema, database, ct);
            return await Guard(async () =>
            {
                var db = Client.GetDatabase(info.Database);
                var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(query.Pipeline.Select(s => s.AsBsonDocument));
                var aggregateOptions = new AggregateOptions { MaxTime = TimeSpan.FromSeconds(_options.QueryTimeoutSeconds) };
                List<BsonDocument> documents;
                try
                {
                    var cursor = query.Collection is null
                        ? await db.AggregateAsync(PipelineDefinition<NoPipelineInput, BsonDocument>.Create(query.Pipeline.Select(s => s.AsBsonDocument)), aggregateOptions, ct)
                        : await db.GetCollection<BsonDocument>(query.Collection).AggregateAsync(pipeline, aggregateOptions, ct);
                    documents = new List<BsonDocument>();
                    while (await cursor.MoveNextAsync(ct))
                    {
                        documents.AddRange(cursor.Current);
                        if (documents.Count > _options.MaxRows) break;
                    }
                }
                catch (MongoCommandException e)
                {
                    throw new SandboxException(e.ErrorMessage ?? e.Message, e);
                }
                var truncated = documents.Count > _options.MaxRows;
                var rows = documents.Take(_options.MaxRows)
                    .Select(d => query.Columns.Select(c => d.TryGetValue(c.Field, out var v) ? Format(v) : null).ToList())
                    .ToList();
                return new QueryResult(query.Columns.Select(c => c.Header).ToList(), rows, truncated);
            });
        }

        /// <summary>Documents of one collection: top-level fields as columns, nested values as JSON.</summary>
        public async Task<QueryResult> CollectionAsync(MongoSchemaGenerator.MongoSchemaResult schema, string name, string? database, CancellationToken ct = default)
        {
            var info = await EnsureAsync(schema, database, ct);
            var collectionName = schema.Map.Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new SandboxException($"Unknown collection '{name}'.");
            return await Guard(async () =>
            {
                var documents = await Client.GetDatabase(info.Database).GetCollection<BsonDocument>(collectionName)
                    .Find(FilterDefinition<BsonDocument>.Empty).Limit(_options.MaxRows + 1).ToListAsync(ct);
                var columns = documents.SelectMany(d => d.Names).Distinct().ToList();
                var rows = documents.Take(_options.MaxRows)
                    .Select(d => columns.Select(c => d.TryGetValue(c, out var v) ? Format(v) : null).ToList()).ToList();
                return new QueryResult(columns, rows, documents.Count > _options.MaxRows);
            });
        }

        public async Task DropDatabaseAsync(string database, CancellationToken ct = default) =>
            await Guard(async () => { await Client.DropDatabaseAsync(database, ct); return 0; });

        // ------------------------------------------------------------------ helpers

        private static async Task<List<TableCount>> CountsAsync(IMongoDatabase db, MongoSchemaGenerator.MongoSchemaResult schema, CancellationToken ct)
        {
            var counts = new List<TableCount>();
            foreach (var c in schema.Map.Collections)
                counts.Add(new TableCount(c.Name, (int)await db.GetCollection<BsonDocument>(c.Name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct)));
            return counts;
        }

        /// <summary>Connection problems become a clear message (MongoDB not installed or not running).</summary>
        private async Task<T> Guard<T>(Func<Task<T>> action)
        {
            try
            {
                return await action();
            }
            catch (TimeoutException e)
            {
                throw new SandboxException($"MongoDB is not available at {Server}: is it installed and running?", e);
            }
            catch (MongoConnectionException e)
            {
                throw new SandboxException($"MongoDB is not available at {Server}: {e.Message}", e);
            }
        }

        private static BsonDocument WithoutNulls(BsonDocument document)
        {
            foreach (var name in document.Names.ToList())
            {
                var value = document[name];
                if (value.IsBsonNull) document.Remove(name);
                else if (value.IsBsonDocument) WithoutNulls(value.AsBsonDocument);
                else if (value.IsBsonArray)
                    foreach (var item in value.AsBsonArray.Where(i => i.IsBsonDocument)) WithoutNulls(item.AsBsonDocument);
            }
            return document;
        }

        /// <summary>A date field (possibly inside arrays) given as a string becomes a BSON date.</summary>
        private static void ToDates(BsonValue node, string[] path, int index = 0)
        {
            if (node.IsBsonArray) { foreach (var item in node.AsBsonArray) ToDates(item, path, index); return; }
            if (!node.IsBsonDocument) return;
            var document = node.AsBsonDocument;
            if (!document.TryGetValue(path[index], out var value)) return;
            if (index < path.Length - 1) { ToDates(value, path, index + 1); return; }

            BsonValue Convert(BsonValue v) =>
                v.IsString && DateTime.TryParse(v.AsString, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                    ? new BsonDateTime(d) : v;
            document[path[index]] = value.IsBsonArray ? new BsonArray(value.AsBsonArray.Select(Convert)) : Convert(value);
        }

        private static string? Format(BsonValue value) => value switch
        {
            BsonNull or BsonUndefined => null,
            BsonDateTime d => d.ToUniversalTime() is var t && t.TimeOfDay == TimeSpan.Zero ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            BsonString s => s.Value,
            BsonDouble f => f.Value.ToString(CultureInfo.InvariantCulture),
            BsonDecimal128 m => m.Value.ToString(),
            BsonBoolean b => b.Value ? "true" : "false",
            BsonDocument or BsonArray => value.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson }),
            _ => value.ToString()
        };

        private static string Describe(BsonDocument d) =>
            "{" + string.Join(", ", d.Elements.Take(3).Select(e => $"{e.Name}: {Format(e.Value)}")) + (d.ElementCount > 3 ? ", …" : "") + "}";

        private static string Reason(MongoWriteException e)
        {
            if (e.WriteError?.Category == ServerErrorCategory.DuplicateKey) return "duplicate key: " + e.WriteError.Message;
            if (e.WriteError?.Details is { } details && details.TryGetValue("details", out var d))
                return "document failed validation: " + d.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson });
            return e.WriteError?.Message ?? e.Message;
        }
    }
}
