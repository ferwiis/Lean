# Auroboros LEAN Fork — Binary Data Pipeline Streaming Migration

## Mission

Refactor the custom binary historical-data infrastructure in the Auroboros
fork of QuantConnect LEAN so that binary market data is consumed as a true
streaming LEAN data source instead of materializing entire historical ranges
into managed `List<TradeBar>` / `List<QuoteBar>` collections.

This is a critical engine-level refactor.

The final implementation must preserve LEAN semantics while replacing the
current batch runtime path with the existing memory-mapped streaming readers.

The target repository is the user's private/local LEAN fork.

Primary working branch:

`auroboros`

Do NOT assume that this document's examples exactly match the current APIs
after synchronization with upstream LEAN. Inspect the real code first and
adapt implementation details to the actual current signatures.

---

# CRITICAL OPERATING RULES

Before editing anything:

1. Inspect the repository and Git state.
2. Inspect the current `auroboros` branch.
3. Inspect the configured remotes.
4. Compare `auroboros` against the current official LEAN upstream branch.
5. Inspect all binary-pipeline files listed below.
6. Inspect the latest official implementations of:
   - `FileSystemDataFeed`
   - `SubscriptionDataReader`
   - `SubscriptionDataReaderSubscriptionEnumeratorFactory`
   - `SubscriptionDataReaderHistoryProvider`
   - related fill-forward/filter/warmup infrastructure.
7. Produce an internal implementation plan based on the CURRENT source code.
8. Only then begin modifications.

Do not blindly transplant code from old LEAN versions.

Do not assume signatures described in this document are still current.

The architectural intent of this specification is authoritative; concrete
method signatures must follow the current codebase.

---

# SAFETY / GIT RULES

Do NOT:

- commit
- push
- force push
- create pull requests
- delete branches
- run `git reset --hard`
- rewrite Git history
- discard existing user changes
- silently resolve unrelated merge conflicts
- modify unrelated engine components unless strictly required

The final repository should remain with the implementation changes visible
as ordinary uncommitted working-tree changes so the user can inspect them.

At the end provide:

- `git status --short`
- `git diff --stat`
- a clear per-file summary
- the exact commands required to inspect the complete diff
- build/test results
- unresolved risks

---

# PHASE 1 — Current architecture audit

Inspect at minimum:

```text
Engine/DataFeeds/BinaryDataFeed.cs
Engine/DataFeeds/BinaryDataLoader.cs
Engine/DataFeeds/BinaryFileResolver.cs
Engine/DataFeeds/BinaryTradeBarSubscriptionReader.cs
Engine/DataFeeds/BinaryQuoteBarSubscriptionReader.cs
Engine/HistoricalData/BinaryHistoryProvider.cs
Engine/DataFeeds/FileSystemDataFeed.cs
Engine/DataFeeds/SubscriptionDataReader.cs
Engine/DataFeeds/Enumerators/Factories/
    SubscriptionDataReaderSubscriptionEnumeratorFactory.cs
Engine/HistoricalData/SubscriptionDataReaderHistoryProvider.cs
Engine/QuantConnect.Lean.Engine.csproj
```

Search for every runtime reference to:

```text
BinaryDataLoader
BinaryTradeBarSubscriptionReader
BinaryQuoteBarSubscriptionReader
BinaryFileResolver
BinaryHistoryProvider
BinaryDataFeed
BarDataMode
bar-type
timeframe
```

Also inspect configuration/launcher wiring that chooses:

- DataFeed implementation
- HistoryProvider implementation

Do not rely only on the filenames listed above.

---

# KNOWN CURRENT PROBLEM

The current `BinaryDataLoader` is memory-mapped at the raw-byte level but is
NOT streaming at the LEAN object level.

Its current behavior conceptually resembles:

```text
MemoryMappedFile
      ↓
read records
      ↓
List<TradeBar> / List<QuoteBar>
      ↓
global list
      ↓
GroupBy / OrderBy / ToList
      ↓
optional consolidated list
      ↓
IEnumerator over already-materialized data
```

