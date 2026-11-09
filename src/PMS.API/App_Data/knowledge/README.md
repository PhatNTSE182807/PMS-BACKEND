# Assistant knowledge base

These Markdown files are what the customer chat assistant answers from (retrieval-augmented
generation). They are read at start-up, split into chunks and embedded.

Writing rules:

- One topic per file, one question-sized section per `##` heading. A section is the unit the
  assistant retrieves, so it must make sense on its own: repeat the subject ("a booking", "the
  deposit") instead of writing "it" or "as above".
- Write for customers (User and Guest): plain English, no internal names, no requirement codes
  (`FR-…`, `BR-…`), no Staff-only procedures.
- The source of truth is SRS v1.5 (`docs/SRS_v1.5.md`); `tests/AssistantEval/questions.json`
  maps every evaluation question to the SRS requirements it checks.
- Only state what the system really does. When a value is set per lot (prices, fines), say so and
  point to the lot's price list instead of quoting a number.
- When a rule or a default value changes in code or configuration (`Parking` section of
  `appsettings.json`), update the matching section here in the same commit.
- This README is not indexed.
