export type DiffKind = 'context' | 'added' | 'removed' | 'skip'

export interface DiffRow {
  kind: DiffKind
  text: string
  oldLine?: number
  newLine?: number
  skipped?: number
  sectionId?: string
  collapsedRows?: DiffRow[]
}

interface Edit {
  kind: Exclude<DiffKind, 'skip'>
  text: string
}

const splitLines = (value: string) => value.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n')

const fallbackEdits = (before: string[], after: string[]): Edit[] => {
  let prefix = 0
  while (prefix < before.length && prefix < after.length && before[prefix] === after[prefix]) prefix++

  let suffix = 0
  while (suffix < before.length - prefix && suffix < after.length - prefix
    && before[before.length - 1 - suffix] === after[after.length - 1 - suffix]) suffix++

  return [
    ...before.slice(0, prefix).map(text => ({ kind: 'context' as const, text })),
    ...before.slice(prefix, before.length - suffix).map(text => ({ kind: 'removed' as const, text })),
    ...after.slice(prefix, after.length - suffix).map(text => ({ kind: 'added' as const, text })),
    ...before.slice(before.length - suffix).map(text => ({ kind: 'context' as const, text })),
  ]
}

const buildEdits = (before: string[], after: string[]): Edit[] => {
  // Keep the dynamic-programming matrix bounded for large generated assets.
  if (before.length * after.length > 4_000_000) return fallbackEdits(before, after)

  const width = after.length + 1
  const table = new Uint32Array((before.length + 1) * width)
  for (let oldIndex = before.length - 1; oldIndex >= 0; oldIndex--) {
    for (let newIndex = after.length - 1; newIndex >= 0; newIndex--) {
      const index = oldIndex * width + newIndex
      table[index] = before[oldIndex] === after[newIndex]
        ? table[(oldIndex + 1) * width + newIndex + 1] + 1
        : Math.max(table[(oldIndex + 1) * width + newIndex], table[index + 1])
    }
  }

  const edits: Edit[] = []
  let oldIndex = 0
  let newIndex = 0
  while (oldIndex < before.length && newIndex < after.length) {
    if (before[oldIndex] === after[newIndex]) {
      edits.push({ kind: 'context', text: before[oldIndex] })
      oldIndex++
      newIndex++
    } else if (table[(oldIndex + 1) * width + newIndex] >= table[oldIndex * width + newIndex + 1]) {
      edits.push({ kind: 'removed', text: before[oldIndex++] })
    } else {
      edits.push({ kind: 'added', text: after[newIndex++] })
    }
  }
  while (oldIndex < before.length) edits.push({ kind: 'removed', text: before[oldIndex++] })
  while (newIndex < after.length) edits.push({ kind: 'added', text: after[newIndex++] })
  return edits
}

export const createUnifiedDiff = (baseline: string, current: string, contextLines = 3): DiffRow[] => {
  const edits = buildEdits(splitLines(baseline), splitLines(current))
  const rows: DiffRow[] = []
  let oldLine = 1
  let newLine = 1

  for (const edit of edits) {
    const row: DiffRow = { kind: edit.kind, text: edit.text }
    if (edit.kind !== 'added') row.oldLine = oldLine++
    if (edit.kind !== 'removed') row.newLine = newLine++
    rows.push(row)
  }

  if (!rows.some(row => row.kind !== 'context')) return rows
  const visible = rows.map((row, index) => row.kind !== 'context'
    || rows.slice(Math.max(0, index - contextLines), index + contextLines + 1).some(candidate => candidate.kind !== 'context'))
  const compact: DiffRow[] = []
  let section = 0
  for (let index = 0; index < rows.length;) {
    if (visible[index]) {
      compact.push(rows[index++])
      continue
    }
    const start = index
    while (index < rows.length && !visible[index]) index++
    compact.push({
      kind: 'skip',
      text: '',
      skipped: index - start,
      sectionId: `section-${section++}`,
      collapsedRows: rows.slice(start, index),
    })
  }
  return compact
}