This defeats the principal memory advantage of a streaming data source.

For multi-year datasets it can materialize millions of managed LEAN objects.

With multiple independent Auroboros backtest processes, each process owns a
separate managed heap and therefore independently creates those object graphs.

This should not be described as a traditional memory leak.

It is excessive deliberate materialization and retention.

---

# TARGET ARCHITECTURE

The runtime data path should become conceptually:

```text
BinaryDataFeed
      ↓
Binary subscription enumerator/factory
      ↓
BinaryFileResolver
      ↓
BinaryTradeBarSubscriptionReader
              OR
BinaryQuoteBarSubscriptionReader
      ↓
streaming normalization / dedupe / consolidation as required
      ↓
LEAN enumerator wrappers
      ↓
FillForward / filters / schedules / warmup semantics
      ↓
Subscription
```

History should become:

```text
BinaryHistoryProvider
      ↓
Binary subscription enumerator/factory
      ↓
BinaryFileResolver
      ↓
BinaryTradeBarSubscriptionReader
              OR
BinaryQuoteBarSubscriptionReader
      ↓
streaming normalization
      ↓
UTC → exchange/data-time-zone handling
      ↓
streaming consolidation when required
      ↓
StrictDailyEndTimes
      ↓
Corporate Events where applicable
      ↓
FillForward
      ↓
SubscriptionFilter
      ↓
Subscription
      ↓
Slices
```

The binary runtime path must NOT require complete historical collections.

---

# DESIGN PRINCIPLE

Follow the architectural philosophy of modern LEAN's:

```text
FileSystemDataFeed
    → subscription enumerator factory
    → SubscriptionDataReader
    → IEnumerator<BaseData>
    → LEAN wrappers
```

Do not attempt to force the binary files into the CSV parser.

The binary implementation may use a custom factory/reader, but its lifecycle
and wrapping semantics should resemble LEAN's native architecture.

---

# PHASE 2 — Characterization before destructive changes

Before removing `BinaryDataLoader`, establish behavioral equivalence.

Where practical, create focused tests using small generated/test `.bin` files.

The existing Loader can temporarily serve as a reference implementation.

Characterize:

## TradeBar

- timestamp
- Time
- EndTime
- Period
- Symbol
- Open
- High
- Low
- Close
- Volume

## QuoteBar

- timestamp
- Time
- EndTime
- Period
- Symbol
- Bid OHLC
- Ask OHLC
- Value

Test at least:

- one file
- multiple sequential files
- request beginning inside a file
- request ending inside a file
- exact `[start, end)` boundaries
- empty file
- no matching files
- duplicate timestamp on adjacent file boundaries
- multiple available timeframes for the same symbol
- range spanning multiple files
- TradeBar
- QuoteBar

Do not delete the Loader until enough characterization exists to confidently
compare old and new behavior.

---

# PHASE 3 — BinaryFileResolver

`BinaryFileResolver` is conceptually correct and should remain.

Its responsibility must stay narrow:

```text
filesystem discovery
filename parsing
metadata
time-range file selection
```

It should NOT:

- instantiate LEAN bars
- perform consolidation
- perform global dedupe
- own LEAN subscription semantics

## Required improvement: timeframe selection

The current resolver primarily selects using:

```text
symbol
+
[startUtc, endUtc)
```

This is insufficient when several native periods exist for one symbol.

Example:

```text
EURUSD 5M
EURUSD 15M
EURUSD 1H
```

The binary pipeline must deterministically choose the correct native dataset.

Refactor resolver/factory behavior so that file selection includes native
period/resolution semantics.

Do NOT combine heterogeneous timeframes into one stream by accident.

The implementation should explicitly define:

- what native period is selected;
- what happens when requested period == native period;
- what happens when requested period > native period;
- what happens when requested period < available native period;
- how multiple candidate native periods are resolved.

