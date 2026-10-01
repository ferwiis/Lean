# SDD2 Codex Chat Action Results

Execution date: 2026-09-27 (America/Bogota)

Repository: `C:\Code_Projects\Lean`

Branch: `feature/binary-data-pipeline`

Authoritative specification: `SDD2-CODEX_BINARY_PIPELINE_HARDENING.md`

## Executive result

Problems 1 through 3 are implemented. The binary backend now separates physical row type from logical LEAN type, resolves legacy and explicitly tagged files deterministically, implements the validated 32-byte/64-byte ABIs, uses native QuoteBar semantics, selects exact or largest-smaller-divisor native periods, streams non-overlap files sequentially, merges overlap groups with bounded state, detects logical duplicate conflicts, and shares the same raw semantics between History and DataFeed.

Focused SDD2 tests, selected native lifecycle regressions, the complete solution build, real EURNZD characterization, and an end-to-end Launcher run passed. A full native fixture sweep remains blocked by the pre-existing/intermittent Python.NET GIL finalizer failure described below; no SDD2 assertion failure is hidden behind that environment issue.

Problem 4 was analyzed only. No seek/index/benchmark/multiprocessing/shared-memory/IPC implementation was added.

No commit, stage, push, merge, rebase, reset, or history rewrite was performed.

## 1. Repository state

### Execution-time Git references

- Branch: `auroboros`
- Starting HEAD: `18fdeeae4b08b9d8012a8854d13dd84319f04187`
- Ending working-tree HEAD: `18fdeeae4b08b9d8012a8854d13dd84319f04187` (unchanged)
- Immediate parent / pre-SDD1 checkpoint: `5630bfa0cbc915b209e0fb33cdbd5185ecb3096c`
- Local `master`: `6eb389012d73c364547d61546ff822fc8432dee2`
- Local `fork/master`: `6eb389012d73c364547d61546ff822fc8432dee2`
- Fork master verified with `git ls-remote` at execution time: `6eb389012d73c364547d61546ff822fc8432dee2`
- Official QuantConnect LEAN master verified with `git ls-remote` at execution time: `b1337938bacbdcdf7327ba4c6a10ffa89ffac403`
- Official commit inspected: `Fix market close for Cboe indices (#9830)` (2026-09-25)
- Local `upstream/master` was stale at `6eb389012...`; it was not fetched or moved because upstream synchronization was explicitly out of scope.

Configured remotes:

```text
fork     https://github.com/ferwiis/Lean.git (fetch/push)
upstream https://github.com/QuantConnect/Lean.git (fetch/push)
```

Branch divergence:

- `git rev-list --left-right --count fork/master...HEAD` returned `0 2`: `auroboros` is two Auroboros commits ahead of fork master.
- The official master comparison inspected during the audit showed official master 24 commits ahead of fork master and 0 behind it. Consequently the working branch has two Auroboros-only commits while official master has 24 upstream-only commits relative to their common fork-master base.
- No upstream commit was merged or rebased.

Recent branch history:

```text
18fdeeae4 (HEAD -> auroboros, fork/auroboros) Revisión temporal: Migración del pipeline binario de LEAN a arquitectura streaming
5630bfa0c Improvement: High-Performance Binary Data Pipeline for LEAN Engine
6eb389012 (tag: 18084, fork/master, master; stale local upstream/master) Expose brokerage data to the algorithm and the live results (#9788)
```

### Pre-existing working-tree state

The following state existed before implementation and was preserved unless explicitly noted:

```text
 D "CODEX-MD-Chat-Action results.md"
 D CODEX_BINARY_PIPELINE_MIGRATION.md
 M Engine/DataFeeds/BinaryQuoteBarSubscriptionReader.cs
 M Engine/DataFeeds/FileSystemDataFeed.cs
?? "Auroboros Binary Pipeline - Issues 2 - Solution — Pre-SDD.md"
?? CODEX_BINARY_PIPELINE_ITERATION_02_ISSUES.md
?? "Pre-SDD2-ISSUE 1-PURPOSE.md"
?? "SDD1-CODEX-Chat-Action results.md"
?? SDD1-CODEX_BINARY_PIPELINE_MIGRATION.md
?? SDD2-CODEX_BINARY_PIPELINE_HARDENING.md
```

The local QuoteBar reader already contained the intended `Reserved` correction, although it was incomplete/broken as a compilation unit; the final implementation preserves `Reserved` and does not reintroduce `Spread`. The pre-existing `FileSystemDataFeed.cs` edit was the malformed token `fillForwa.rdResolution`; it was restored to `fillForwardResolution` because it prevented compilation. That file has no final diff from HEAD. The two deleted documents and the unrelated untracked design documents were not restored, edited, renamed, or deleted.

