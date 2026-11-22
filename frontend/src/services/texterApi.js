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
  /** Requirements text -> { model, diagram } */
  extractConceptualModel: (text) => post('/api/conceptualmodel/extract', { text })
}