Prefer the smallest sensible native period that can correctly satisfy the
request and then stream-consolidate upward if needed, unless the existing
Auroboros dataset contract defines a different rule.

Document the selected policy in code/tests.

---

# PHASE 4 — Harden the binary Subscription Readers

Existing readers:

```text
BinaryTradeBarSubscriptionReader
BinaryQuoteBarSubscriptionReader
```

are the intended replacement for runtime batch loading.

Preserve their core design:

- `IEnumerator<BaseData>`
- forward-only
- lazy file opening
- `MemoryMappedFile`
- read-only views
- one LEAN data point produced per `MoveNext()`
- explicit resource disposal
- multiple-file traversal
- requested time-window filtering
- no complete-dataset materialization

## 4.1 Remove hardcoded Symbol assumptions

Current readers create a symbol conceptually like:

```csharp
Symbol.Create(symbol, SecurityType.Forex, Market.Oanda)
```

Do not recreate a LEAN Symbol if the request already provides one.

Use the actual symbol/config/request supplied by LEAN.

The binary reader must preserve:

- SecurityType
- Market
- Symbol identity
- mapping identity where relevant

Do not hardcode Forex/Oanda in the core reader architecture.

If Auroboros currently supports only Forex binary datasets, validate that
constraint explicitly, but still consume the existing LEAN `Symbol`.

---

## 4.2 Avoid global materialization

No new runtime implementation should use any complete-dataset form of:

```csharp
ToList()
OrderBy()
GroupBy()
List<TradeBar> containing the whole request
List<QuoteBar> containing the whole request
```

Small bounded collections are acceptable for:

- a single consolidation bucket;
- metadata;
- a handful of file descriptors;
- test data.

Memory use for the historical stream should remain essentially independent
of the total number of historical bars.

---

## 4.3 Dedupe streaming

The old Loader performs global dedupe by timestamp.

Do not replace that with another global `GroupBy`.

Assuming files are chronologically sorted, implement dedupe incrementally.

Typical state:

```text
lastEmittedTime
```

or equivalent.

Handle duplicate timestamps at file boundaries without retaining the full
history.

If duplicates can occur non-adjacently because files are not sorted, detect
that invariant and either:

- validate/throw a useful error; or
- implement the smallest safe alternative.

Do not silently reintroduce full-dataset materialization.

---

## 4.4 File ordering

Ensure files are consumed deterministically in chronological order.

Validate metadata order.

If overlapping files exist, define deterministic behavior and test it.

---

## 4.5 Empty files

A zero-record file must not terminate the complete stream if later selected
files contain valid records.

Advance to the next file.

Test this explicitly.

---

## 4.6 Timestamp seeking

The current reader can scan from record zero until the requested `startUtc`.

Correctness is more important than optimization.

Do NOT implement a complicated binary index unless it is straightforward and
well-tested.

A future optimization may binary-search the first timestamp when records are
strictly sorted.

For this migration, first establish correct streaming behavior.

Document timestamp seek/indexing as future optimization if not implemented.

---

## 4.7 Large-file safety

Inspect integer conversions such as:

```csharp
checked((int)_accessor.Capacity)
```

Determine whether the actual Auroboros file format can exceed 2 GB per file.

If yes, remove unsafe `int` assumptions from record offsets/counts.

Use suitable 64-bit indexing where necessary.

If current dataset contracts guarantee files remain below that size, document
and validate the constraint.

---

# PHASE 5 — QuoteBar binary contract

The QuoteBar record layout is a critical binary ABI.

Current implementation assumes approximately:

```text
double timestamp
float Open
float High
float Low
float Close
float Volume
float BidOpen
float BidHigh
float BidLow
float BidClose
float AskOpen
float AskHigh
float AskLow
float AskClose
padding / record size contract
```

with a currently expected record size around 64 bytes.

Do NOT guess.

