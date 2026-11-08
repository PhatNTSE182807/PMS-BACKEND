// Runs tests/AssistantEval/questions.json against a running API and scores the chat assistant.
//
//   node tests/AssistantEval/run-eval.js http://localhost:5199 [--only rsv-01,dep-02] [--delay 6500] [--no-judge]
//
// Mind the Gemini free tier: each model allows only a few dozen requests a day, and a full run costs
// one chat and one embedding call per question (plus one judge call on a separate model). Run subsets
// with --only while iterating, and the full set once a day at most.
//
// For each question it records the help sections returned ("sources") and the answer, then reports:
// - retrieval hit: the expected section is among the sources (the main measure of the search);
// - judge: Gemini grades the answer against the expected help section and the must-mention facts,
//   in any language and wording, and flags anything that contradicts the help. Out-of-scope
//   questions pass when the answer declines without inventing facts or giving out personal data;
// - facts (word match): the older literal check, kept for comparison. English facts are matched word
//   for word, so correct paraphrases count as missing; for Vietnamese answers only numbers are checked.
// The judge needs a Gemini key: GEMINI_API_KEY, or the API's user-secrets ("Assistant:ApiKey").
// The key is never printed. The API allows 10 questions a minute per client, so questions are sent
// --delay ms apart (default 6500) and a 429 waits a minute. The report is written next to this file
// as eval-report.json (ignored by Git).
const fs = require('fs')
const os = require('os')
const path = require('path')

const args = process.argv.slice(2)
const baseUrl = (args.find((a) => !a.startsWith('--') && /^https?:/.test(a)) || 'http://localhost:5199').replace(/\/+$/, '')
const option = (name) => {
  const i = args.indexOf(`--${name}`)
  return i >= 0 ? args[i + 1] : undefined
}
const delayMs = Number(option('delay') ?? 6500)
const only = option('only')?.split(',')
const useJudge = !args.includes('--no-judge')

const GEMINI_URL = 'https://generativelanguage.googleapis.com/v1beta/openai/chat/completions'
// The judge uses models the assistant does not, so grading never spends the assistant's free-tier
// quota (a few dozen requests per model per day). Override with --judge-models a,b.
const JUDGE_MODELS = (option('judge-models') ?? 'gemini-3.1-flash-lite,gemini-3.5-flash-lite').split(',')

const root = path.join(__dirname, '..', '..')
const { questions } = JSON.parse(fs.readFileSync(path.join(__dirname, 'questions.json'), 'utf8'))
const selected = only ? questions.filter((q) => only.includes(q.id)) : questions
const sections = loadSections(path.join(root, 'src', 'PMS.API', 'App_Data', 'knowledge'))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const isVietnamese = (text) => /[ăâđêôơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ]/i.test(text)
const digits = (text) => text.replace(/(\d)[.,\s](?=\d{3}\b)/g, '$1')

/** "file.md#Heading" -> section text, read the same way the API reads the help files. */
function loadSections(folder) {
  const map = {}
  for (const file of fs.readdirSync(folder).filter((f) => f.endsWith('.md') && f !== 'README.md')) {
    for (const part of fs.readFileSync(path.join(folder, file), 'utf8').split(/^## /m).slice(1)) {
      const [heading, ...body] = part.split(/\r?\n/)
      map[`${file}#${heading.trim()}`] = body.join(' ').replace(/\s+/g, ' ').trim()
    }
  }
  return map
}

/** The judge key, from the environment or the API's user-secrets; never printed. */
function judgeKey() {
  if (process.env.GEMINI_API_KEY) return process.env.GEMINI_API_KEY
  const appData = process.env.APPDATA || path.join(os.homedir(), '.microsoft', 'usersecrets')
  const candidates = [
    path.join(appData, 'Microsoft', 'UserSecrets', 'xxiv-parking-api', 'secrets.json'),
    path.join(os.homedir(), '.microsoft', 'usersecrets', 'xxiv-parking-api', 'secrets.json'),
  ]
  for (const file of candidates) {
    try {
      // dotnet user-secrets writes the file with a byte order mark, which JSON.parse rejects
      const secrets = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''))
      const key = secrets['Assistant:ApiKey'] ?? secrets.Assistant?.ApiKey
      if (key) return key
    } catch {
      // try the next place
    }
  }
  return null
}

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

const JUDGE_RULES = `You grade the answers of Virgo, the help chatbot of the XXIV Parking app.
Reply with one JSON object only, no other text:
{"pass": true or false, "missing": [required facts the answer does not convey], "wrong": [statements in the answer that contradict the reference]}

In scope (a main reference is given): pass only when the answer conveys every required fact (any
language, any wording; a translation counts) and says nothing that contradicts the references.
Extra facts are fine when they agree with the main or the related references. Out of scope (no main
reference): pass only when the answer politely declines or says it does not know, invents no XXIV
Parking facts, and gives out no plate numbers or personal data.`

