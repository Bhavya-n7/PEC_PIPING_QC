# Demo script (what to show, what to say)

Shown to the QC team on the PC that has Navisworks Manage (a student licence is fine for a **demonstration**; use for company work needs the company's commercial licence, which is the company's responsibility). Use the real isometric and its model; do not use client material that PEC has not cleared for a cloud model (what you type and the files the assistant reads are processed by Claude).

## Before
- Installed build, first start done (name, employee code), Claude Code signed in.
- Projects > Add project: client, number, the Navisworks model (`.nwd`), an inputs folder (can be empty for the first demo).

## 1. Export the model (about 20 to 30 seconds)
Type: *Export the Navisworks model of this project so that we can check drawings against it. Tell me how many pipe lines it holds.*
- An **approval window** appears (the export writes a snapshot to the shared folder). Click **Approve**.
- Expect: the model file is opened hidden on a copy, nothing is saved or changed in Navisworks. On the sample model: 303 pipe lines, 284 nozzles, 252 equipment items, 67 pipes that occur more than once in the model tree.
- Say: the team exports once per model revision; everyone checks against the shared snapshot, no Navisworks needed for the checks.

## 2. Check an isometric
Type: *Check the isometric `<path>.pdf` against the model. Verdict first, every FAIL and REVIEW with both values, then what you could not check.*
- Expect (sample isometric, no PMS / line list / P&ID in the inputs): **REVIEW**, 0 FAIL, 3 REVIEW, 29 OK, 6 not verified. The REVIEWs are real: the model's test pressure is 300 psig on the branch but 10 psig on the pipe, the operating pressure is blank in the model, and the supports cannot be matched until the client's support map is added.
- Say: a FAIL always shows the drawing value, the source value and where the source is. "Not verified" is never a pass. Without the project's PMS, line list and P&IDs those comparisons are listed as not done.

## 3. Catch a wrong drawing
Check a copy with a changed piping class, test pressure, coordinate, tee or pipe quantity (the five prepared copies).
- Expect: **FAIL** with both values, e.g. class F400A on the drawing against B400A in the model and in the line number.

## 4. Show the rest (optional)
- Add a PMS, a line list and the P&IDs to the inputs folder and run the check again: more OK findings, and class, size, pressure, temperature and P&ID number are compared with the sources.
- Correct the assistant ("that is wrong because ...", with the source): it records a lesson. The QC lead approves true lessons on the Knowledge page ("Lessons to review"); from then on every PC applies them.
- Knowledge > Team activity (lead only): who used it, how often it was corrected.

## What not to promise
- It was tested on one real isometric, one real model and one real piping plan; the P&ID check has only been tested on drawings made for testing. Say so.
- It checks text and numbers. Symbols, routing and clearances are always a person's job; every result says so.
- The measure of success is agreement with the QC team's own findings on a batch of real drawings. That has not been measured yet: it is the purpose of the pilot.
