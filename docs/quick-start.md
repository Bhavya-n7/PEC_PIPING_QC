# Quick start for QC engineers

1. **Install**: run `PecQcSetup.exe` from the office update folder. It needs Claude Code (Setup tells you if it is missing). Navisworks is only needed on the PC that exports the model.
2. **First start**: enter your name, employee code and company once. Everything the assistant does is logged against it.
3. **Add your project** (Projects): client, project number, the Navisworks model file, and the inputs folder with the PMS, line list, P&IDs and vendor data.
4. **Ask**, or pick a quick task:
   - "Check the isometric `<file>.pdf`": identity, title block, BOM, coordinates and pipe lengths against the model; line list, PMS class and P&ID where you have them.
   - "Check the redrawn P&ID `<file>.pdf` against the original": missing, changed and extra lines, tags and notes.
   - "Check the GA drawing `<file>.pdf`": tags and coordinates against the model; line numbers on a piping plan against the model, the line list and the P&IDs.
5. **Read the answer in this order**: the verdict, every **FAIL** (the drawing value, the source value and where the source is), the **REVIEW** items (a person must look), then **not verified** (what could not be checked). *A clean verdict means nothing was found among what could be compared, not that the drawing is right.* Symbols, routing and clearances are always yours to check.
6. **Save the report** when asked: it needs your approval and goes to `reports\<project>\`.
7. **Correct the assistant** when it is wrong and say why, with the source. It records a lesson; the QC lead approves the true ones for the whole team.
8. **Do not type passwords or keys.** What you type and the files it reads for you are processed by Claude (Anthropic), so only use it on material PEC allows to be processed that way.