## 2. SDD1 baseline audit

The complete SDD2 and SDD1 specifications were read before editing. `HEAD` was compared to its immediate parent with `git diff HEAD^ HEAD`, and every binary source/test plus the current official native lifecycle seams was inspected.

The SDD1 commit `18fdeeae...` had already:

- removed `BinaryDataLoader` from the runtime path;
- replaced full-history materialization with forward-only MMF readers;
- introduced `BinarySubscriptionEnumeratorFactory` as the shared source seam;
- narrowed `BinaryDataFeed` to a `FileSystemDataFeed` specialization;
- narrowed `BinaryHistoryProvider` to a `SubscriptionDataReaderHistoryProvider` specialization;
- retained LEAN `Symbol` instances;
- used 64-bit offsets/counts, lazy file opening, and deterministic disposal;
- introduced streaming consolidation and timezone conversion;
- added generated and real-data tests.

Those architectural gains were preserved. SDD2 intentionally replaced SDD1's logical-type-driven physical interpretation, smallest-compatible-period selection, first-file-wins overlap behavior, and non-native `QuoteBar.Value` behavior. It also hardened early History disposal.

Current official LEAN source at `b1337938...` was used as the lifecycle/style reference for `FileSystemDataFeed`, `SubscriptionDataReaderHistoryProvider`, `SubscriptionDataReaderSubscriptionEnumeratorFactory`, `SubscriptionUtils`, `FillForwardEnumerator`, `SubscriptionFilterEnumerator`, `StrictDailyEndTimesEnumerator`, `SynchronizingHistoryProvider`, `TradeBar`, and `QuoteBar`. No changes were made to `Common/Data/Market/TradeBar.cs` or `Common/Data/Market/QuoteBar.cs`.

### Producer ABI audit

The actual producer was inspected at:

```text
C:\Code_Projects\Auroboros
branch: work/phase-2
HEAD: 4c9cfc9f1f32221a6d97b63b79a9fab236943cfb
source: Data/data_engineering/data_saving_functions.h
```

The C header explicitly declares `TradeBarRow` as one `double` followed by five `float` fields: 28 explicit bytes with native alignment making `sizeof(TradeBarRow) == 32`; it declares no trailing field. The final four bytes are therefore modeled only as ABI padding. The same header explicitly declares `uint32_t reserved` as the final `QuoteBarRow` field. The Python ctypes producer contract was also checked and agreed with these layouts.

## 3. Final architecture

DataFeed path:

```text
BinaryDataFeed (narrow FileSystemDataFeed specialization)
  -> BinarySubscriptionEnumeratorFactory
  -> BinaryFileResolver.ResolveRequest
  -> per-file PhysicalRecordType resolution/preflight
  -> BinaryPhysicalFileCursor
  -> physical-to-logical projection
  -> sequential K=1 path OR bounded K-way overlap merge
  -> UTC-to-LEAN exchange-time normalization
  -> optional single-bucket streaming consolidation
  -> existing StrictDaily/corporate-event wrappers
  -> native FileSystemDataFeed warmup, LastPointTracker, filters,
     fill-forward, schedules, Subscription, Synchronizer, and Slice
```

History path:

```text
BinaryHistoryProvider (narrow SubscriptionDataReaderHistoryProvider specialization)
  -> the same BinarySubscriptionEnumeratorFactory / resolver / cursor /
     projection / sequential-or-merge raw semantics
  -> native History strict-daily, corporate-event, fill-forward,
     filtering, scheduling, timezone-offset, and Slice synchronization
  -> SynchronizingHistoryProvider try/finally disposal
```

The physical cursor validates and projects one fixed-width record at a time. Metadata/file lists are bounded by the number of files; records are never materialized as a complete history. A non-overlap group has one active mapped file. An overlap group retains one pending record per active file in a priority queue.

## 4. Problem 1 implementation

### Physical contract and filename declarations

- Added `PhysicalRecordType` with `TradeBarRow` and `QuoteBarRow`, independent of requested LEAN type.
- Added explicit-layout `TradeBarRow` (`Size=32`): timestamp at 0, OHLCV at 8..24, no `Reserved` member, four trailing alignment bytes.
- Added explicit-layout `QuoteBarRow` (`Size=64`): global OHLCV, Bid OHLC, Ask OHLC, and `uint Reserved` at offset 60. There is no `Spread` member.
- Added optional case-insensitive `_TB` and `_QB` filename declarations while retaining legacy four-token filenames.
- Explicit declarations are authoritative but validated against file length.
- Empty legacy files remain unresolved and harmless; empty declared files retain their declaration but emit no rows.