Locate the producer/exporter responsible for generating these `.bin` files,
if present in the accessible workspace/repository.

Verify:

- exact field order;
- exact primitive widths;
- exact packing;
- actual record size;
- presence/absence of absolute OHLCV;
- bid/ask fields;
- timestamp unit;
- timestamp timezone.

If the producer is outside this repository and cannot be inspected, preserve
the existing validated contract but make the assumption explicit.

Add tests that fail loudly when record-size/layout expectations are violated.

---

# PHASE 6 — Introduce a shared binary enumerator factory

Create the smallest useful abstraction that lets BOTH:

```text
BinaryDataFeed
BinaryHistoryProvider
```

request the same underlying streaming binary source.

Possible name:

```text
BinarySubscriptionEnumeratorFactory
```

The exact type/name is flexible if a better current-LEAN architecture exists.

Responsibilities:

- receive the real LEAN request/configuration context;
- inspect requested data type;
- select TradeBar vs QuoteBar reader;
- preserve the request Symbol;
- resolve appropriate native timeframe;
- obtain correct files through BinaryFileResolver;
- return `IEnumerator<BaseData>`;
- apply only binary-source-specific wrappers shared by DataFeed and History.

Do not duplicate file-reading logic in DataFeed and HistoryProvider.

Do not turn the factory into a second giant loader.

It must remain streaming.

---

# PHASE 7 — Time-zone semantics

This is critical.

Binary timestamps originate as UTC Unix timestamps.

LEAN's downstream enumerators often reason using request-local/exchange-local
times.

The existing `BinaryHistoryProvider` already contains an important correction:

```text
UTC → Exchange Time Zone
```

before subscription filtering.

Preserve this behavior.

Centralize or clearly standardize timestamp semantics so that:

```text
BinaryDataFeed
BinaryHistoryProvider
```

cannot disagree.

Determine from current LEAN APIs whether the correct destination should be:

- exchange timezone;
- configured data timezone;
- another normal LEAN normalization layer.

Follow CURRENT official LEAN semantics, not outdated assumptions.

Add tests for at least one non-UTC exchange timezone.

Do not compare raw UTC bar timestamps against local request boundaries.

---

# PHASE 8 — Streaming consolidation

The old batch implementation can consolidate:

```text
5m → 15m
5m → 1h
...
```

The new implementation must preserve required behavior WITHOUT:

```csharp
OrderBy(...whole stream...)
ToList(...whole stream...)
```

Use a forward-only consolidating enumerator/wrapper or existing suitable LEAN
consolidator infrastructure if that cleanly fits the data-feed pipeline.

Desired memory behavior:

```text
read bar
   ↓
current consolidation bucket
   ↓
emit consolidated bar
   ↓
clear/reuse bucket
```

Only the current bounded window should remain in memory.

Correctly compute:

TradeBar:

- first Open
- max High
- min Low
- last Close
- summed Volume
- correct Time
- correct EndTime
- correct Period
- correct Symbol

QuoteBar:

- Bid OHLC
- Ask OHLC
- representative Value
- Time
- EndTime
- Period
- Symbol

Test consolidation across a physical `.bin` file boundary.

Define behavior for a final partial bucket according to the existing Auroboros
contract / LEAN expectations.

---

# PHASE 9 — Refactor BinaryDataFeed

This is one of the main objectives.

Currently the custom feed still calls:

```text
BinaryDataLoader.LoadAsTradeBars
BinaryDataLoader.LoadAsQuoteBars
```

Replace this runtime path with the streaming binary enumerator factory/readers.

After migration, normal binary subscriptions must NOT materialize their
historical range in a collection.

## Data type selection

Do not use global configuration such as `bar-type` as the authoritative source
when LEAN's request already identifies the requested type.

Prefer:

```text
request.Configuration.Type
request.DataType
```

or the appropriate current APIs.

The request must be the source of truth.

Avoid contradictions such as:

