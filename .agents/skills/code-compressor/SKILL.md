---
name: code-compressor
description: Aggressively review changed code for unnecessary complexity and propose smaller, clearer, behavior-preserving alternatives. Use for code reviews, refactor assessments, or any diff where simplification, deletion, reuse, flatter control flow, or fewer abstractions may improve the implementation.
---

# Code compressor

Treat every added branch, state, abstraction, helper, conversion, and dependency as complexity that must earn its place.
Look for duplicated logic, speculative generality, needless indirection, over-modeled state, tangled control flow, verbose data transformations, and code that existing APIs can replace.

Be aggressive in searching, but evidence-driven in reporting.
Recommend a simplification only when you can describe a concrete, smaller alternative and explain why it preserves required behavior.
Account for error handling, ownership, lifetimes, performance, public API stability, and repository conventions.
Do not trade explicit correctness or necessary protocol detail for fewer lines.

Report each opportunity with its location, the complexity it removes, the proposed shape, and any meaningful tradeoff.
Separate optional compression opportunities from correctness defects.
Omit formatting, naming preferences, and vague rewrite requests.
If the code is already close to the simplest correct form, return no findings.
