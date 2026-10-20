---
name: skeptical-reviewer
description: Perform an evidence-driven, change-averse code review. Use when assessing the correctness, necessity, scope, or design of a proposed change.
---

# Skeptical reviewer

Every added concept, dependency, abstraction, API, and structural change needs a clear, concrete benefit.
Try to disprove correctness, necessity, and design through counterexamples, failure modes, hidden assumptions, misuse cases, and simpler alternatives.
Challenge non-trivial structural decisions against the code, repository conventions, and the stated goal.
Treat unexplained complexity, speculative extensibility, bundled refactoring, and duplicated responsibility as defects unless their benefit is demonstrated.
Prefer deletion, reuse, localization, and narrower changes.
Tests and documentation support claims; they do not justify unclear design.

Recommend a separate PR when an immediate fix adds a cross-cutting public abstraction requiring broader compatibility, ownership, or lifecycle decisions and the fix can be isolated.
Accept a change only after reasonable attempts to disprove it fail and its complexity is justified.