```text
LEAN requests QuoteBar
config says TradeBar
```

---

## Remove obsolete adapters

The current feed needs:

```text
TradeBarEnumeratorAdapter
QuoteBarEnumeratorAdapter
```

because it converts:

```text
IEnumerator<TradeBar>
IEnumerator<QuoteBar>
```

into:

```text
IEnumerator<BaseData>
```

The new subscription readers already implement:

```text
IEnumerator<BaseData>
```

Once the Loader path is gone, remove these adapters unless another legitimate
consumer remains.

---

## Align with current FileSystemDataFeed

Do not merely patch the old BinaryDataFeed.

Compare it method-by-method against the CURRENT upstream `FileSystemDataFeed`.

Port relevant modern behavior, including where applicable:

- current `CreateEnumerator` structure;
- current warmup handling;
- `LastPointTracker`;
- warmup/normal overlap behavior;
- fill-forward arguments;
- schedule wrapper behavior;
- universe handling;
- factor file provider propagation;
- daily strict end times;
- filter semantics;
- current `SubscriptionUtils` signatures;
- disposal/lifetime patterns.

BinaryDataFeed should differ from FileSystemDataFeed primarily in how the
underlying raw data enumerator is created.

Do not fork unnecessary LEAN behavior.

---

# PHASE 10 — Warmup correctness

Modern `FileSystemDataFeed` uses a `LastPointTracker` around warmup/normal
transitions.

The binary feed must preserve current LEAN behavior.

Test:

- no warmup;
- warmup;
- changed warmup resolution;
- warmup → normal transition;
- overlap region;
- no duplicated transition point;
- no gap introduced by the binary source.

Do not simplify this area merely because current Auroboros strategies happen
not to expose a bug.

---

# PHASE 11 — Universe subscriptions

The binary source is primarily intended for historical market bars.

Preserve official LEAN behavior for universe subscriptions unless there is a
specific binary implementation for them.

Do not accidentally route:

```text
FundamentalUniverse
UserDefinedUniverse
TimeTriggeredUniverse
```

through a Forex binary bar reader.

Follow current `FileSystemDataFeed` behavior for universe-specific requests.

Only normal TradeBar/QuoteBar market subscriptions should use the custom
binary source unless explicitly supported.

---

# PHASE 12 — Refactor BinaryHistoryProvider

The current BinaryHistoryProvider still calls `BinaryDataLoader`.

Replace its underlying raw source with the shared streaming binary enumerator.

Preserve, using current LEAN ordering semantics:

- proper timestamp normalization;
- StrictDailyEndTimes;
- CorporateEventEnumeratorFactory where appropriate;
- QuoteBar fill-forward;
- FillForwardEnumerator;
- SubscriptionFilterEnumerator;
- request time-window filtering;
- parallel/non-parallel history subscription creation;
- Slice synchronization.

Compare closely with current:

```text
SubscriptionDataReaderHistoryProvider
```

The custom provider should differ primarily at the point where raw historical
market data is obtained.

---

## Eliminate batch consolidation from HistoryProvider

Do not do:

```csharp
src.OrderBy(...)
```

over a multi-year streaming source.

Use streaming consolidation.

Do not call `.Count` on a source if that forces enumeration/materialization.

Do not enumerate the source twice.

---

# PHASE 13 — BinaryDataLoader retirement

Do NOT delete `BinaryDataLoader` at the beginning.

Delete/retire it only after:

```text
BinaryDataFeed
BinaryHistoryProvider
```

no longer depend on it and characterization tests pass.

Search the entire repository:

```bash
git grep -n "BinaryDataLoader"
```

If there are no legitimate runtime consumers:

Preferred result:

```text
remove BinaryDataLoader.cs
```

If there is still a legitimate diagnostic/offline/test use case:

rename/reframe it explicitly as something like:

```text
BinaryBatchDataLoader
```

and keep it outside the runtime subscription path.

