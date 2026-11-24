---
name: qc-platform
description: How the PEC QC Assistant works: status, identity and roles, the shared office folder, approvals, and what to do when something is not ready. Read it at the start of a session or when a tool refuses.
---

# QC platform

## Start of a session
1. `qc_status`: version, user and role, shared folder (reachable, writable), active project, Navisworks, updates. It lists `problems`; tell the user what blocks work before starting a check.
2. `who_am_i`: name, employee code, company, role. If identity is incomplete, the app asks once; do not invent one.
3. `learn_pull`: the lessons the team approved.

## The shared office folder
One folder for the team (skills, lessons, context, documents, requirements, projects, models, reports, users, activity, audit, prompts, tools, updates). Users can **create, overwrite and append** but **not delete**: backups are kept as `<file>.bak_<date-time>`, logs only grow. If the folder cannot be reached the assistant works from local copies and says so.

## Roles (enforced by the tools)
| Role | Can |
|---|---|
| `qc-engineer` | run checks, read everything shared, propose lessons and skills |
| `qc-lead` | + save shared skills, approve lessons, requirements and reports, list users |
| `admin` | + approve shared kit items, change roles (`set_user_role`), manage the shared folder and updates |

A refused call returns `denied: true` with the needed role. Report it to the user; never try another tool to get the same effect.

## Approvals
Tools that write something shared take `apply=false` (a plan, nothing written) and `apply=true` (the user approves in the app; the result says `waitingForApproval` until they do, then call again with the same arguments). Every shared write is audited (who, what, hash of arguments, outcome).

## When something is not ready
- No active project: ask for client, Navisworks model and project inputs folder.
- Navisworks not found: model checks (isometric, GA) cannot run; P&ID comparison can.
- Shared folder unreachable: continue locally, tell the user the lesson or report is saved on this PC only.
