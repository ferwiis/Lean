## 1. Repository state

- Repository: `C:\Code_Projects\Lean`
- Branch: `feature/binary-data-pipeline`
- HEAD: `a5b7a46da`
- Local `upstream/master`: `6eb389012`
- Divergence: 422 upstream-only commits, 1 branch-only commit
- No merge, rebase, commit, staging, or push performed.
- Auroboros was inspected read-only to verify the producer ABI.
- Initial working tree contained only the pre-existing untracked `CODEX_BINARY_PIPELINE_MIGRATION.md`.
- No historical data files were modified.

The initial full build was blocked by transient access-denied locks in `Logging\bin` and SourceLink intermediate files. Later full Release builds succeeded without source-level remediation.

## 2. Architecture implemented

```text
BinaryDataFeed
  → FileSystemDataFeed lifecycle
  → BinarySubscriptionEnumeratorFactory
  → BinaryFileResolver
  → BinaryTradeBarSubscriptionReader / BinaryQuoteBarSubscriptionReader
  → UTC-to-exchange-time conversion
  → bounded streaming consolidation
  → strict daily end times / corporate events
  → fill-forward / filtering / LastPointTracker
  → schedules / Subscription
```

```text
BinaryHistoryProvider
  → SubscriptionDataReaderHistoryProvider lifecycle
  → BinarySubscriptionEnumeratorFactory
  → Binary readers and normalization
  → native strict-end-time / corporate-event / fill-forward / filter ordering
  → parallel or synchronous Subscription
  → synchronized Slices
```

Key behavior:

- Request type determines TradeBar versus QuoteBar; `bar-type`, `timeframe`, and `BarDataMode` are gone from the runtime path.
- Exact native period wins. Otherwise the smallest finer period that evenly divides the request is consolidated upward.
- A request finer than all available data receives the smallest native period honestly—no synthetic finer-resolution bars.
- Incompatible non-divisible periods fail explicitly.
- Files are ordered deterministically; overlapping timestamps use first-file-wins streaming deduplication.
- Readers use 64-bit offsets/counts, lazy read-only mappings, and one bar per `MoveNext()`.
- Universe subscriptions continue through native `FileSystemDataFeed` handling.
- Actual LEAN `Symbol` identity is preserved.

Producer inspection confirmed:

- TradeBar native ABI: 32 bytes due trailing native alignment.
- QuoteBar native ABI: 64 bytes.
- Timestamps: UTC Unix seconds.
- QuoteBar contains absolute OHLCV plus bid/ask fields; `Value` uses the absolute close.

## 3. Files changed

- [BinaryDataFeed.cs](/C:/Code_Projects/Lean/Engine/DataFeeds/BinaryDataFeed.cs)  
  Replaced the duplicated/custom feed implementation with a `FileSystemDataFeed` subclass overriding only raw market-data creation.

- [BinaryFileResolver.cs](/C:/Code_Projects/Lean/Engine/DataFeeds/BinaryFileResolver.cs)  
  Added strict filename validation, injectable roots, deterministic sorting, half-open intersection, and period-aware homogeneous file selection.

- [BinaryTradeBarSubscriptionReader.cs](/C:/Code_Projects/Lean/Engine/DataFeeds/BinaryTradeBarSubscriptionReader.cs)  
  Hardened as a lazy, forward-only 32-byte ABI reader with streaming dedupe, chronology checks, 64-bit offsets, empty-file traversal, and deterministic disposal.

- [BinaryQuoteBarSubscriptionReader.cs](/C:/Code_Projects/Lean/Engine/DataFeeds/BinaryQuoteBarSubscriptionReader.cs)  
  Implemented the verified 64-byte QuoteBar ABI, absolute close semantics, symbol preservation, validation, dedupe, and disposal.

- [FileSystemDataFeed.cs](/C:/Code_Projects/Lean/Engine/DataFeeds/FileSystemDataFeed.cs)  
  Added a small protected raw-enumerator seam. All existing lifecycle, warmup, universe, filter, fill-forward, schedule, and `LastPointTracker` behavior remains in the base class.

- [BinaryHistoryProvider.cs](/C:/Code_Projects/Lean/Engine/HistoricalData/BinaryHistoryProvider.cs)  
  Now subclasses the native history provider and replaces only its raw reader.

- [SubscriptionDataReaderHistoryProvider.cs](/C:/Code_Projects/Lean/Engine/HistoricalData/SubscriptionDataReaderHistoryProvider.cs)  
  Extracted a protected raw-reader factory method while preserving native wrapper order and behavior.

