# Pilot plan

Purpose: find out, on real drawings and with real engineers, whether the QC Assistant finds what the QC team finds, how often it is wrong, and how much time it saves, before it goes to the whole office. **Nothing in this plan has been run yet**: the numbers below marked *proposed* are for the QC lead and the owner to confirm or change.

## Who and how long
- 3 to 5 QC engineers on one live project, plus the QC lead and the release manager for support. Two weeks, about 20 to 30 drawings of each QC type the project has (isometric first: it has the most testing behind it; GA and P&ID only with the caveats in the guide).
- The release manager installs the signed build on each pilot PC (it carries the update key) and checks the install (see `docs/runbook.md`). Each PC also needs Claude Code signed in and, for the person who exports the model, Navisworks.

## Before the first drawing
1. Export the project model once (`model_export`) and check `model_summary` (count of lines, duplicates).
2. Put the project's PMS, line list, P&IDs and vendor data in the inputs folder: **without them the check lists those comparisons as not done**, and the pilot would measure a weaker tool than the one you want to judge.
3. The QC lead adds the client's support map and any client rules (each with its source document) to `iso-rules.json` / the requirement pack.
4. Agree what "a finding" is: one thing wrong on one line, tag or sheet, so the hand list and the assistant's list can be compared.

## For each drawing (what the engineer does)
1. The engineer checks the drawing **by hand as usual first** and writes the result in the hand list (`docs/pilot-hand-list-template.csv`: Drawing, Subject (line or tag), Check, Description). Doing it blind to the assistant is what makes the comparison honest.
2. Then run the check (`iso_check`, `pid_compare`, `ga_check`) and save the report.
3. Review each FAIL and REVIEW on the drawing, and tell the assistant what is true: *confirmed*, *false alarm*, or *something it missed* (it records this with `finding_feedback`; for a false alarm it also records the rule behind it as a lesson).
4. Tell it how long the check took with the assistant and your estimate by hand (`pilot_time`).
5. The QC lead runs `pilot_compare` (the saved report against the hand list) per drawing and `pilot_metrics` weekly.

## What is measured
| Measure | How | Proposed target |
|---|---|---|
| Recall | hand findings the assistant reported as FAIL or REVIEW, over all hand findings (`pilot_compare`) | at least 90% pointed at; at least 75% as a FAIL |
| False-alarm rate | FAILs the engineer called false alarms over FAILs reviewed (`pilot_metrics`) | at most 20% |
| Missed, by check | what the engineers report as missed | no check with a systematic blind spot |
| Time | minutes with the assistant against the manual estimate (`pilot_metrics`) | at least 30% saved on isometrics |
| Install | installs that worked over installs tried (logged by Setup) | all |
| Use | prompts, corrections, tool errors per person and day (Knowledge > Team activity) | people keep using it in week 2 |

Read the numbers with their counts. `pilot_compare` counts a FAIL that is not on the hand list as "not matched": it can be a false alarm, a second view of an error that is on the list, or a real error the engineer missed: a person decides each, and the decision goes in as feedback.

## Stop and fix
- A FAIL that was wrong on a drawing that went out, or a real error that was missed on a drawing that went out: stop using it for that check, record it, and the release manager looks the same day.
- A check with more than 30% false alarms after 10 drawings: the QC lead switches it off in the guidance (or adds the client rule) until it is fixed.
- The assistant is an aid. **A person signs off every drawing.**

## Data
Prompts and answers go to Claude (Anthropic). For a client who does not allow that, set `ActivityDetail = "metadata"` (no typed or answered text in the activity log) and do not run the pilot on that client's drawings unless the client has agreed. The feedback records hold the drawing file name, the line or tag and a short note: no drawing content.

## Result
At the end the QC lead writes one page: the numbers with their counts, the checks that work, the checks that do not, what changes before the office rollout (`docs/rollout.md`), and the decision to go ahead, extend the pilot or stop.