It must not be easy for future engine code to accidentally return to
full-history materialization.

Update comments that falsely describe obsolete implementation details.

---

# PHASE 14 — Remove obsolete configuration

Audit:

```text
bar-type
timeframe
BarDataMode
```

If LEAN request/configuration semantics fully replace these settings:

remove them from the runtime binary feed.

Do not leave two conflicting sources of truth.

If `timeframe` remains necessary for a specific Auroboros business contract,
document exactly why and how it differs from:

```text
request.Configuration.Resolution
request.Configuration.Increment
```

---

# PHASE 15 — Resource lifetime

Verify disposal end-to-end.

Each binary reader must correctly dispose:

```text
MemoryMappedViewAccessor
MemoryMappedFile
acquired pointers
nested enumerators
wrappers
```

Check:

- normal completion;
- early subscription cancellation;
- exceptions;
- switching files;
- empty file;
- history enumeration ending early;
- feed shutdown.

Avoid swallowing serious resource-lifetime errors.

A best-effort cleanup catch may be acceptable only when justified and logged.

---

# PHASE 16 — Multiprocess behavior

Do NOT implement application-level IPC/shared object caching in this refactor.

Auroboros uses independent processes for optimization families.

Therefore:

```text
C# singleton
```

cannot share LEAN object instances across those processes.

The intended optimization is:

```text
same file-backed .bin files
      ↓
independent MemoryMappedFile mappings
      ↓
OS filesystem/page cache can reuse physical pages
      ↓
each process creates only current/near-current LEAN objects
```

This is vastly simpler than IPC and should be benchmarked first.

Do not implement:

- named shared-memory bar graphs;
- IPC serialization;
- cross-process singleton services;
- centralized bar-object caches

unless objective benchmarks later prove they are necessary.

---

# PHASE 17 — Test requirements

Add focused tests where practical.

Minimum logical coverage:

## BinaryFileResolver

- filename parsing
- invalid filename
- symbol matching
- timeframe matching
- `[start, end)` intersection
- deterministic ordering
- no matches
- overlapping files

## TradeBar reader

- correct parsing
- exact range boundaries
- multiple files
- empty file
- duplicate boundary
- chronological ordering
- resource disposal

## QuoteBar reader

Same coverage plus:

- Bid values
- Ask values
- Value
- binary record contract

## Timezone

- UTC source timestamp
- non-UTC exchange
- correct downstream local Time/EndTime
- filters do not discard valid data

## Consolidation

- base period
- 5m → 15m
- multi-file boundary
- OHLC correctness
- volume
- QuoteBar Bid/Ask
- partial final bucket

## BinaryDataFeed

- TradeBar request
- QuoteBar request
- request type determines reader
- fill-forward
- warmup
- warmup transition
- schedule/universe path not broken

## BinaryHistoryProvider

- TradeBar history
- QuoteBar history
- fill-forward
- requested range
- timezone
- consolidated history
- parallel history path where testable

---

# LOCAL BINARY TEST DATA

Real Auroboros binary test data is available locally under:

`Data/historical_data`

Use this dataset for characterization, integration tests, and runtime validation.

Do not modify or delete the original binary files.

Prefer a small deterministic subset for automated tests instead of scanning
the complete historical dataset unnecessarily.

The binary files follow the existing Auroboros naming/layout conventions
consumed by `BinaryFileResolver`.

Before changing the binary record readers, inspect the available files and
confirm that their naming, period metadata, and record layout agree with the
current reader assumptions.

When validating the migration, test at minimum:

- one binary file;
- multiple consecutive binary files;
- a request starting inside a file;
- a request ending inside a file;
- crossing a physical file boundary;
- the native timeframe;
- upward consolidation if applicable;
- TradeBar and QuoteBar datasets if both are available.

Do not commit binary market data to Git.

---

# PHASE 18 — Build and static validation

Run formatting/compiler/static validation appropriate to the repository.

