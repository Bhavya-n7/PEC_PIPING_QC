---
name: qc-learning
description: How to record, find and reuse what the QC team learns: corrections, client rules, project decisions, each with its source. Read it when the user corrects a finding or says remember this.
---

# QC learning

## When to record
- The user says a finding is wrong, or gives the rule behind it.
- A client or project rule is found that was not known (PMS note, client procedure, a convention on the drawings).
- A check needed a workaround that the next person would need too.

## How
1. Re-check the finding first; do not record a lesson you have not understood.
2. `learn_record` with: the rule in one or two sentences, **its source** (document, section, page, or "told by <name>"), the client/project it applies to, and the QC type. Show the entry to the user before saving; saving needs their approval.
3. Never record a guess, a personal opinion, or a client value that was not read from a source. Never record passwords, server names or paths of the company network.

## Reuse
`learn_pull` at the start of a session; `learn_search` for a topic; `recall` for saved client notes. A lesson that disagrees with a project document is a REVIEW item: say both and let the QC lead decide.

## Context notes
`remember` / `forget` keep short per-project notes (what is set up, what is pending, key decisions). Keep them factual and dated.

## For the QC lead and the administrator: curating
Lessons are usable by everyone the moment they are recorded; curating makes the good ones permanent and hides the wrong ones.
- `learn_pending`: what nobody has decided on, strongest first (**strong** = several different people recorded the same thing; corrections come first). Show each with who recorded it and its context. Needs the role qc-lead or admin.
- `learn_review` (`approve` or `reject`, the ids, `apply=true` after showing the user): approve = permanent in the company list and in `LEARNED.md` (the previous version is kept); reject = hidden from everyone. Never approve or reject on your own: ask the user which ids.
- `activity_summary` (per day and person: prompts, corrections, tool calls, tool errors) and `activity_report` (the detail) show where the assistant was corrected or a tool failed: record the lesson behind each correction. Both need qc-lead or admin: other people's prompts are not for everyone.
- Lessons and activity written while the shared folder was away are sent back by themselves at start; `sync_offline` does it on request.
