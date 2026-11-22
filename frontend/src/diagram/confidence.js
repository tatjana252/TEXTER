// Confidence levels as returned by the backend (ConfidenceLevel enum).
export const CONFIDENCE = {
  High: { label: 'Certain', stroke: '#2f9e44', fill: '#e6f6ea', text: '#1e6b2e' },
  Medium: { label: 'Fairly certain', stroke: '#e0a800', fill: '#fff6d5', text: '#7a5c00' },
  Low: { label: 'Uncertain', stroke: '#d63c3c', fill: '#fde6e6', text: '#8f1f1f' }
}

export const CONFIDENCE_ORDER = ['High', 'Medium', 'Low']

export const colorsFor = (level) => CONFIDENCE[level] ?? CONFIDENCE.Low

/** How directly the cited facts support an element (ConfidenceCategory enum). */
export const CATEGORY = {
  Stated: 'stated',
  Inferred: 'inferred',
  Assumed: 'assumed'
}

/** Level of a bare category, e.g. from an ERLD @confidence(stated) annotation (no caps applied). */
export const levelFor = (category) =>
  ({ stated: 'High', inferred: 'Medium', assumed: 'Low' })[String(category).toLowerCase()] ?? 'Low'
