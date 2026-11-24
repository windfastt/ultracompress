# Pipeline advisor — rules v1

Content-aware **codec profile** and **preprocess** suggestions from `FileFingerprint` + user profile. Feeds the worker CLI today; tournament search (`feat/tournament`) can consume the same `AdvisorPlan`.

## API

- `PipelineAdvisor.Plan(fingerprint, profile)` → `AdvisorPlan`
- `PipelineAdvisor.SuggestPrimaryRecipe(...)` → single `PipelineRecipe` for execution

## Rules (v1)

| Rule ID | Condition | Effect |
|---------|-----------|--------|
| `entropy_gate` | Shannon ≥ 7.9 bits/byte | Fast `zstd` level 3, no tournament |
| `tiny_file_fast` | Size &lt; 4 KiB | `zstd` level 3 |
| `entropy_skip_reencode` | Shannon ≥ 7.5 | Skip type-specific re-encode preprocessors |
| `extension_*` | Path extension or MIME | Preprocess arm (PDF, SQLite, PE, PNG, log/CSV, text flate) |
| `profile_default` | User profile | Base codec level from `ProfileDefaults`; dict cap on large files |
| `small_tournament_arm` | Small/Tiny/Lab/Experimental | Alternate codec hints + candidate recipes |

## Profiles

| Profile | Primary | Tournament candidates |
|---------|---------|------------------------|
| Balanced | Advisor primary | 0 |
| Small | Advisor primary | 3 |
| Tiny | Advisor primary | Up to 5 additional pipelines (8 cap in alternates list) |

Non-`zstd` candidates are suggestions only until backends register in the worker.