### Constant-time schema resolution

Per file, resolution uses only metadata, length divisibility, and—only when both schemas remain legal for a non-empty legacy file—a fixed 128-byte probe. It never scans the complete payload. The probe compares four TradeBar candidates with two QuoteBar candidates, validates timestamp conversion/chronology and finite/plausible candidate fields, ignores `Reserved` as a discriminator, and accepts a schema only when exactly one candidate remains. Too-short, both-valid, neither-valid, and structurally invalid payloads fail explicitly.

Selected candidate files are structurally preflighted before stream creation. Discoverable errors are aggregated and sorted deterministically.

### Projection matrix

```text
TradeBarRow -> TradeBar : supported
TradeBarRow -> QuoteBar : rejected
QuoteBarRow -> TradeBar : supported, using global OHLCV directly
QuoteBarRow -> QuoteBar : supported, using stored Bid/Ask directly
```

`QuoteBarRow -> QuoteBar` invokes vanilla `QuoteBar` construction and therefore preserves `Value == Close`; it does not copy global record close into `Value`, synthesize Bid/Ask, or assign semantics to `Reserved`. The exact LEAN `Symbol` supplied by the request is passed to every logical bar.

### Runtime diagnostics

Record errors include path, record index, 64-bit byte offset, PRT, timestamp, and reason. Runtime checks cover finite/supported timestamps, checked Unix conversion, non-decreasing per-file chronology, fixed-size invariants, and finite numeric fields used by the selected projection. Cast/overflow and projection failures are wrapped with binary context.

## 5. Problem 2 implementation

Resolution is performed against physically resolved, logically compatible period groups:

1. exact requested native period, if available;
2. otherwise the largest native period smaller than the request that exactly divides it;
3. otherwise explicit failure.

Requests finer than the smallest compatible native period and non-divisible requests are rejected instead of silently receiving a coarser/different source. Mixed `TradeBarRow`/`QuoteBarRow` files are valid for a TradeBar request because both project legitimately to TradeBar. A QuoteBar request rejects a participating non-empty TradeBar-only period group. Empty schema declarations do not poison an otherwise valid stream.

Upward aggregation retains the SDD1 single-bucket, forward-only custom consolidator because current LEAN does not expose a smaller raw-source consolidation seam at this location. Trade OHLCV and Quote Bid/Ask OHLC are aggregated directly, `QuoteBar.Value` is reset from vanilla `QuoteBar.Close`, and memory is independent of request history length. Time normalization follows native reader behavior: the UTC physical bar start is converted to LEAN exchange-local time while the period is retained, allowing downstream native timezone/DST machinery to remain authoritative.

Generated tests prove equivalence among 5M, 15M, and native 30M representations. The local real-data set contains only EURNZD 5M files, so real multi-timeframe equivalence characterization is correctly reported as SKIPPED rather than PASS.

## 6. Problem 3 implementation

### Chronology and overlap

- Files are ordered deterministically by `FromUtc`, `ToUtc`, then path.
- Half-open metadata intervals are partitioned into connected overlap groups.
- `K=1` groups use a direct sequential cursor with no priority queue.
- `K>1` groups use `PriorityQueue` ordering by canonical UTC timestamp, deterministic file order, and record index.
- Merge state is `O(K)` and processing inside an overlap group is `O(R log K)`.
- Projection happens before duplicate comparison, including mixed-PRT TradeBar output.
- Equivalent logical duplicates emit once and advance every contributing source.
- Conflicting logical duplicates throw with both paths, indices, offsets, and logical values; filename order never chooses a price.
- Equal records across sequential group/file boundaries are also deduplicated; conflicting boundary records fail.
- Global QuoteBarRow fields and `Reserved` are ignored when comparing QuoteBar logical duplicates because they are not part of that projection.

### Lifecycle preservation

`BinaryDataFeed` changes only supported Forex TradeBar/QuoteBar raw-source creation. Unsupported, non-Forex, auxiliary, and universe subscriptions fall back to the native file-system source. Supported internal Forex market-bar consumers share the binary path. Native warmup, LastPointTracker, fill-forward, filters, exchange hours, strict daily end times, schedules, map/factor handling, universe routing, subscription ownership, synchronization, and Slice creation remain in the existing LEAN implementation.

`BinaryHistoryProvider` changes only `CreateDataReader`. A narrow `try/finally` was added to `SynchronizingHistoryProvider` so disposing an early-terminated History iterator deterministically disposes every subscription. Normal completion and exceptional paths use the same cleanup.

