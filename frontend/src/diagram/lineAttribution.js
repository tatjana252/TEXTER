import { levelFor } from './confidence'

const SOURCE = /@source\(([^)]*)\)/
const CONFIDENCE = /@confidence\(([^)]*)\)/

const parse = (line) => ({
  sentenceIds: line?.match(SOURCE)?.[1].split(',').map((s) => s.trim()) ?? [],
  confidence: line?.match(CONFIDENCE)?.[1].trim() ?? null
})

/** A line that only annotates what follows: "@source(...) ..." in ERLD, "-- ..." in SQL. */
const isAnnotationLine = (line) => /^\s*(@|--)/.test(line ?? '')

/**
 * Attribution of a line of ERLD or SQL code: its own @source / @confidence, or, for a declaration
 * (entity, CREATE TABLE, ...), those on the annotation / comment lines right above it.
 * @returns {{ sentenceIds: string[], spans: {start, length}[], level: string }}
 */
export function attributionAt(code, lineNumber, sentences) {
  const lines = code?.split(/\r?\n/) ?? []
  let found = parse(lines[lineNumber - 1])
  for (let i = lineNumber - 2; i >= 0 && !found.sentenceIds.length && !found.confidence && isAnnotationLine(lines[i]); i--) {
    found = parse(lines[i])
  }
  const spans = sentences
    .filter((s) => found.sentenceIds.includes(s.id))
    .map((s) => ({ start: s.start, length: s.length }))
  return { sentenceIds: found.sentenceIds, spans, level: found.confidence ? levelFor(found.confidence) : 'High' }
}
