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
  validateErld: (code) => post('/api/erld/validate', { code })
}