The timezone wrapper now mirrors native TradeBar/QuoteBar readers: convert `Time` from physical UTC to exchange-local time and retain `Period`, instead of separately converting `EndTime` and distorting the period at DST discontinuities. Canonical source merge/dedup remains UTC. Generated tests cover spring-forward and fall-back labels and prove that repeated local labels do not remove distinct physical UTC records.

MMF pointer acquisition, view accessors, mapped files, group cursors, wrapper enumerators, and subscriptions have deterministic disposal on EOF, group transition, cancellation, early enumeration termination, validation failure, and duplicate conflict.

## 7. Files changed

### SDD2 implementation files

Modified:

- `Engine/DataFeeds/BinaryDataFeed.cs`
- `Engine/DataFeeds/BinaryFileResolver.cs`
- `Engine/DataFeeds/BinaryQuoteBarSubscriptionReader.cs`
- `Engine/DataFeeds/BinaryTradeBarSubscriptionReader.cs`
- `Engine/DataFeeds/Enumerators/Factories/BinarySubscriptionEnumeratorFactory.cs`
- `Engine/HistoricalData/SynchronizingHistoryProvider.cs`
- `Tests/Engine/DataFeeds/BinaryFileResolverTests.cs`
- `Tests/Engine/DataFeeds/BinaryRealDataTests.cs`
- `Tests/Engine/DataFeeds/BinarySubscriptionEnumeratorFactoryTests.cs`
- `Tests/Engine/HistoricalData/BinaryHistoryProviderTests.cs`

Added (untracked, deliberately not staged):

- `Engine/DataFeeds/BinaryDataSubscriptionReader.cs`
- `Engine/DataFeeds/BinaryPhysicalSchema.cs`
- `Tests/Engine/DataFeeds/BinaryDataFeedTests.cs`
- `SDD2-CODEX-Chat-Action results.md`

No SDD2 implementation file was removed. New Auroboros-specific source files use no QuantConnect copyright header; existing upstream headers were preserved.

`Engine/DataFeeds/FileSystemDataFeed.cs` was touched only to repair the pre-existing malformed token described above and ends identical to HEAD. Temporary runtime algorithm/config changes and Launcher output artifacts were removed after validation.

### Pre-existing unrelated state left untouched

- Deleted: `CODEX-MD-Chat-Action results.md`
- Deleted: `CODEX_BINARY_PIPELINE_MIGRATION.md`
- Untracked design/spec documents listed in section 1

Historical files under `Data/historical_data` were read only. None was modified, regenerated, renamed, deleted, or committed.

## 8. Build, test, and runtime record

Status vocabulary in this section is literal: PASS, FAIL, BLOCKED, SKIPPED, NOT EXECUTED, and environment/pre-existing failure are not interchangeable.

### SDK and restore/build

1. **BLOCKED (environment):** initial build with the installed system SDK.

   ```powershell
   dotnet build QuantConnect.Lean.sln -c Release
   ```

   Result: `NETSDK1045`; installed SDK `9.0.203` cannot target the repository's `net10.0`. This is not an SDD2 compile result.

2. A temporary official .NET SDK `10.0.401`, temporary CLI home, temporary NuGet configuration, and temporary package directory were used outside the repository. Engine and Tests restore/build completed. Existing `NU1902`/`NU1903`/`NU1904` dependency advisories remained warnings.

3. **PASS:** final complete solution build.

   ```powershell
   $env:PATH="$env:TEMP\dotnet-sdk-10-sdd2;$env:PATH"
   $env:DOTNET_ROOT="$env:TEMP\dotnet-sdk-10-sdd2"
   $env:DOTNET_CLI_HOME="$env:TEMP\dotnet-cli-sdd2"
   $env:NUGET_PACKAGES="$env:TEMP\nuget-packages-sdd2"
   & "$env:TEMP\dotnet-sdk-10-sdd2\dotnet.exe" build QuantConnect.Lean.sln --no-restore -c Release -v:quiet -clp:ErrorsOnly
   ```

   Result: exit 0, 0 errors, 61 warnings, elapsed 1.63 seconds.

### Focused SDD2 tests