## 4. Files added

- [BinarySubscriptionEnumeratorFactory.cs](/C:/Code_Projects/Lean/Engine/DataFeeds/Enumerators/Factories/BinarySubscriptionEnumeratorFactory.cs)  
  Shared feed/history factory with timezone normalization and bounded single-bucket consolidation.

- [BinaryFileResolverTests.cs](/C:/Code_Projects/Lean/Tests/Engine/DataFeeds/BinaryFileResolverTests.cs)  
  Filename, period policy, intersection, overlap, ordering, and no-match coverage.

- [BinarySubscriptionEnumeratorFactoryTests.cs](/C:/Code_Projects/Lean/Tests/Engine/DataFeeds/BinarySubscriptionEnumeratorFactoryTests.cs)  
  Generated TradeBar/QuoteBar ABI tests, exact boundaries, multi-file traversal, empty files, dedupe, chronology errors, consolidation, timezone conversion, unsupported requests, and early disposal.

- [BinaryHistoryProviderTests.cs](/C:/Code_Projects/Lean/Tests/Engine/HistoricalData/BinaryHistoryProviderTests.cs)  
  Quote history, TradeBar consolidation, fill-forward, timezone, range filtering, and parallel/non-parallel history coverage.

- [BinaryRealDataTests.cs](/C:/Code_Projects/Lean/Tests/Engine/DataFeeds/BinaryRealDataTests.cs)  
  Conditional integration/performance validation against local EURNZD binary data.

## 5. Files removed

- `Engine/DataFeeds/BinaryDataLoader.cs`

`BinaryDataLoader` was deleted after all runtime consumers were migrated and characterization tests passed. It was not retained or renamed because no legitimate consumer remained and retaining it would leave an easy route back to whole-history materialization. `BarDataMode` was removed with it.

## 6. Builds

- `dotnet build QuantConnect.Lean.sln --no-restore -c Release -v:q`  
  Passed: 0 errors, 3,204 existing warnings.

- `dotnet build Engine\QuantConnect.Lean.Engine.csproj --no-restore -c Release -v:minimal`  
  Passed: 0 errors, 3,550 existing warnings.

- `dotnet build Engine\QuantConnect.Lean.Engine.csproj --no-restore --no-dependencies -c Debug -p:GenerateDocumentationFile=false`  
  Passed: 0 errors.

- `dotnet build Tests\QuantConnect.Tests.csproj --no-restore --no-dependencies -c Debug -p:GenerateDocumentationFile=false`  
  Passed: 0 errors.

- Temporary validation algorithm and Launcher builds also passed. Temporary source/configuration files were removed afterward.

Warnings include existing analyzer findings and the existing `DotNetZip` NU1903 advisory.

## 7. Tests

- Generated binary resolver/reader/history suite: **18/18 passed**.
- Binary-wide filter: **31/31 passed**.
- Real EURNZD integration test: **1/1 passed**.
- Native `SubscriptionDataReaderHistoryProviderTests`: **2/2 passed**.
- Native warmup-to-normal fill-forward regression: **1/1 passed**.
- Native map-file-first-date regression: **1/1 passed**.

The complete `FileSystemDataFeedTests` fixture reached six passing cases, but its host was aborted by an existing Python.NET finalizer failure:

```text
InvalidOperationException: GIL must always be released...
```

The option/future-chain subset encountered the same host failure before reporting test results. This was also intermittently logged during successful suites, indicating a local test-harness issue rather than a binary-pipeline assertion failure.

## 8. Runtime/backtest validation

Small real-data backtest, 2010-04-19 through 2010-04-20:

```text
history=12
warmup=1
normal=577
unique transition points=578
exit code=0
```

Long real-data backtest, 2009-11-02 through 2011-09-15:

```text
history=12
warmup=1
normal=148928
algorithm runtime=2.17 seconds
exit code=0
```

Two concurrent long-range LEAN processes reading the same files both completed with identical bar counts and exit code 0.

Only real QuoteBar `.bin` files were available. TradeBar runtime behavior was validated against generated records conforming to the inspected producer ABI.

## 9. Memory/performance

Direct real-data reader test:

```text
files=4
bars=139981
elapsed=0.626 seconds
first=2009-10-26T00:00:00
last=2011-09-16T20:10:00
max observed live-managed delta=0 bytes
max observed working-set delta=27,607,040 bytes
total allocations=678,910,552 bytes
GC collections=10/3/2
```

The allocation count reflects expected transient bar/test objects; sampled live managed memory did not grow beyond the initialized test-host baseline.

