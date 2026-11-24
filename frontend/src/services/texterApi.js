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

  /** Creates the database (if needed) on SQL Server and runs the DDL: { database, server, created, tables, error } */
  deploySqlServerDdl: (code, database, includeAttribution, dropExisting) =>
    post('/api/ddl/deploy', { code, database, includeAttribution, dropExisting }),

  // Step 4. database: the one created in step 3, or null for a temporary database built from the DDL.

  /** The database to query: { server, database, tables: [{ name, rows }], examples } */
  erlqDatabase: (code, database) => post('/api/erlq/database', { code, database }),

  /** LLM sample data loaded into the database (replacing its rows): { tables, rejected } */
  erlqSampleData: (code, requirements, database) => post('/api/erlq/sample-data', { code, requirements, database }),

  /** ERLQ query -> { sql, columns, rows, truncated, errors, executionError } */
  erlqRun: (code, query, database) => post('/api/erlq/run', { code, query, database }),

  /** Rows of one table: { columns, rows, truncated } */
  erlqTable: (code, table, database) => post('/api/erlq/table', { code, table, database })
}