4. **PASS:** final focused binary suite.

   ```powershell
   $env:DOTNET_GCServer='0'
   $env:DOTNET_GCConcurrent='0'
   & "$env:TEMP\dotnet-sdk-10-sdd2\dotnet.exe" test Tests\QuantConnect.Tests.csproj --no-restore --no-build -c Release --filter "FullyQualifiedName~BinaryFileResolverTests|FullyQualifiedName~BinarySubscriptionEnumeratorFactoryTests|FullyQualifiedName~BinaryHistoryProviderTests|FullyQualifiedName~BinaryDataFeedTests" --logger "console;verbosity=minimal"
   ```

   Result: exit 0; 36 passed, 0 failed, 0 skipped; 618 ms.

   Coverage includes ABI/field offsets, no TradeBar `Reserved`, QuoteBar `Reserved`/no `Spread`, suffix/legacy resolution, all probe outcomes, deterministic preflight diagnostics, projection matrix, Value semantics, exact/largest-divisor policy, finer/non-divisible rejection, mixed PRT, aggregation equivalence, empty files, sequential boundaries, two/three-file complementary overlap, identical/conflicting duplicates, intra-file duplicates/order, record diagnostics, disposal, timezone/DST, History sync/parallel paths, fill-forward, and DataFeed warmup-to-normal lifecycle.

5. **FAIL (intermediate implementation test, fixed):** an experimental UTC-before-timezone consolidation ordering produced 36 passes and one failure in the warmup integration assertion (`Warmup periods: 19:00:00, 01:00:00`). The ordering was reverted to the native-compatible lifecycle sequence; the isolated failing test then passed and the final complete 36-test suite passed. This intermediate failure was not reclassified as a pass.

### Native LEAN regressions

6. **PASS:** History/map/subscription/Quote fill-forward selection.

   ```powershell
   & "$env:TEMP\dotnet-sdk-10-sdd2\dotnet.exe" test Tests\QuantConnect.Tests.csproj --no-restore --no-build -c Release --filter "FullyQualifiedName~SubscriptionDataReaderHistoryProviderTests|FullyQualifiedName~MapFileResolverTests|FullyQualifiedName~SubscriptionUtilsTests|FullyQualifiedName~QuoteBarFillForwardEnumeratorTests" --logger "console;verbosity=minimal"
   ```

   Result: exit 0; 26 passed, 0 failed. `SubscriptionUtilsTests` deliberately logged its expected worker-enumerator exception while the test suite passed.

7. **PASS:** native FileSystemDataFeed map/warmup-fill-forward cases.

   ```powershell
   & "$env:TEMP\dotnet-sdk-10-sdd2\dotnet.exe" test Tests\QuantConnect.Tests.csproj --no-restore --no-build -c Release --filter "Name=ChecksMapFileFirstDate|Name=DataIsFillForwardedFromWarmupToNormalFeed" --logger "console;verbosity=minimal"
   ```

   Result: exit 0; 2 passed, 0 failed.

8. **PASS:** selected native fill-forward, DST, weekend-Forex, LastPointTracker, and overlap cases.

   ```powershell
   & "$env:TEMP\dotnet-sdk-10-sdd2\dotnet.exe" test Tests\QuantConnect.Tests.csproj --no-restore --no-build -c Release --filter "Name=HandlesDaylightSavingTimeChange|Name=HandlesDaylightSavingTimeChange_InifinteLoop|Name=FillForwardsFromLastTrackedPoint|Name=DoesNotEmitFillForwardBarOverlappingNextAvailableBar|Name=OandaFillsForwardDailyForexOnWeekends" --logger "console;verbosity=minimal"
   ```

   Result: exit 0; 6 passed, 0 failed.

9. **BLOCKED (environment/pre-existing):** attempts to execute/discover the complete `FileSystemDataFeedTests` and broader `FillForwardEnumeratorTests` fixtures aborted in the test host with:

   ```text
   System.InvalidOperationException: GIL must always be released, and it must be released from the same thread that acquired it.
   at Python.Runtime.Py.GILState.Finalize()
   at System.GC.RunFinalizers()
   ```

   One broader run completed 18 assertions before host abort and another completed 9. A final filtered discovery attempt reproduced the same host crash. These fixture-level validations are BLOCKED, not PASS. The targeted native cases above were rerun independently and passed.

### Real-data validation

Local data discovered (read only):

```text
1-FX_EURNZD_5M_20091026-20100417.bin | 2,226,624 bytes
2-FX_EURNZD_5M_20100418-20101006.bin | 2,207,360 bytes
3-FX_EURNZD_5M_20101006-20110328.bin | 2,241,024 bytes
4-FX_EURNZD_5M_20110328-20110917.bin | 2,283,776 bytes
```

