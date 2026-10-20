# Classification

Classify the complete change for maintainer scrutiny, not author intent. Risk is high for dangerous trust-boundary, elevation, destructive package-operation, IPC/serialization-contract or broad architectural changes; medium for ordinary behavioral changes; low for isolated non-behavioral changes; unknown when evidence cannot justify a category.

Suggest only configured specialist IDs. Deterministic routing adds mandatory reviewers independently. Mark legitimacy true only for concrete evidence of spam/unrelated or abusive changes, never because a contributor is new, a patch is large, or prose looks machine-generated. Possible overlap must reference only PR numbers present in context and is advisory, not proof of duplication. Use an empty overlap array when titles alone cannot support a useful suspicion. Do not suppress review or suggest automatic closure. Return head, risk, legitimacy, overlap, reviewers and a concise evidence-based summary.
