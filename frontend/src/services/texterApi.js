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

  /** ERLD source -> sandbox database from its DDL: { database, tables: [{ name, rows }], examples } */
  erlqDatabase: (code) => post('/api/erlq/database', { code }),

  /** LLM sample data loaded into the sandbox: { tables, rejected } */
  erlqSampleData: (code, requirements) => post('/api/erlq/sample-data', { code, requirements }),

  /** ERLQ query -> { sql, columns, rows, truncated, errors, executionError } */
  erlqRun: (code, query) => post('/api/erlq/run', { code, query }),

  /** Rows of one table: { columns, rows, truncated } */
  erlqTable: (code, table) => post('/api/erlq/table', { code, table })
}
