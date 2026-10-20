# Stage: Issue Triage & Routing

Inspect `issue-evidence/context.json` and produce a schema-valid JSON triage output:

1. Echo exact `issueNumber` and `fingerprint` from `context.json`.
2. Propose only additive routing labels from the allowed type, manager, `platform/*`, and `area/*` sets, excluding any label listed in `removedByHuman` or `protectedLabels`.
3. Provide `evidenceCitations` referencing real quotes from the issue `title`, `body`, or a `comment` with a valid `commentId` from `context.json`.
4. When referencing `relatedCandidates`, include both `similarityBasis` and `importantDifference` (such as version era, backend, or package ID).
5. When referencing `capabilityMatches`, cite only `availableCapabilities` from `context.json` and state what `unmetScope` remains.
6. Keep `missingDiagnostics` task-specific: do not request package-manager logs for UI/accessibility issues, feature requests, or pre-log startup crashes.
