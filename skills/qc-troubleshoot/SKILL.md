---
name: qc-troubleshoot
description: What to do and what to tell the user when a QC step does not work: no project or inputs, a file that cannot be read, a line list that is not understood, nothing found, an approval that is waiting, a role that is too low. Read it when a tool fails or returns nothing.
---

# When a QC step does not work

Say what happened, what it means for the check, and what the user can do. Never continue as if it had worked, and never turn a gap into a pass.

| Situation | What it means | What to do and say |
|---|---|---|
| "There is no active QC project" | The assistant does not know the client, model or inputs | Ask the user to pick or add the project (Projects page). |
| "inputs folder does not exist / is empty" | Nothing to compare against | Say which inputs the check needs (`inputs_list` `missing`) and who can add them. |
| `cannotBeRead`: "no text layer" | The file is a scan or an image: it cannot be searched or read | Those items are REVIEW. Ask for a text PDF or the native file; do not guess from the image. |
| `cannotBeRead`: "not indexable (.dwg, .jpg ...)" | The format cannot be read here | Ask for a PDF or Excel export. |
| `inputs_search` finds nothing | The words are not in the readable files | Try a part of the tag, other spellings, no `kind`. Report "not found in the readable files", and list unreadable files. |
| `line_list_summary`: no header row / fields not recognised | The line list columns are named in a way the reader does not know | Tell the user which fields are missing. Offer `inputs_read` on the sheet to read the columns by eye, but mark values taken that way as read manually. |
| `line_list_lookup`: no match | The line is not in the line list | That is a finding about the drawing or the list: report it, with the spelling you searched. |
| Line listed twice | Data error in the line list | Report both rows (file, sheet, row). |
| `waitingForApproval` | The user has not approved in the app yet | Tell them to look at the approval window; call again with the same arguments after they approve. |
| `denied: true` | The user's role is too low | Say which role is needed (`qc-lead`, `admin`). Do not try another tool for the same effect. |
| Shared folder offline | Work continues from local copies | Say that lessons and reports are saved on this PC only for now. |
| Excel file "in use" | A colleague has it open | It is read as it was last saved; mention that unsaved changes are not seen. |
| A tool for a comparison does not exist | The check cannot be done by tool yet | Do the part that can be done, mark the rest INFO "not verified (no tool)", and say so first in the answer. |
| The user says the finding is wrong | You may have misread, or the source is not what you used | Re-read the source passage, show it, correct the finding, and record the rule with `learn_record` (skill `qc-learning`). |

## Do not
- Do not retry the same failing call more than once; change something or report it.
- Do not read around a refusal (a path outside the inputs folder, a role limit).
- Do not describe the cause as certain when you are inferring it: say "probably".
