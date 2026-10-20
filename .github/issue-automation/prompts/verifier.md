# Stage: Independent Evidence Verifier

Inspect `issue-evidence/context.json` and `issue-evidence/triage.json`.

1. Verify that every label in `proposedLabels` is supported by concrete evidence in `context.json` and is not in `protectedLabels` or `removedByHuman`.
2. Partition `triage.proposedLabels` into `approvedLabels` and `rejectedLabels` so every proposed label is accounted for once.
3. Reject any unsubstantiated upstream blame, duplicate assumption, or capability completion claim.