10. **PASS with one SKIPPED characterization:** NUnit real-data tests.

    ```powershell
    & "$env:TEMP\dotnet-sdk-10-sdd2\dotnet.exe" test Tests\QuantConnect.Tests.csproj --no-restore --no-build -c Release --filter "FullyQualifiedName~BinaryRealDataTests" --logger "console;verbosity=normal"
    ```

    Result: process exit 0; one test passed and one skipped. The pass streamed all four files and 139,981 QuoteBars from `2009-10-26T00:00:00` through `2011-09-16T20:10:00`, crossed all file boundaries, preserved Bid/Ask and native Value semantics, and found a bounded sample proving global close is not forced into QuoteBar.Value. The multi-timeframe equivalence test was SKIPPED because only 5M is locally available. The test process printed the same Python.NET finalizer exception during shutdown, but NUnit completed, reported the assertion result, and returned exit 0; that environment symptom is recorded separately rather than treated as an SDD2 failure.

11. **PASS:** independent temporary real-data harness (outside the repository).

    ```powershell
    & "$env:TEMP\dotnet-sdk-10-sdd2\dotnet.exe" run --project "$env:TEMP\sdd2-real-harness\Sdd2RealHarness.csproj" --no-restore -c Release
    ```

    Result: exit 0:

    ```text
    PASS files=4 bars=139981 first=2009-10-26T00:00:00.0000000
    last=2011-09-16T20:10:00.0000000 boundaries=3 projectedTrades=12
    elapsed=00:00:00.1703958 maxManagedDelta=8669784
    retainedManagedDelta=1048 maxWorkingSetDelta=10690560 allocated=73995480
    ```

    These are characterization observations from one run, not old-vs-new benchmark claims.

### End-to-end Launcher runtime

A temporary algorithm requested EURNZD Oanda QuoteBars, a two-bar History call, one Daily warmup period, normal hourly feed data, and the same symbol as the benchmark. `Launcher/config.json` was temporarily pointed at `BinaryDataFeed`/`BinaryHistoryProvider`, then restored byte-for-byte. The temporary algorithm and generated result/log/data-monitor files were removed after execution.

12. **FAIL (configuration attempt):** adding `--data-feed-handler` on the command line was rejected because it is not a supported `LeanArgumentParser` option.

13. **FAIL (configuration attempt):** launching without `--config Launcher/config.json` selected the unrelated root optimizer `config.json` and failed deserializing optimizer parameter arrays for the backtest job.

14. **FAIL (environment/configuration attempt):** launching with the correct config but without an explicit data folder failed before algorithm execution because `../../../Data/symbol-properties/symbol-properties-database.csv` was not resolvable from that invocation.

15. **FAIL (validation-harness assertion, fixed):** the first complete binary runtime processed History and 49 normal callbacks but the temporary harness incorrectly required `OnData` to be called during warmup. LEAN's native warmup completed before normal callbacks; the harness was corrected to require `OnWarmupFinished` rather than a warmup `OnData` count.

16. **PASS:** final runtime command.

    ```powershell
    & "$env:TEMP\dotnet-sdk-10-sdd2\dotnet.exe" Launcher\bin\Release\QuantConnect.Lean.Launcher.dll --config Launcher/config.json --algorithm-type-name Sdd2BinaryRuntimeValidationAlgorithm --algorithm-location Launcher\bin\Release\QuantConnect.Algorithm.CSharp.dll --data-folder C:\Code_Projects\Lean\Data --close-automatically true
    ```

    Result: exit 0. LEAN loaded `BinaryDataFeed` and `BinaryHistoryProvider`, History returned 2 QuoteBars, native warmup completed, 49 chronological normal hourly callbacks were validated, QuoteBar Bid/Ask/Value and exact Symbol identity held, 103 total data points were processed, and the algorithm logged:

    ```text
    SDD2_BINARY_RUNTIME PASS history=2 warmup=0 normal=49 last=2010-04-21T00:00:00.0000000
    ```

### Git integrity

17. **PASS:** `git diff --check` returned exit 0 with no whitespace errors. Git printed only line-ending normalization warnings for existing LF working files.

18. **NOT EXECUTED:** a repository-wide all-tests run was not attempted after the fixture-level Python.NET crashes; targeted SDD2 and relevant native regression suites were used instead. This is not reported as PASS.

## 9. Runtime and memory observations

The implementation has no history-sized record collection. The hot path allocates one logical bar per emitted record; metadata lists scale with file count, the sequential path maps one file, the overlap path keeps one cursor/pending record per overlapping file, and consolidation retains one working bucket.

Observed real-data harness values for 139,981 bars are reported in section 8: about 8.67 MB maximum observed managed delta, 1,048 bytes retained managed delta after forced collection, about 10.69 MB maximum observed working-set delta, and about 74.0 MB cumulative managed allocation. The NUnit-host characterization observed different host-inclusive values (about 88.5 MB managed delta and 493.9 MB cumulative allocation) because it includes the large LEAN/NUnit/Python test environment. Neither set is a comparison benchmark, and no relative performance claim is made.