At minimum:

```bash
git diff --check
```

Build the affected solution/projects.

Run targeted tests first.

Then run broader relevant LEAN tests if runtime permits.

Do not claim success for tests that were not actually executed.

For every failed test distinguish:

```text
introduced failure
pre-existing failure
environment/data dependency
```

---

# PHASE 19 — Runtime validation

If the local historical Auroboros `.bin` dataset is available:

run at least one known binary backtest.

Prefer a small deterministic date range first.

Validate:

```text
bars actually arrive
OnData receives expected data
warmup completes
portfolio clock advances
History works
no duplicate timestamps
no obvious gaps
TradeBar/QuoteBar types match request
```

Then run a longer range.

Do NOT interpret strategy profitability as validation of the data pipeline.

Validate data correctness separately from trading logic.

---

# PHASE 20 — Memory/performance benchmark

Compare old batch implementation against new streaming implementation if the
old code remains temporarily available during testing.

Measure where possible:

```text
wall-clock load/backtest time
process Private Bytes
working set / RSS
GC Gen0 collections
GC Gen1 collections
GC Gen2 collections
peak managed heap
page faults if practical
```

Test:

```text
1 process
multiple Auroboros processes reading the same files
```

Primary success criterion is not only speed.

The critical improvement is that peak managed memory should not grow roughly
linearly with the full requested historical bar count.

Streaming should keep only bounded working state.

---

# ACCEPTANCE CRITERIA

The migration is successful only when ALL applicable conditions below hold.

### Architecture

- BinaryDataFeed no longer loads a complete historical range through
  BinaryDataLoader.
- BinaryHistoryProvider no longer loads a complete historical range through
  BinaryDataLoader.
- TradeBar and QuoteBar use forward-only streaming readers.
- BinaryFileResolver remains filesystem/metadata focused.
- Request information, not global `bar-type`, determines the LEAN data type.
- Reader does not hardcode a new Forex/Oanda Symbol when LEAN already supplied
  one.
- Timeframe selection is deterministic.
- Dedupe is streaming.
- Consolidation is streaming.
- No whole-stream `OrderBy`, `GroupBy` or `ToList` is required by the binary
  runtime path.

### LEAN compatibility

- Current FileSystemDataFeed warmup semantics are preserved where applicable.
- LastPointTracker behavior is preserved where current LEAN requires it.
- Fill-forward remains correct.
- exchange/data-time-zone semantics remain correct.
- scheduled/universe subscriptions are not broken.
- BinaryHistoryProvider retains the correct LEAN history wrapper order.

### Resource correctness

- MMF views are disposed.
- pointers are released.
- files can transition correctly.
- early disposal does not leak mapped handles.

### Correctness

- TradeBar values match binary input.
- QuoteBar values match binary input.
- timestamps are correct.
- `[start, end)` semantics are correct.
- no unexpected duplicates.
- no missing file-boundary data.
- consolidation values are correct.

### Validation

- solution builds;
- targeted tests pass;
- applicable integration test/backtest passes;
- `git diff --check` passes.

### Memory

- the runtime no longer retains a complete multi-year collection of LEAN bars;
- memory remains bounded relative to stream size;
- multiprocess tests show substantially improved private-memory behavior versus
  batch materialization.

---

# DEFINITION OF DONE FOR BinaryDataLoader

Only after all runtime references are gone:

```bash
git grep -n "BinaryDataLoader"
```

should determine its fate.

If no legitimate use remains:

```text
delete it
```

If retained solely for test/offline utilities:

```text
rename it clearly as batch-only
move it away from the production streaming path
document it as non-runtime
```

There should be no ambiguity about which architecture is canonical.

---

# DO NOT OVERENGINEER

Do not introduce:

- IPC caches
- distributed caches
- custom memory managers
- unsafe shared-memory synchronization
- premature timestamp indexes
- generalized support for every LEAN asset type
- broad unrelated refactors

