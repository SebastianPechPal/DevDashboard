# DevDashboard

A terminal dashboard that tracks Azure DevOps PR-review turnaround and bug/defect
metrics for a fixed set of Palfinger repositories. This file fixes the language the
dashboard uses; it is a glossary, not a spec.

## Language

### Repositories & navigation

**Tracked repo**:
An Azure DevOps repository the dashboard monitors. The set is fixed in configuration.
_Avoid_: project (a tracked repo belongs to an ADO **project**, which is a different thing).

**Local repo path**:
The local working-copy directory that corresponds to a **tracked repo**, mapped
explicitly per repo. Not every tracked repo has one.
_Avoid_: checkout, clone dir.

**Focused panel**:
The dashboard panel currently receiving keyboard navigation — either Open PRs or Bugs.
Exactly one is focused at a time.
_Avoid_: active panel, selected panel.

**Background agent**:
A separate Claude Code session, launched from the dashboard, that focuses on one
selected pull request and runs in that PR's **local repo path** while the dashboard
keeps running.
_Avoid_: subagent, worker, job.

### Defect metrics

**Found System**:
The stage at which a bug was discovered, per the Bug-Report-Guidelines wiki:
**Production** (found by customers in the released version), **QA** (found in a
pre-release / release candidate), **Test** (found during test activities, any build),
**Dev** (found in nightly/development builds). Independent of a bug's **State**.
_Avoid_: environment, found-in, stage.

**DLR90 Production**:
The trailing-90-day defect leakage rate to **Production** — the share of bugs created
in the window whose **Found System** is Production, i.e. reached customers.
_Avoid_: leakage, escape rate (use the full term).

**State**:
A bug's workflow status on the board (New, To Do, Testing, Approved, Blocked, Done,
Removed). Distinct from **Found System** — a bug found in Dev can be in State "Testing".
_Avoid_: status (in prose), stage.

## Example dialogue

**Dev:** "12345 shows orange — is that a production bug?"
**Expert:** "No. Orange means its **Found System** is QA or Test — caught pre-release or
during testing. Red is **Production**, which means a customer found it. 12345's **Found
System** is QA; the 'Testing' you saw is its **State**, a different field."
**Dev:** "If I open a **background agent** on a PR, where does it run?"
**Expert:** "In that PR's **tracked repo**'s **local repo path**. If we never mapped one
for that repo, it refuses and warns — better than running in the wrong directory."
