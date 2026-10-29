# PmcApp

## Ingredient frequency labeling (menu item 17)

`PmcIngredientFrequencyLabeler.RunAsync()` migrates the local MongoDB schema,
creates four indexes, labels eligible articles, then writes an Excel frequency report.
It updates individual fields with `$set`; existing article data, previous ingredient
labels and unknown fields such as review results are preserved.

An article is eligible when `AbstractText` is a nonblank string and `Sections`
contains at least one nonblank string value. Eligibility uses actual text rather
than `HasFullText`, `HasAbstract`, source or human-study labels. The migration saves
this eligibility snapshot as `IsIngredientFrequencyEligible` for indexed processing.

```bash
/home/breg/.dotnet/dotnet run --project PMC_APP -c Release -- ingredient-frequency \
  --keywords /home/breg/IngredientKeywordSearch_0/IngredientKeywordSearch/keywords.xlsx \
  --output reports/ingredient-frequency \
  --threads 10
```

Defaults: `mongodb://localhost:27017`, database `pmc`, collection `all_articles`,
256 articles per processing batch, 10,000 per migration batch, and approximately
80% of logical processors as workers. Options: `--mongo-uri`, `--database`,
`--collection`, `--batch-size`, `--migration-batch-size`, `--threads`, `--readers`, `--keywords`,
`--output`, `--report-only`, `--resume-labeling`. Worker count controls parallelism, not an OS CPU quota.
On this 12-CPU machine the production job also uses systemd `CPUQuota=960%` to cap
the labeling process at 80% of total CPU capacity. MongoDB runs in its own service.
`/home/breg/.dotnet/dotnet` is the working .NET 8 SDK used for this run; other
machines can use their normal `dotnet` executable.

The production run encountered a host-wide out-of-memory event that killed MongoDB.
MongoDB was restarted with a temporary systemd override at
`/run/systemd/system/mongod.service.d/pmc-ingredient-memory.conf`, adding
`--wiredTigerCacheSizeGB 12` to its existing command. This leaves the on-disk
MongoDB configuration unchanged and limits its cache to 12 GiB until the override
is removed or the machine reboots. Stored completion flags allow recovery after
MongoDB journal recovery; uncommitted articles are processed again.

### New article fields

| Field | Type | Meaning |
|---|---|---|
| `IsIngredientFrequencyLabeled` | bool | All three sets of counts were saved successfully. |
| `IngredientTitleCounts` | array of `{Keyword, Count}` | Positive occurrence counts in the title. |
| `HasIngredientInTitle` | bool | At least one ingredient keyword occurs in the title. |
| `IngredientAbstractCounts` | array of `{Keyword, Count}` | Positive occurrence counts in the abstract. |
| `HasIngredientInAbstract` | bool | At least one ingredient keyword occurs in the abstract. |
| `IngredientSectionCounts` | array of `{Keyword, Count}` | Positive counts summed across section bodies. |
| `HasIngredientInSections` | bool | At least one ingredient keyword occurs in section bodies. |
| `IsIngredientFrequencyEligible` | bool | Eligibility snapshot computed during migration. |
| `IngredientFrequencyVersion` | string or null | Matching policy and SHA-256 of the effective keyword list. |
| `IngredientFrequencyLabeledAt` | UTC date or null | Time the successful result was generated. |

Unprocessed/ineligible records receive false flags, empty count arrays and null
version/timestamp. A completed record with no matches has a true completion flag
and empty arrays. Counts use 64-bit integers. Arrays support keyword names with
MongoDB-special punctuation without using the names as BSON keys.

`IngredientSearch/KeywordMatcher.cs` and `XlsxKeywordReader.cs` were copied unchanged
from `/home/breg/IngredientKeywordSearch_0/IngredientKeywordSearch/`.
The immutable Aho-Corasick matcher is shared between workers; each worker reuses
private count buffers. Matching is invariant case-insensitive with Unicode word
boundaries. Internal whitespace/punctuation is literal; overlapping keyword phrases
are counted independently. The `keyword` column is read from the first Excel sheet;
the input `frequency` column is ignored. Duplicate keywords are counted once.

Title, abstract and each section value are searched separately: phrases cannot cross
field or section boundaries. Section keys/headings are not separately searched;
stored section values, including tables and any duplicate text, are counted as stored.

### Indexes and progress

- `ingredient_frequency_work_v1`: eligibility, completion, keyword version, PMC ID.
- Three partial indexes on title/abstract/section presence plus PMC ID, containing
  only completed frequency labels.

The migration uses the existing unique `PmcId_1` index and updates disjoint ID ranges
in parallel. An atomic `migration-checkpoint.json` records each fully acknowledged
wave, so interrupted migrations resume without rescanning the completed ranges.
Completed full migrations start from the beginning on a subsequent full invocation.
The recovery option `--migration-start-after <PmcId>` can supply a known fully
migrated boundary when recovering a migration that predates checkpoint support.
Every ten seconds the job
prints its phase, processed/total counts, percentage and elapsed time, and atomically
updates `progress.json` in the output directory. Schema migration, index building,
labeling and report aggregation have separate progress phases. Index-building
progress is an indeterminate phase until MongoDB finishes building the indexes.

The flag, counts, presence fields, version and timestamp are written together in
one atomic article update. Restarting the same command preserves committed labels
and resumes pending ones. Changing keywords causes eligible articles to be relabeled.
Migration recomputes eligibility on every full invocation. `--report-only` rebuilds
the report from stored labels without migration or labeling and requires all
currently eligible articles to be labeled with the requested keyword version.
`--resume-labeling` skips the already completed schema/index preparation and resumes
labeling plus report generation; it requires the matching completed migration
checkpoint and existing work index. Use it when continuing the same dataset snapshot.
The labeler uses a bounded article queue with up to four MongoDB readers (configurable
with `--readers`) over disjoint PMC ID ranges, and the requested number of workers.
Each worker searches articles and asynchronously saves its own
small batch, overlapping CPU work with database I/O without loading the corpus.
Only run one writer for a collection at a time; the output directory has a process
lock. If article text changes after labeling, invalidate that article's
`IsIngredientFrequencyLabeled` flag before rerunning, so its counts are recomputed.

### Output

`ingredient_keyword_frequencies.xlsx` contains five worksheets: Summary,
Keyword frequencies, Title, Abstract, and Full text sections. Each keyword has
occurrence counts, article counts and article percentages for each field, total
occurrences, and the number of distinct articles with a match in any field.
Zero-match keywords are included. Percentages use all eligible labeled articles
as the denominator. A JSON companion stores the same aggregates and provenance.
Reports aggregate persisted labels, including work completed by earlier resumed runs.
Report generation also reads disjoint PMC ID ranges in parallel and merges local
keyword aggregates, preserving distinct article counts without double-counting.

### Verification

```bash
/home/breg/.dotnet/dotnet run --project tests/PmcIngredientFrequency.Tests -c Release
```

The executable test harness checks whole-word/Unicode/overlap matching, thread
safety, field/section boundaries, old DTO deserialization, actual-text eligibility,
schema migration, indexes, preservation of existing fields, interruption/resumption
and report totals. A small-batch stress case also verifies queue backpressure,
partial final write batches, and cancellation/error propagation from a failed worker.
It uses a randomly named temporary local MongoDB database and
removes that database afterward.
