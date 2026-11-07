// Runs tests/AssistantEval/questions.json against a running API and scores the chat assistant.
//
//   node tests/AssistantEval/run-eval.js http://localhost:5199 [--only rsv-01,dep-02] [--delay 6500]
//
// For each question it records the help sections returned ("sources") and the answer, then reports:
// - retrieval hit: the expected section is among the sources (the main measure of the search);
// - facts: every must-mention fact is in the answer. Facts are English phrases, so for a Vietnamese
//   answer only the facts with numbers are checked (digits compared without separators); read the
//   saved answers for the rest;
// - out of scope: the answer should decline; these are listed for reading, not scored.
// The API allows 10 questions a minute per client, so questions are sent --delay ms apart (default
// 6500) and a 429 waits a minute. The full report is written next to this file as eval-report.json.
const fs = require('fs')
const path = require('path')

const args = process.argv.slice(2)
const baseUrl = (args.find((a) => !a.startsWith('--')) || 'http://localhost:5199').replace(/\/+$/, '')
const option = (name) => {
  const i = args.indexOf(`--${name}`)
  return i >= 0 ? args[i + 1] : undefined
}
const delayMs = Number(option('delay') ?? 6500)
const only = option('only')?.split(',')

const { questions } = JSON.parse(fs.readFileSync(path.join(__dirname, 'questions.json'), 'utf8'))
const selected = only ? questions.filter((q) => only.includes(q.id)) : questions
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const isVietnamese = (text) => /[ăâđêôơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ]/i.test(text)
const digits = (text) => text.replace(/(\d)[.,\s](?=\d{3}\b)/g, '$1')

async function ask(question) {
  for (;;) {
    const response = await fetch(`${baseUrl}/api/public/assistant/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ messages: [{ role: 'user', content: question }] }),
    })
    if (response.status === 429) {
      console.log('  rate limited, waiting a minute...')
      await sleep(61_000)
      continue
    }
    if (!response.ok) throw new Error(`HTTP ${response.status}: ${await response.text()}`)

    const text = await response.text()
    const result = { mode: null, sources: [], answer: '', error: null }
    for (const block of text.split('\n\n')) {
      const name = /^event: (.+)$/m.exec(block)?.[1]
      const data = /^data: (.+)$/m.exec(block)?.[1]
      if (!name || !data) continue
      const payload = JSON.parse(data)
      if (name === 'sources') Object.assign(result, { mode: payload.mode, sources: payload.sources })
      if (name === 'delta') result.answer += payload.text
      if (name === 'error') result.error = payload.message
    }
    return result
  }
}

async function main() {
  console.log(`Assistant evaluation: ${selected.length} questions against ${baseUrl}\n`)
  const rows = []
  for (const [i, q] of selected.entries()) {
    if (i > 0) await sleep(delayMs)
    const r = await ask(q.question)
    const ids = r.sources.map((s) => `${s.file}#${s.heading}`)
    const hit = q.source ? ids.includes(q.source) : null
    const rank = q.source ? ids.indexOf(q.source) + 1 : null

    const vietnamese = isVietnamese(r.answer)
    const checkable = q.mustMention.filter((m) => !vietnamese || /\d/.test(m))
    const missing = checkable.filter((m) =>
      /\d/.test(m)
        ? !digits(r.answer).includes(digits(m).replace(/[^\d]/g, ''))
        : !r.answer.toLowerCase().includes(m.toLowerCase()),
    )

    rows.push({ ...q, mode: r.mode, sources: ids, scores: r.sources.map((s) => s.score), answer: r.answer, error: r.error, hit, rank, vietnamese, missing })
    const mark = q.outOfScope ? 'OOS ' : hit ? 'HIT ' : 'MISS'
    console.log(`${mark} ${q.id.padEnd(7)} ${missing.length ? `missing: ${missing.join(', ')}` : ''}${r.error ? ` error: ${r.error}` : ''}`)
  }

  const scored = rows.filter((r) => r.source)
  const hits = scored.filter((r) => r.hit).length
  const top1 = scored.filter((r) => r.rank === 1).length
  const factsOk = scored.filter((r) => r.missing.length === 0).length
  const summary = {
    when: new Date().toISOString(),
    baseUrl,
    mode: rows[0]?.mode,
    questions: rows.length,
    retrievalHitRate: +(hits / scored.length).toFixed(3),
    retrievalTop1Rate: +(top1 / scored.length).toFixed(3),
    factsCompleteRate: +(factsOk / scored.length).toFixed(3),
    outOfScope: rows.filter((r) => r.outOfScope).map((r) => ({ id: r.id, question: r.question, answer: r.answer })),
  }
  fs.writeFileSync(path.join(__dirname, 'eval-report.json'), JSON.stringify({ summary, rows }, null, 2))

  console.log(`\nRetrieval: ${hits}/${scored.length} found (${top1} ranked first)`)
  console.log(`Facts:     ${factsOk}/${scored.length} answers contain every checkable fact`)
  console.log(`Out of scope answers to read: ${summary.outOfScope.length}`)
  console.log(`Report: ${path.join(__dirname, 'eval-report.json')}`)
}

main().catch((e) => {
  console.error(e.message)
  process.exitCode = 1
})