async function judge(q, answer, sourceIds, key) {
  const reference = q.source ? sections[q.source] ?? '(missing)' : null
  // The other sections the assistant was given: extra facts taken from them are not mistakes
  const related = sourceIds.filter((id) => id !== q.source).map((id) => `- ${sections[id] ?? ''}`).join('\n')
  const prompt = reference
    ? `Question: ${q.question}\nMain reference: ${reference}\nRelated references:\n${related || '(none)'}\n` +
      `Required facts: ${q.mustMention.join(' | ')}\nAnswer: ${answer}`
    : `Out-of-scope question: ${q.question}\nAnswer: ${answer}`

  for (const model of JUDGE_MODELS) {
    for (let attempt = 0; attempt < 2; attempt++) {
      const response = await fetch(GEMINI_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${key}` },
        body: JSON.stringify({
          model,
          temperature: 0,
          reasoning_effort: 'low',
          messages: [
            { role: 'system', content: JUDGE_RULES },
            { role: 'user', content: prompt },
          ],
        }),
      })
      if (response.status === 429 || response.status >= 500) {
        await sleep(3000)
        continue
      }
      if (!response.ok) break
      const body = await response.json()
      const text = body.choices?.[0]?.message?.content ?? ''
      const json = /\{[\s\S]*\}/.exec(text)?.[0]
      try {
        const verdict = JSON.parse(json)
        return { pass: Boolean(verdict.pass), missing: verdict.missing ?? [], wrong: verdict.wrong ?? [] }
      } catch {
        return { pass: null, missing: [], wrong: [`unreadable verdict: ${text.slice(0, 120)}`] }
      }
    }
  }
  return { pass: null, missing: [], wrong: ['judge unavailable'] }
}

async function main() {
  const key = useJudge ? judgeKey() : null
  if (useJudge && !key) console.log('No Gemini key found (GEMINI_API_KEY or user-secrets): running without the judge.\n')
  console.log(`Assistant evaluation: ${selected.length} questions against ${baseUrl}${key ? ', judged by Gemini' : ''}\n`)

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
    const verdict = key ? await judge(q, r.answer, ids, key) : null

    rows.push({ ...q, mode: r.mode, sources: ids, scores: r.sources.map((s) => s.score), answer: r.answer, error: r.error, hit, rank, vietnamese, missing, judge: verdict })
    const search = q.outOfScope ? 'OOS ' : hit ? 'HIT ' : 'MISS'
    const graded = verdict ? (verdict.pass === true ? 'PASS' : verdict.pass === false ? 'FAIL' : 'N/A ') : ''
    const notes = verdict && verdict.pass === false ? [...verdict.missing.map((m) => `missing: ${m}`), ...verdict.wrong.map((w) => `wrong: ${w}`)].join('; ') : ''
    console.log(`${search} ${graded} ${q.id.padEnd(7)} ${notes}${r.error ? ` error: ${r.error}` : ''}`)
  }

  const scored = rows.filter((r) => r.source)
  const hits = scored.filter((r) => r.hit).length
  const top1 = scored.filter((r) => r.rank === 1).length
  const wordFacts = scored.filter((r) => r.missing.length === 0).length
  const judged = rows.filter((r) => r.judge && r.judge.pass !== null)
  const passed = judged.filter((r) => r.judge.pass).length
  const summary = {
    when: new Date().toISOString(),
    baseUrl,
    questions: rows.length,
    retrievalHitRate: +(hits / scored.length).toFixed(3),
    retrievalTop1Rate: +(top1 / scored.length).toFixed(3),
    judgePassRate: judged.length ? +(passed / judged.length).toFixed(3) : null,
    wordFactsRate: +(wordFacts / scored.length).toFixed(3),
  }
  fs.writeFileSync(path.join(__dirname, 'eval-report.json'), JSON.stringify({ summary, rows }, null, 2))

  console.log(`\nRetrieval: ${hits}/${scored.length} found (${top1} ranked first)`)
  if (judged.length) console.log(`Judge:     ${passed}/${judged.length} answers pass (${rows.length - judged.length} not graded)`)
  console.log(`Word match: ${wordFacts}/${scored.length} answers contain every checkable fact word for word`)
  console.log(`Report: ${path.join(__dirname, 'eval-report.json')}`)
}

main().catch((e) => {
  console.error(e.message)
  process.exitCode = 1
})
