async function post(url, body) {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  })
  const data = await response.json().catch(() => null)
  if (!response.ok) {
    throw new Error(data?.detail ?? data?.title ?? `Request failed (${response.status})`)
  }
  return data
}

export const texterApi = {
  /** Requirements text -> { model, diagram, sentences, notes } */
  extractConceptualModel: (text) => post('/api/conceptualmodel/extract', { text }),

  /** Conceptual model -> { code, errors } */
  generateErld: (model, modelName, includeAttribution) =>
    post('/api/erld/generate', { model, modelName, includeAttribution }),

  /** ERLD source -> { code, errors } */
  validateErld: (code) => post('/api/erld/validate', { code }),

  /** ERLD source -> { sql, errors, warnings } (SQL Server DDL) */
  generateSqlServerDdl: (code, includeAttribution, dropExisting) =>
    post('/api/ddl/sqlserver', { code, includeAttribution, dropExisting }),

  /** ERLD source -> MongoDB schema: { script, notEnforced, errors, warnings } */
  generateMongoSchema: (code, dropExisting) => post('/api/ddl/mongodb', { code, dropExisting }),

  /** Creates the MongoDB database's collections and indexes: { database, server, created, tables, error } */
  deployMongoSchema: (code, database, dropExisting) => post('/api/ddl/mongodb/deploy', { code, database, dropExisting }),

  /** Creates the database (if needed) on SQL Server and runs the DDL: { database, server, created, tables, error } */
  deploySqlServerDdl: (code, database, includeAttribution, dropExisting) =>
    post('/api/ddl/deploy', { code, database, includeAttribution, dropExisting }),

  // Step 4. store: 'sqlserver' or 'mongodb'; database: the one created in step 3, or null for a temporary one.

  /** The database to query: { server, database, tables: [{ name, rows }], examples } */
  erlqDatabase: (code, store, database) => post('/api/erlq/database', { code, store, database }),

  /** LLM sample data loaded into the database (replacing its rows / documents): { tables, rejected } */
  erlqSampleData: (code, store, requirements, database) => post('/api/erlq/sample-data', { code, store, requirements, database }),

  /** ERLQ query -> { sql (T-SQL or the pipeline), columns, rows, truncated, errors, executionError } */
  erlqRun: (code, store, query, database) => post('/api/erlq/run', { code, store, query, database }),

  /** Rows of one table / documents of one collection: { columns, rows, truncated } */
  erlqTable: (code, store, table, database) => post('/api/erlq/table', { code, store, table, database })
}