## 10. Remaining risks and limitations

- The intermittent Python.NET/Python.Runtime GIL finalizer crash blocks broad fixture execution in this environment. It also occurs during native test discovery and is not tied to an SDD2 assertion. Focused final suites pass.
- Local real data contains only one symbol/timeframe family (EURNZD 5M QuoteBarRow). There is no local real TradeBarRow file and no second native timeframe, so those real-data characterizations are unavailable; generated ABI/projection/equivalence tests cover them.
- Legacy files whose length is compatible with both ABIs and whose first 128 bytes remain ambiguous deliberately fail and require `_TB`/`_QB`; this is a safety policy, not a fallback guess.
- The branch remains intentionally unsynchronized with 24 newer official commits. Future upstream integration should re-audit the narrow native seams, especially `SynchronizingHistoryProvider` and `FileSystemDataFeed`.
- The custom streaming consolidator remains a narrow binary-source adapter because there is no clean current raw-source native consolidator seam. Any future LEAN seam change should prefer removing custom lifecycle code, not duplicating it.
- Package vulnerability warnings are pre-existing dependency warnings and were not remediated because dependency upgrades are outside SDD2 scope.

No functional SDD2 blocker remains for Problems 1 through 3.

## 11. Deferred Problem 4 engineering diagnosis

Problem 4 was not implemented. The proposal is technically plausible only after profiling demonstrates that scanning from record zero is a material bottleneck for representative History/DataFeed requests.

### Timestamp seek assessment

Fixed-width rows and monotonic timestamps permit a direct binary search within each already-resolved physical file. A future implementation should:

1. complete the existing constant-time PRT/file-length preflight;
2. use 64-bit-safe `recordIndex * recordSize` arithmetic;
3. binary-search for the first record whose canonical UTC timestamp can participate in the request, accounting for consolidator lookback/bucket semantics and fill-forward's need for a previous point;
4. begin each cursor at that index while retaining existing sequential/overlap merge, validation, duplicate, and disposal behavior;
5. prove byte-for-byte logical equivalence against the current full forward scan for exact, consolidated, overlapping, empty, corrupt, and DST-spanning cases.

The search must not trust filename dates as a substitute for record timestamps and must not skip the prior record required by `LastPointTracker`, fill-forward, or a partial consolidation bucket. Connected overlap groups do not invalidate per-file seek, but every active file needs its own lower bound before entering the existing bounded merge.

### Persistent/sidecar index critique

A sidecar index is not justified until direct fixed-record binary search is measured and found insufficient. If eventually required, the design needs explicit format versioning, ABI/endianness markers, source length and strong content identity, timestamp-range metadata, atomic write/rename, crash recovery, stale-index invalidation, concurrent-reader/writer policy, and read-only-data behavior. An index must be optional and correctness-neutral: a missing/stale/corrupt sidecar must fall back safely, never change logical output.

### Measurement plan

Any future old-vs-new benchmark must use the same commit except for the seek change, identical request sets and correctness oracle, isolated processes, cold- and warm-OS-cache runs, multiple start depths/range sizes, exact and consolidated resolutions, overlap groups, History and DataFeed, and reported distributions rather than a single timing. It must separately record time-to-first-data, total elapsed time, allocation, retained managed memory, working set, and records physically inspected. No comparative claim is made in this task.

### Multiprocessing/shared service critique

Multiprocessing, shared memory, IPC, centralized binary services, and cross-process object caches add lifecycle, invalidation, serialization, failure-recovery, and deployment complexity while the OS already shares file-backed MMF pages. They should remain deferred unless profiling proves a bottleneck not addressed by direct per-file seek and normal OS page cache behavior. No such code was added.

## 12. Final Git state

`git status --short` after implementation/report creation:

