---
name: self-review-change
description: Adversarially review a change you just made - your own diff, or one handed to you - the way a demanding PR reviewer would, and prove it works instead of asserting it. Use whenever the user asks you to review your own code, double-check or be sure a fix really works, look for regressions or side effects, sanity-check a change before committing, or "review this like a PR". Also run it unprompted before handing back any non-trivial fix. Covers re-testing the claim instead of re-reading your reasoning, building a behaviour-preservation ledger against the code you deleted, verifying against the reporter's environment rather than your own, and reporting blocking vs non-blocking findings honestly.
---

# Self-review a change

You already believe the change is correct. That belief is the thing under review, not the evidence for it.

Re-reading your own diff and nodding is not a review - you will reconstruct the same reasoning that produced the bug. A review means attacking specific falsifiable claims with experiments. Prefer one command that could have proved you wrong over three paragraphs explaining why you are right.

Work through the sections below and keep a running list of findings. Not every section applies to every change; skip the ones that genuinely do not, and say which in the report.

## 1. Restate the claims, then attack them

Write down, explicitly, what the change asserts:

- "The old code produced X" - the mechanism you diagnosed.
- "The new code produces Y" - for every input, not just the reported one.
- "Nothing else depended on X."

Each of those is falsifiable. Go falsify them. The one that usually breaks is the third.

Watch for claims you smuggled into your own handoff report as supporting detail. Those get the least scrutiny and are the most likely to be wrong - you measured them once, on your machine, and never checked whether they generalise. Re-verify the incidental "and this was broken too" observations before you repeat them.

## 2. Test the exact artifact, not an approximation

If the code builds a command line, a URL, a query, or a serialized payload, reconstruct that string **verbatim as the code emits it** and exercise it. Do not retype an equivalent-looking one into your shell.

A shell test adds or removes a quoting layer relative to production. It can pass while the real call fails, and vice versa. Drive the real API instead: build the actual `ProcessStartInfo` and call `Process.Start`, hit the actual URL, feed the actual bytes. A short throwaway script in the scratchpad that mirrors the production call is worth more than any amount of reading.

## 3. Build a behaviour-preservation ledger

A fix is a swap: an old path out, a new path in. "The bug is gone" says nothing about the other inputs the old path handled.

List every input class the deleted code coped with, then check the new code on each:

- empty input, and a single row
- preamble the old parser skipped: headers, separators, banners, progress noise
- malformed or short rows the old code dropped
- rows with a missing or empty field, in the middle and at the end
- values at the extremes: very long, zero-length, unusual characters, embedded separators
- non-English or non-default locale, if the old code keyed on displayed text
- encoding: is the output really UTF-8, or the console code page?

Each line of deleted code was doing something. If you cannot say what a deleted line handled and why it is now unnecessary, you have not finished reviewing.

Where behaviour genuinely changed for a non-reported input, decide deliberately: is the new behaviour better, equal, or a silent regression? Write the answer down - "worse than before for input Z" is a blocking finding even when the reported bug is fixed.

## 4. Find the ceiling you just moved

If the fix raises a limit rather than removing one - a width, a buffer, a timeout, a page size - you have not eliminated the failure, you have relocated it. Find the new ceiling and prove realistic input stays well under it.

Better: prefer the shape that has no ceiling at all. Then prove *that*, with an input far past where the old limit sat.

## 5. Chase the source of the data, not its rendering

Switching from "parse what was displayed" to "read the underlying value" is usually right, but it silently drops anything the renderer was computing. Before you trust it, open the format definition, the view, the template, the serializer - and confirm the display was a plain projection of the field you now read, not a calculated one.

If the renderer combined fields, your cleaner read is lossy.

## 6. Verify against the reporter's environment, not yours

Your machine has one version of the dependency. The reporter has another, and the numbers that drive the bug - column widths, defaults, schema, flags - move between versions.

Do not generalise from your box. Fetch the actual artifact for their version and read it: download the package, pull the tagged file, check the changelog. This is cheap and it routinely changes the diagnosis, or at least the accuracy of what you claim in the report.

## 7. Prove the mechanism is the only one

You found *a* cause that reproduces the symptom. Grep for other code that could produce the same symptom. If a second producer exists, your fix is partial and the issue will be reopened.

Search for the symptom's signature - the literal marker, the error code, the sentinel value - across the whole repo, and classify each hit as producer or defensive consumer.

## 8. Follow every caller and every fixture

Changing a data format or a signature breaks things that are not in your diff yet.

- Grep for **all** callers of every function you touched, including tests.
- Update fixtures in files you did not think were involved; a passing filtered test run hides a fixture you missed elsewhere.
- If a fixture had to change, ask whether it was encoding a real contract. A fixture you edited to make green is a regression you laundered.

## 9. Map the downstream blast radius

Trace where the previously-wrong value flowed. Often the fix repairs more than was reported - update checks, detail lookups, path resolution - and finding those confirms the diagnosis was the root cause rather than a symptom near it. Say so in the report; it is evidence, not padding.

Conversely, if the wrong value was being compensated for downstream, your fix may double-correct. Check for existing workarounds keyed to the broken value and flag any that are now dead or actively harmful.

## 10. Check the adjacent duplicates

Near-duplicate implementations drift. Check the siblings for the same defect - and check whether one of them is the already-fixed version you should have copied.

Report what you find there. Do not widen the diff to fix it; that is the user's call.

## 11. Run the real gates

- The **full** test suite for the affected project, not the filter you used while iterating.
- Warning count against a baseline: stash the change, build, count, unstash. Only then can you honestly say the remaining warnings are pre-existing.
- Every formatter and linter gate CI runs, checking exit codes explicitly rather than skimming output.
- Confirm the changed files still have the line endings and encoding they had before you rewrote them.

## Report

Lead with the verdict, then the findings a reviewer would leave, most serious first:

- **Blocking** - would break something; must change before commit.
- **Non-blocking** - worth knowing, defensible either way.
- **Nits** - style, naming, comment placement.

Then:

- **Verified by experiment** - what you actually ran and what it showed. Name the check that could have failed.
- **Could not verify** - the environment, version, or device you do not have. Be specific; "untested on the reporter's dependency version" is useful, "probably fine" is not.
- **Corrections** - anything in your earlier report this review showed was wrong or overstated. State it plainly and move on.

If the review found nothing, say the change survived, and list what you tried to break it with. A review with no findings and no experiments is not a review.
