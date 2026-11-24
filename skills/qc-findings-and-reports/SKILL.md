---
name: qc-findings-and-reports
description: How to write QC findings and the report: what OK, REVIEW, FAIL and INFO mean, what a FAIL must contain, the order of the answer, and how to save the result with qc_report_write. Read it before you report the result of any check.
---

# Findings and reports

## The four results
| Result | Meaning | Needs |
|---|---|---|
| **FAIL** | The drawing and the source disagree | the subject, the value on the drawing, the value in the source, and the source document with page / sheet / row |
| **REVIEW** | It cannot be decided and a person must look | the subject and the reason (a value is blank, unclear, unreadable, ambiguous, a rule is only a candidate) |
| **OK** | They agree | the subject and what was compared |
| **INFO** | Something cannot be verified at all (not in the model, no tool for it) | what and why |

**A FAIL without a source is not allowed**: if the source does not say, it is a REVIEW. `qc_report_write` refuses a FAIL that lacks either value or the source document, and tells you what is missing.

## FAIL or REVIEW?
- Source says 300 psig, drawing says 150 psig: **FAIL**.
- Line list has no test pressure for the line: **REVIEW** ("not in the line list").
- The PMS page has no text layer: **REVIEW** for every item that needed it.
- The two sources disagree with each other (model says 10, line list says 300, drawing says 300): **REVIEW**, show all three values; the drawing agrees with one of them but a person must say which is right.
- A client rule marked `candidate` is broken: **REVIEW**, never FAIL.
- Units differ or are not stated: **REVIEW**.
- Something the tools cannot reach (graphics, symbols, line routing, the model when no model tool is available): **INFO**, "not verified".

## The verdict
The worst finding: FAIL, then REVIEW, else OK. **No findings is "NOT CHECKED", never "OK".** Say at the end which checklist items were done and which were not (`qc_checklist`).

## The answer to the user
1. **Verdict** and the counts: "FAIL: 2 findings, REVIEW: 3".
2. A **table of FAIL and REVIEW** findings: subject, what, drawing value, source value, where the source is.
3. **What I could not check** and why.
4. The path of the saved report.
Short. Do not list the OK items in the chat; they are in the report. Do not argue the finding away: the person decides which side is right and signs off.

## Saving the report
1. Build the findings as a JSON array: `check`, `status`, `subject`, `message`, `drawing`, `source`, `sourceDoc`, `sourcePage`, `location`.
2. `qc_report_write` with `apply=false`: it validates, shows the verdict and the files it would write. Fix every error it lists.
3. Show the user the verdict and the findings; when they agree, call again with `apply=true`. The app asks them to approve; the report is written to the project's folder in `reports` as Excel and Markdown, never over an older report.
4. Give the user both paths.

## Never
- Never turn a REVIEW into a FAIL to look decisive, or a FAIL into an OK to look clean.
- Never write a finding from memory. Every value comes from the drawing or a source you read in this chat.