```text
 D "CODEX-MD-Chat-Action results.md"
 D CODEX_BINARY_PIPELINE_MIGRATION.md
 M Engine/DataFeeds/BinaryDataFeed.cs
 M Engine/DataFeeds/BinaryFileResolver.cs
 M Engine/DataFeeds/BinaryQuoteBarSubscriptionReader.cs
 M Engine/DataFeeds/BinaryTradeBarSubscriptionReader.cs
 M Engine/DataFeeds/Enumerators/Factories/BinarySubscriptionEnumeratorFactory.cs
 M Engine/HistoricalData/SynchronizingHistoryProvider.cs
 M Tests/Engine/DataFeeds/BinaryFileResolverTests.cs
 M Tests/Engine/DataFeeds/BinaryRealDataTests.cs
 M Tests/Engine/DataFeeds/BinarySubscriptionEnumeratorFactoryTests.cs
 M Tests/Engine/HistoricalData/BinaryHistoryProviderTests.cs
?? "Auroboros Binary Pipeline - Issues 2 - Solution — Pre-SDD.md"
?? CODEX_BINARY_PIPELINE_ITERATION_02_ISSUES.md
?? Engine/DataFeeds/BinaryDataSubscriptionReader.cs
?? Engine/DataFeeds/BinaryPhysicalSchema.cs
?? "Pre-SDD2-ISSUE 1-PURPOSE.md"
?? "SDD1-CODEX-Chat-Action results.md"
?? SDD1-CODEX_BINARY_PIPELINE_MIGRATION.md
?? SDD2-CODEX-Chat-Action results.md
?? SDD2-CODEX_BINARY_PIPELINE_HARDENING.md
?? Tests/Engine/DataFeeds/BinaryDataFeedTests.cs
```

The two deletions and five non-SDD2 untracked documents above are pre-existing user state.

`git diff --stat` (tracked working-tree changes only; Git does not include untracked added files):

```text
 CODEX-MD-Chat-Action results.md                    |  279 ----
 CODEX_BINARY_PIPELINE_MIGRATION.md                 | 1568 --------------------
 Engine/DataFeeds/BinaryDataFeed.cs                 |   10 +
 Engine/DataFeeds/BinaryFileResolver.cs             |  314 +++-
 .../DataFeeds/BinaryQuoteBarSubscriptionReader.cs  |  254 +---
 .../DataFeeds/BinaryTradeBarSubscriptionReader.cs  |  239 +--
 .../BinarySubscriptionEnumeratorFactory.cs         |   49 +-
 .../HistoricalData/SynchronizingHistoryProvider.cs |   87 +-
 Tests/Engine/DataFeeds/BinaryFileResolverTests.cs  |  261 +++-
 Tests/Engine/DataFeeds/BinaryRealDataTests.cs      |   85 ++
 .../BinarySubscriptionEnumeratorFactoryTests.cs    |  499 +++++--
 .../HistoricalData/BinaryHistoryProviderTests.cs   |  289 +++-
 12 files changed, 1216 insertions(+), 2718 deletions(-)
```

`git diff --check`:

```text
PASS (exit 0; no whitespace errors)
```

## 13. Final SDD2 completion checklist

- [x] Complete SDD2 read before editing.
- [x] Branch, status, remotes, history, fork master, and live official master audited.
- [x] SDD1 HEAD-vs-parent migration fully audited.
- [x] Current official native seams inspected.
- [x] Actual producer header inspected before modeling TradeBar padding.
- [x] PhysicalRecordType separated from requested LEAN type.
- [x] 32-byte TradeBarRow with no invented trailing field.
- [x] 64-byte QuoteBarRow with `Reserved` at 60..63 and no `Spread`.
- [x] Optional `_TB`/`_QB` plus legacy resolution implemented.
- [x] Constant-time per-file structural resolution and bounded 128-byte ambiguity probe implemented.
- [x] Ambiguous/invalid schemas fail explicitly.
- [x] QuoteBar projection uses stored Bid/Ask and native `Value == Close`.
- [x] QuoteBarRow-to-TradeBar uses global OHLCV directly.
- [x] Exact, then largest-smaller-exact-divisor timeframe policy implemented.
- [x] Finer and non-divisible requests rejected.
- [x] Streaming single-bucket consolidation retained.
- [x] Sequential non-overlap fast path implemented.
- [x] Bounded chronological overlap merge implemented.
- [x] Equivalent duplicates deduplicated; conflicting duplicates fail.
- [x] Mixed PRT TradeBar behavior implemented.
- [x] History and DataFeed share raw binary semantics.
- [x] Native warmup, LastPointTracker, fill-forward, filters, schedules, timezone/DST, universe routing, and lifecycle behavior retained and tested at relevant seams.
- [x] Early History and merged-source disposal hardened/tested.
- [x] Detailed deterministic diagnostics implemented/tested.
- [x] Outdated SDD1 expectations replaced.
- [x] Common TradeBar/QuoteBar market semantics left unchanged.
- [x] Targeted SDD2 tests passed.
- [x] Relevant selected native regressions passed.
- [x] Complete solution build passed.
- [x] Real local EURNZD data characterized without modification.
- [x] `git diff --check` passed.
- [x] Final diff reviewed for scope, allocation, duplicate lifecycle logic, resource leaks, and hidden materialization.
- [x] Problem 4 analyzed but not implemented.
- [x] No comparative performance claim made.
- [x] No commit/stage/push/merge/rebase/reset/history rewrite performed.

