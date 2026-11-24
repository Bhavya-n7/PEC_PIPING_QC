# PEC QC Assistant - guide

## 1. What it does
You type what you need in plain words; Claude does it with tools on your PC. The assistant is built for the QC team and covers three kinds of check:

| QC type | Checked | Against |
|---|---|---|
| Piping isometric | isometric drawing (PDF) | Navisworks 3D model, PMS, P&ID, line list, vendor data |
| P&ID | redrawn P&ID (PDF) | the client's original P&ID (PDF) |
| GA drawing | general arrangement drawing (PDF) | Navisworks 3D model, P&ID, vendor data |

**QC is read-only.** The assistant never changes a model, a drawing or a client document. It finds what does not agree and shows, for each finding, what the drawing says, what the source says, and where each came from. People decide which side is right and sign off. Anything it cannot decide is shown as **REVIEW**, never as a pass.

**What is available in this version (0.1):** the platform: your identity and role, QC projects, the shared folder, skills and lessons, reading PDF, Excel and Word files, and answering from the project inputs. The check tools for the three QC types are added step by step in the next versions; the guide is updated with each one.

## 2. Setup (first time)
1. Run `PecQcSetup.exe`. It installs for your Windows user (no administrator rights).
2. Choose the shared office folder if your office has one. Skills, lessons, projects, reports, user list and updates are shared there.
3. Start **PEC QC Assistant**. Enter your name, employee code and company once.
4. Press **Sign in with Claude** at the bottom of the left menu and sign in with your own Claude account. No API key is needed.
5. Add your QC project (Projects, then Add project): the client, the Navisworks model and the folder with the project inputs.

## 3. Your first task
Add or pick a **project** (Projects), then try a card on the start page, for example "Which project are we on?" or "What project inputs do we have?". Type your own request any time.

## 4. Projects
A project card says which client, project number, Navisworks model and inputs folder you work on. The **inputs folder** holds the PMS, the line list, the P&IDs and the vendor data of that project; if you leave it empty the assistant uses the project's folder on the shared drive. The active project is sent with every request, so you never repeat it. Project cards are shared: a colleague sees the project you add and picks it with one click. The status light **Project inputs** shows whether the folder is there and how many files it holds.

## 5. Quick tasks
The cards on the start page are ready-made requests: check an isometric, check a folder of isometrics, compare a redrawn P&ID with the original, check a GA drawing, list the project inputs, ask what a specification says, and save what we learned. The assistant asks only for what is missing, such as a drawing path. Edit the list in the quick-tasks file to add your own.

## 5b. Model and effort
Under the message box choose the Claude model and effort. Medium is the default; higher thinks longer and costs more.

## 6. Approvals: you stay in control
QC does not change models or drawings, so there is little to approve. What the assistant **saves for everyone** needs your approval: a new shared skill, a lesson, a requirement pack, a custom tool, a published report. A window shows what and where. **Approve** lets it run once, exactly that action; **Reject** stops it. Every shared write is recorded in an audit log.

## 7. Knowledge: lessons, documents, learning
The assistant keeps **lessons** (corrections, preferences, gotchas). When you tell it a finding is wrong, it records the rule so the mistake is not repeated; an administrator approves lessons into the company list that every user's assistant reads. Put client procedures and standards in the documents folder to make them searchable.

## 7b. The shared office folder, roles and custom tools
One shared folder holds skills, lessons, requirement packs, projects, model snapshots, reports, the user list, the activity and audit logs and updates. If it is not reachable the assistant works with local copies. On the shared folder you can create and change files but not delete them; ask an administrator to remove something. Everyone has a **role**: qc-engineer (run checks), qc-lead (also save shared skills and see the user list) and admin (also approve shared items and change roles). Settings shows yours.

## 7c. Navisworks
Isometric and GA checks read the 3D model through your own Navisworks Manage, in the background and on a copy: your model is never changed. It works with the licence you have, education or commercial; which one is allowed for company work is your company's responsibility. The status light **Navisworks** shows whether it was found.

## 8. Updates and versions
The app checks `latest.json` in the update source (default: shared folder\updates). When a newer version exists press **Update now**, or let it install by itself (Settings). The version is shown in the window title.

## 9. Tokens and cost
The assistant keeps its instructions short and reads guides and documents only when needed. The chat shows tokens used per request.

## 10. FAQ
- **Can it change my drawings or my model?** No. QC is read-only by design.
- **Does my project data go to Claude?** Only what appears in your messages and tool results (for example names, tags and values in findings). The model and the drawings are read on your PC.
- **What happens when it cannot read a drawing?** It says so and marks the check REVIEW. Scanned (image-only) drawings cannot be read yet.
- **Why does it say REVIEW?** The drawing or the source is incomplete or unclear, for example a value that is blank in the model. A person has to look.
- **Who sees what I ask?** Prompts and what the assistant did are written to the shared activity log with your name and employee code, so the team can support you and learn from every task.

## 11. Troubleshooting
- **"Claude: not signed in"**: press Sign in with Claude and finish in the browser.
- **Shared: offline**: the shared folder cannot be reached; the assistant uses local copies. Check the network or the folder in Settings.
- **Shared: read only**: you can use what is there, but cannot save shared items. Ask an administrator for modify rights.
- **Navisworks not found**: install Navisworks Manage, or set its folder in Settings.
- **Project inputs: folder missing or empty**: edit the project and set the right folder, or put the PMS, line list, P&IDs and vendor data in it.
- **A step failed**: the message shows what the tool reported; the assistant explains it and never claims success when a tool failed.
