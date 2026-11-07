// Checks tests/AssistantEval/questions.json against the knowledge files and SRS v1.5:
// every source section exists, every SRS code is a real requirement, every must-mention fact is
// in its source section, and every knowledge section has at least one question.
// Run from the repo root: node tests/AssistantEval/check-questions.js .
const fs = require('fs'), path = require('path');
const root = process.argv[2];
const dir = path.join(root, 'src/PMS.API/App_Data/knowledge');
const srs = fs.readFileSync(path.join(root, 'docs/SRS_v1.5.md'), 'utf8');
const sections = {};
for (const f of fs.readdirSync(dir).filter(f => f.endsWith('.md') && f !== 'README.md')) {
  const parts = fs.readFileSync(path.join(dir, f), 'utf8').split(/^## /m).slice(1);
  for (const p of parts) { const [h, ...body] = p.split('\n'); sections[f + '#' + h.trim()] = body.join(' ').replace(/\s+/g, ' '); }
}
const { questions } = JSON.parse(fs.readFileSync(path.join(root, 'tests/AssistantEval/questions.json'), 'utf8'));
let problems = 0; const used = new Set(); const ids = new Set();
for (const q of questions) {
  if (ids.has(q.id)) { problems++; console.log('duplicate id', q.id); } ids.add(q.id);
  for (const code of q.srs || []) if (!new RegExp('\| ' + code.replace(/-/g, '\-') + ' \|').test(srs)) { problems++; console.log(q.id, 'SRS code not in v1.5 tables:', code); }
  if (!q.source) continue;
  const text = sections[q.source];
  if (!text) { problems++; console.log(q.id, 'missing source', q.source); continue; }
  used.add(q.source);
  for (const m of q.mustMention) if (!text.toLowerCase().includes(m.toLowerCase())) { problems++; console.log(q.id, 'fact not in source:', JSON.stringify(m)); }
}
const uncovered = Object.keys(sections).filter(s => !used.has(s));
console.log(`${questions.length} questions, ${Object.keys(sections).length} sections, ${problems} problems`);
console.log('sections without a question:', uncovered.length ? '\n  ' + uncovered.join('\n  ') : 'none');
process.exitCode = problems || uncovered.length ? 1 : 0;