Long full-engine run:

```text
managed delta=87,825,072 bytes
working-set delta=78,057,472 bytes
```

Concurrent runs:

```text
process 1: managed=105,740,320; working set=136,114,176
process 2: managed=44,579,592; working set=132,288,512
```

These full-engine values include LEAN, result handling, logging, and runtime infrastructure. No apples-to-apples legacy-loader benchmark, Private Bytes, or page-fault comparison was performed. Structurally, the runtime now retains only the current record/consolidation bucket rather than the complete history.

## 10. Remaining risks

- The branch is 422 commits behind the local upstream reference, so a future synchronization may conflict with the two native extension seams.
- No real TradeBar `.bin` file was locally available.
- The Auroboros Python `QuoteBarRow` binding omits the C header’s explicit `spread` field, although native alignment still produces 64-byte records and the reader does not consume spread. The producer bindings should eventually be reconciled.
- Deep-in-file requests currently scan from record zero; a timestamp index remains a possible future optimization.
- First-file-wins overlap handling cannot recover an older missing timestamp from a later overlapping file without introducing merge lookahead.
- Finer-than-native requests intentionally expose the coarser native period.

## 11. Final Git state

`git status --short`:

```text
 M Engine/DataFeeds/BinaryDataFeed.cs
 D Engine/DataFeeds/BinaryDataLoader.cs
 M Engine/DataFeeds/BinaryFileResolver.cs
 M Engine/DataFeeds/BinaryQuoteBarSubscriptionReader.cs
 M Engine/DataFeeds/BinaryTradeBarSubscriptionReader.cs
 M Engine/DataFeeds/FileSystemDataFeed.cs
 M Engine/HistoricalData/BinaryHistoryProvider.cs
 M Engine/HistoricalData/SubscriptionDataReaderHistoryProvider.cs
?? CODEX_BINARY_PIPELINE_MIGRATION.md
?? Engine/DataFeeds/Enumerators/Factories/BinarySubscriptionEnumeratorFactory.cs
?? Tests/Engine/DataFeeds/BinaryFileResolverTests.cs
?? Tests/Engine/DataFeeds/BinaryRealDataTests.cs
?? Tests/Engine/DataFeeds/BinarySubscriptionEnumeratorFactoryTests.cs
?? Tests/Engine/HistoricalData/BinaryHistoryProviderTests.cs
```

`CODEX_BINARY_PIPELINE_MIGRATION.md` is the pre-existing user file. All other listed changes are from this migration.

`git diff --stat`:

```text
 Engine/DataFeeds/BinaryDataFeed.cs                 | 307 +++---------
 Engine/DataFeeds/BinaryDataLoader.cs               | 551 ---------------------
 Engine/DataFeeds/BinaryFileResolver.cs             | 378 +++++++-------
 .../DataFeeds/BinaryQuoteBarSubscriptionReader.cs  | 507 ++++++++-----------
 .../DataFeeds/BinaryTradeBarSubscriptionReader.cs  | 477 ++++++++----------
 Engine/DataFeeds/FileSystemDataFeed.cs             |  14 +-
 Engine/HistoricalData/BinaryHistoryProvider.cs     | 388 ++-------------
 .../SubscriptionDataReaderHistoryProvider.cs       |  49 +-
 8 files changed, 711 insertions(+), 1960 deletions(-)
```

That stat excludes the five untracked added files, totaling another 1,241 lines.

`git diff --check` exited 0 with no whitespace errors. Git emitted informational LF-to-CRLF normalization warnings.

## 12. Inspecting the result

```powershell
Set-Location C:\Code_Projects\Lean

git status --short
git diff
git diff -- Engine/DataFeeds
git diff -- Engine/HistoricalData
git diff -- Engine/DataFeeds/BinaryDataLoader.cs
```

Because new files remain untracked, inspect them as diffs with:

```powershell
$newFiles = @(
    "Engine/DataFeeds/Enumerators/Factories/BinarySubscriptionEnumeratorFactory.cs",
    "Tests/Engine/DataFeeds/BinaryFileResolverTests.cs",
    "Tests/Engine/DataFeeds/BinaryRealDataTests.cs",
    "Tests/Engine/DataFeeds/BinarySubscriptionEnumeratorFactoryTests.cs",
    "Tests/Engine/HistoricalData/BinaryHistoryProviderTests.cs"
)

foreach ($file in $newFiles) {
    git diff --no-index -- NUL $file
}
```

`git diff --no-index` returning exit code 1 is expected when it displays a new file.

All changes remain uncommitted and unpushed.