The immediate supported scope is the Auroboros binary historical-data use case.

Prefer a small, idiomatic LEAN-compatible streaming implementation.

---

# IMPORTANT NON-GOALS

This task is NOT:

- rewriting all LEAN data infrastructure;
- contributing the feature upstream to QuantConnect;
- porting Auroboros to live trading;
- implementing Tick/OpenInterest/CustomData unless required by existing code;
- changing strategy logic;
- changing portfolio/risk logic;
- changing optimization algorithms;
- implementing IPC.

This is a private fork improvement.

---

# COMMENTS / DOCUMENTATION CLEANUP

Update obsolete comments.

In particular, remove comments that claim future migration to
MemoryMappedFile when the implementation already uses MemoryMappedFile.

Document actual behavior, not historical intentions.

Clearly mark unsupported binary data types.

---

# IMPLEMENTATION STYLE

Prefer:

- existing LEAN abstractions;
- small enumerator wrappers;
- single responsibility;
- deterministic behavior;
- bounded memory;
- clear ownership/disposal;
- tests around edge conditions.

Avoid:

- unnecessary custom frameworks;
- giant utility classes;
- duplicating official LEAN behavior;
- hidden global configuration when request context already exists.

---

# FINAL REVIEW BEFORE STOPPING

Before considering the task complete, search for problematic patterns in the
affected runtime path:

```bash
git grep -n "BinaryDataLoader"
git grep -n "ToList()" Engine/DataFeeds Engine/HistoricalData
git grep -n "OrderBy(" Engine/DataFeeds Engine/HistoricalData
git grep -n "GroupBy(" Engine/DataFeeds Engine/HistoricalData
git grep -n "Market.Oanda" Engine/DataFeeds
git grep -n "bar-type"
```

These searches do not imply every match is wrong.

Review each relevant match manually.

---

# REQUIRED FINAL RESPONSE FROM CODEX

Do not simply say "done".

Provide a structured engineering report containing:

## 1. Repository state

- working branch
- upstream reference used
- whether upstream integration occurred
- whether baseline built before refactoring

## 2. Architecture implemented

Explain the final pipeline.

Example format:

```text
BinaryDataFeed
  → BinarySubscriptionEnumeratorFactory
  → BinaryFileResolver
  → Binary*SubscriptionReader
  → wrappers
  → Subscription
```

and equivalent History path.

## 3. Files changed

For every changed file:

```text
path
why it changed
main behavioral change
```

## 4. Files removed

Explain every deletion.

Especially explain whether `BinaryDataLoader` was:

```text
deleted
retained
renamed
```

and why.

## 5. Tests

For each command:

```text
command
result
```

Never report an unexecuted test as passing.

## 6. Runtime validation

State whether a real `.bin` backtest was executed.

If not, state why.

## 7. Memory/performance observations

Provide actual measurements if benchmarking was possible.

Otherwise explicitly state that no runtime benchmark was performed.

## 8. Remaining risks

Be specific.

## 9. Git diff

Run:

```bash
git status --short
git diff --stat
git diff --check
```

Then tell the user how to inspect:

```bash
git diff
```

and, if useful:

```bash
git diff -- Engine/DataFeeds/
git diff -- Engine/HistoricalData/
```

Do not commit.

Do not push.

Leave all modifications available for user review.

---

# FINAL PRINCIPLE

Correctness and architectural consistency with modern LEAN are more important
than preserving the historical Auroboros implementation.

However, preserve the proven Auroboros binary-data contract and the existing
UTC/exchange-time-zone correction unless current LEAN semantics demonstrably
require a different implementation.

The desired end state is:

```text
file-backed binary data
        +
MemoryMappedFile
        +
forward-only IEnumerator<BaseData>
        +
bounded managed memory
        +
native LEAN subscription semantics
```

not:

```text
file-backed binary data
        +
millions of LEAN objects accumulated before enumeration
```