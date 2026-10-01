# Auroboros LEAN Fork — SDD2: Binary Contract, Request Resolution, and Stream Integrity

## Document status

This is the execution specification for the second Auroboros binary-pipeline implementation pass.

It is intended to be given directly to Codex running a high-reasoning model configuration. The architectural decisions are already closed. Codex is expected to inspect the real repository, adapt the mechanics to the current LEAN APIs, implement Problems 1–3, validate the result, and leave all source changes uncommitted for human review.

Problem 4 is deliberately **informational and deferred** in this SDD. Codex must review and critique the proposed Problem 4 plan at the end, but must not implement it.

The final result of this task must include a new repository-root report:

`SDD2-CODEX-Chat-Action results.md`

The final chat response should be brief and should point the user to that report instead of reproducing the full engineering report in chat.

---

# 1. Mission

The Auroboros LEAN fork already completed the main architectural migration from a batch binary loader to a forward-only, memory-mapped streaming source integrated with native LEAN lifecycles.

SDD1 removed the dominant architectural problem:

```text
binary files
    ↓
materialize the full historical range
    ↓
List<TradeBar> / List<QuoteBar>
    ↓
LEAN
```

and replaced it with a design conceptually equivalent to:

```text
binary files
    ↓
MemoryMappedFile
    ↓
forward-only record reader
    ↓
IEnumerator<BaseData>
    ↓
native LEAN lifecycle wrappers
    ↓
History / DataFeed
```

SDD2 does **not** redesign that migration.

SDD2 hardens the streaming architecture so it has an explicit binary contract, deterministic request resolution, correct overlap semantics, native-like LEAN behavior, bounded memory, and a maintainable implementation that resembles code LEAN itself could reasonably have developed.

The business objective is:

```text
correct historical semantics
+
minimal avoidable binary-read overhead
+
bounded managed memory
+
native LEAN lifecycle compatibility
+
deterministic behavior
+
clean future maintainability
```

The objective is not to make an unsupported marketing claim such as "the fastest binary reader in the world."

The objective is to remove avoidable overhead and preserve the shortest correct path from fixed-width binary market data to the LEAN objects the engine actually requires.

---

# 2. Business context

Auroboros uses LEAN for large historical backtesting and optimization workloads.

The binary dataset exists to avoid unnecessary CSV/ZIP parsing and to make large local historical datasets practical for repeated strategy execution.

The most important runtime properties are therefore:

- low time-to-first-data;
- streaming rather than full-history materialization;
- deterministic request semantics;
- no silent data fabrication;
- no silent loss of valid records at file boundaries;
- no dependency on filename ordering for price selection;
- bounded memory with respect to total historical record count;
- efficient reuse of LEAN's existing subscription, History, warmup, fill-forward, timezone, schedule, and lifecycle machinery;
- a narrow custom patch that can remain maintainable as upstream LEAN evolves.

Multiprocessing, shared-memory services, IPC caches, and cross-process object sharing remain outside the current scope.

---

# 3. Authoritative sources and precedence

Use the following precedence when implementing this SDD:

1. **This SDD2 specification** defines the required semantics.
2. **The real current `auroboros` working tree** defines the concrete APIs and any user changes that must be preserved.
3. **`Auroboros Binary Pipeline - Issues 2 - Solution — Pre-SDD.md`** contains the architectural decisions from which this SDD was derived.
4. **`SDD1-CODEX-Chat-Action results.md`** records what SDD1 actually implemented and validated.
5. **`SDD1-CODEX_BINARY_PIPELINE_MIGRATION.md`** explains the original migration intent.
6. **Current official LEAN master** is a reference for API shape, lifecycle behavior, code organization, naming, comments, tests, and implementation style.

If an example signature in this document differs from the real current source code, adapt the implementation to the real API.

Do **not** reinterpret the architecture because a method signature changed.

The architectural decisions in this SDD are the source of truth.

---

# 4. Draft-time repository snapshot — informational only

At the time this SDD was prepared, the connected remote repository showed:

```text
repository: ferwiis/Lean
branch:     auroboros
HEAD:       18fdeeae4b08b9d8012a8854d13dd84319f04187
parent:     5630bfa0cbc915b209e0fb33cdbd5185ecb3096c
fork master: 6eb389012d73c364547d61546ff822fc8432dee2
```

The current official QuantConnect/Lean master observed at drafting time was newer than the fork master.

These SHAs are **not execution-time assumptions**.

Codex must inspect the current repository again before editing.

The important historical relationship to preserve conceptually is:

```text
fork master
    ↓
initial Auroboros high-performance binary-pipeline commit
    ↓
SDD1 streaming migration checkpoint
    ↓
SDD2 working-tree implementation
```

The user will create the next commit after reviewing the completed SDD2 implementation.

Codex must not create that commit.

---

# 5. Critical operating rules

Before modifying any source file:

1. inspect `git status --short`;
2. confirm the current branch;
3. inspect configured remotes;
4. inspect recent branch history;
5. identify the current parent/previous checkpoint;
6. compare the current `auroboros` HEAD against its immediate parent;
7. compare the branch against the fork master;
8. inspect current official LEAN implementations relevant to the seams used by this feature;
9. inspect every current binary-pipeline source and test file;
10. inspect any local uncommitted user changes before touching overlapping files;
11. produce an internal implementation plan;
12. only then begin editing.

Do not assume the remote HEAD exactly matches the user's local working tree.

If the user already has a local correction such as the `Reserved` field change, preserve it and build on it.

Do not overwrite unrelated user modifications.

---

# 6. Required pre-edit audit

Inspect at minimum the current versions of:

```text
Engine/DataFeeds/BinaryDataFeed.cs
Engine/DataFeeds/BinaryFileResolver.cs
Engine/DataFeeds/BinaryTradeBarSubscriptionReader.cs
Engine/DataFeeds/BinaryQuoteBarSubscriptionReader.cs
Engine/DataFeeds/Enumerators/Factories/BinarySubscriptionEnumeratorFactory.cs
Engine/DataFeeds/FileSystemDataFeed.cs

Engine/HistoricalData/BinaryHistoryProvider.cs
Engine/HistoricalData/SubscriptionDataReaderHistoryProvider.cs

Tests/Engine/DataFeeds/BinaryFileResolverTests.cs
Tests/Engine/DataFeeds/BinarySubscriptionEnumeratorFactoryTests.cs
Tests/Engine/DataFeeds/BinaryRealDataTests.cs
Tests/Engine/HistoricalData/BinaryHistoryProviderTests.cs
```

Search the entire repository for:

```text
BinaryDataLoader
BinaryFileResolver
BinaryTradeBarSubscriptionReader
BinaryQuoteBarSubscriptionReader
BinarySubscriptionEnumeratorFactory
BinaryHistoryProvider
BinaryDataFeed
BarDataMode
bar-type
timeframe
TradeBarRow
QuoteBarRow
Spread
Reserved
first-file-wins
```

Also inspect the current official LEAN master implementations of the narrow native paths that the custom binary backend relies on.

At minimum inspect the current equivalent of:

```text
FileSystemDataFeed
SubscriptionDataReader
SubscriptionDataReaderSubscriptionEnumeratorFactory
SubscriptionDataReaderHistoryProvider
QuoteBar
FillForwardEnumerator
QuoteBarFillForwardEnumerator
StrictDailyEndTimesEnumerator
SubscriptionFilterEnumerator
SubscriptionUtils
```

Do not perform a broad unrelated LEAN audit.

The purpose of upstream inspection is to make the custom implementation look and behave like current LEAN, not to synchronize branches in this task.

---

# 7. Required diff audit before implementation

Codex must understand what SDD1 changed before changing it again.

Inspect the diff between:

```text
previous binary-pipeline commit
vs
current SDD1 streaming checkpoint
```

At drafting time this was conceptually:

```text
5630bfa0...
    ↓
18fdeeae...
```

Confirm the real execution-time SHAs.

The implementation plan must account for the fact that SDD1 already:

- removed runtime full-history materialization;
- retired `BinaryDataLoader` from the runtime path;
- introduced forward-only MMF readers;
- introduced a shared binary subscription enumerator factory;
- made `BinaryDataFeed` reuse `FileSystemDataFeed`;
- made `BinaryHistoryProvider` reuse `SubscriptionDataReaderHistoryProvider`;
- preserved the real LEAN `Symbol`;
- added 64-bit record offsets/counts;
- added lazy file opening and deterministic disposal;
- added streaming consolidation;
- added timezone handling;
- added real-data characterization;
- added generated binary-reader tests.

Do not re-solve problems already solved by SDD1 unless this SDD explicitly changes their semantics.

---

# 8. Git and repository safety rules

Do not:

- commit;
- stage files;
- push;
- force-push;
- create a pull request;
- merge;
- rebase;
- reset history;
- switch branches destructively;
- synchronize fork master;
- update upstream refs in a way that changes the branch;
- delete user files;
- rewrite historical `.bin` files;
- download replacement historical market data;
- discard unrelated working-tree changes.

Leave all SDD2 implementation changes visible in the working tree.

The user owns commit creation and later history cleanup.

---

# 9. Copyright and file-header rule

For **new Auroboros-specific files written from scratch**, do not copy a QuantConnect copyright banner into the file.

If a new custom file needs a header, use an Auroboros-specific header or no copyright header, consistent with the surrounding custom Auroboros files.

Do not add a new QuantConnect copyright claim to original Auroboros code.

However, this engineering task does **not** authorize removal or alteration of existing third-party copyright/license notices from upstream-derived LEAN files. Preserve existing legal notices in files inherited from upstream or derived from upstream code.

Do not perform broad copyright-header cleanup as part of SDD2.

---

# 10. Scope

## 10.1 Implement in SDD2

Implement the agreed solutions for:

```text
Problem 1 — Binary physical contract vs logical LEAN type
Problem 2 — LEAN request resolution against available datasets
Problem 3 — Stream chronology, overlap integrity, and LEAN lifecycle correctness
```

Also implement the tests required to prove those changes.

## 10.2 Do not implement in SDD2

Do not implement Problem 4 items:

- binary-search timestamp seek;
- persistent indexes;
- sidecar indexes;
- old-vs-new benchmark infrastructure;
- benchmark claims;
- upstream synchronization/rebase;
- final fixup/squash;
- final patch-stack cleanup;
- final documentation cleanup;
- multiprocessing redesign;
- shared memory;
- IPC;
- centralized binary data service;
- cross-process object caches.

At the end of the task, diagnose Problem 4 and evaluate the proposed future solution.

Do not modify runtime code for Problem 4.

---

# 11. Supported scope after SDD2

The supported production scope remains intentionally narrow.

## 11.1 Security type

Current supported binary historical-data market:

```text
Forex
```

Do not claim support for all LEAN asset classes.

The architecture may remain extensible, but unsupported markets must not silently enter the Forex reader path.

## 11.2 Logical LEAN data types

Supported logical output types:

```text
TradeBar
QuoteBar
```

Unsupported unless already required by current verified code:

```text
Tick
OpenInterest
Auxiliary custom binary market data
arbitrary CustomData
```

## 11.3 Natural Forex semantics

For Auroboros internal code that genuinely needs Forex market quotes, the natural logical request is:

```text
Forex
    ↓
QuoteBar
```

Do not use an accidental `TradeBar` overload to stand in for a Forex quote request.

An explicit `TradeBar` request may still be satisfied if a supported physical schema can legitimately project to `TradeBar`.

---

# 12. Core architectural invariant

The most important SDD2 invariant is:

```text
PhysicalRecordType
        ≠
RequestedLeanType
```

A physical file describes how bytes are laid out.

A LEAN request describes what logical object the engine needs.

The runtime must no longer choose the physical reader solely from:

```text
request.DataType
```

or:

```text
request.Configuration.Type
```

The correct conceptual flow is:

```text
file candidate
    ↓
resolve physical schema
    ↓
validate physical schema
    ↓
test physical → logical compatibility
    ↓
select projection
    ↓
produce logical LEAN data
```

---

# 13. Physical record type model

Introduce the smallest maintainable representation needed to model physical schemas explicitly.

Preferred conceptual name:

```text
PhysicalRecordType
```

Initial values:

```text
TradeBarRow
QuoteBarRow
```

Do **not** encode record sizes into the type names.

Do not introduce names such as:

```text
TradeBar32
TradeBarRow32
QuoteBar64
QuoteBarRow64
FxQuote64
```

The sizes belong in the ABI contract, tests, and schema descriptors.

They do not belong in the semantic type names.

A minimal enum plus small schema metadata/helper is acceptable.

Do not build a generalized serialization framework.

---

# 14. Binary ABI

## 14.1 Common timestamp contract

Both physical record types begin with:

```text
double Timestamp
```

Timestamp semantics:

```text
Unix seconds
UTC
```

Use the existing producer contract and current validated behavior.

Do not silently reinterpret timestamps as local time.

Do not introduce a second timestamp convention.

---

# 15. `TradeBarRow` ABI

`TradeBarRow` remains a fixed-width record.

Total record size:

```text
32 bytes
```

Logical fields:

```text
offset 0   double Timestamp
offset 8   float  Open
offset 12  float  High
offset 16  float  Low
offset 20  float  Close
offset 24  float  Volume
offset 28  4 bytes trailing native ABI alignment/padding
```

The final four bytes are part of the existing 32-byte producer ABI shape.

Do not rename the type to include `32`.

Do not invent new price semantics for those trailing bytes.

If producer inspection proves a stronger explicit contract for those final bytes, document it in tests and the Action Results. Otherwise preserve them as ABI padding/alignment.

Supported projection:

```text
TradeBarRow
    ↓
TradeBar
```

Unsupported projection:

```text
TradeBarRow
    ✗
QuoteBar
```

---

# 16. `QuoteBarRow` ABI

`QuoteBarRow` is a fixed-width record.

Total record size:

```text
64 bytes
```

Required layout:

```text
offset 0   double Timestamp

offset 8   float  Open
offset 12  float  High
offset 16  float  Low
offset 20  float  Close
offset 24  float  Volume

offset 28  float  BidOpen
offset 32  float  BidHigh
offset 36  float  BidLow
offset 40  float  BidClose

offset 44  float  AskOpen
offset 48  float  AskHigh
offset 52  float  AskLow
offset 56  float  AskClose

offset 60  uint32 Reserved
```

The field at bytes `60..63` is:

```text
Reserved
```

It is not `Spread`.

The physical format must no longer expose those bytes as a price field.

If the local working tree already contains this correction, preserve it.

If the current source still declares `Spread`, replace that physical field definition with `Reserved` and update tests accordingly.

`Reserved`:

- must not affect QuoteBar pricing;
- must not affect TradeBar projection;
- must not be exposed as spread;
- must not be used as record identity;
- must not be repurposed without an explicit future ABI version.

Do not infer additional semantics for `Reserved` in this task.

---

# 17. `QuoteBarRow` projection matrix

`QuoteBarRow` supports two logical projections:

```text
QuoteBarRow
    ├──→ QuoteBar
    └──→ TradeBar
```

## 17.1 `QuoteBarRow → TradeBar`

Use the global OHLCV fields:

```text
Open
High
Low
Close
Volume
```

Do not construct a `QuoteBar` first.

Create the `TradeBar` directly from the global OHLCV fields.

This avoids unnecessary allocations and preserves the intended global aggregate semantics.

## 17.2 `QuoteBarRow → QuoteBar`

Use:

```text
Bid OHLC
Ask OHLC
```

as the authoritative quote-side fields.

The global OHLCV fields are not the source of `QuoteBar.Value`.

Do not overwrite vanilla QuoteBar pricing with the global close.

---

# 18. Vanilla `QuoteBar.Value` semantics

The custom binary path must behave like vanilla LEAN.

Conceptually:

```text
Bid.Close + Ask.Close
        ↓
QuoteBar.Close
        ↓
QuoteBar.Value
```

Use the current official `QuoteBar` implementation as the exact reference.

The preferred implementation is to construct the QuoteBar in the most vanilla-compatible way available in the current API, so that:

```text
Value == QuoteBar.Close
```

after Bid/Ask are assigned.

Do not do:

```text
Value = record.Close
```

where `record.Close` is the global OHLCV close.

Do not modify `Common/Data/Market/QuoteBar.cs` to make the custom binary format fit.

The binary adapter must fit LEAN, not the reverse.

---

# 19. Bid/Ask fallback policy

Synthetic or reconstructed Bid/Ask belongs to Data Engineering / ingestion.

Once a valid `QuoteBarRow` exists on disk, the LEAN binary reader must:

- consume the stored Bid/Ask values;
- not invent a new spread;
- not regenerate Bid/Ask from global OHLCV;
- not replace stored Bid/Ask with midpoint values;
- not attach custom provenance fields to `QuoteBar`.

If future work needs to distinguish observed quotes from reconstructed quotes, that is a separate metadata/ingestion capability and is not part of SDD2.

---

# 20. Exactly one physical schema per file

Each `.bin` file contains exactly one `PhysicalRecordType` from beginning to end.

Valid:

```text
file A → TradeBarRow
file B → QuoteBarRow
```

Invalid:

```text
one physical file
    TradeBarRow
    QuoteBarRow
    TradeBarRow
```

The fixed-width property is an invariant.

Do not support mixed physical record widths inside one file.

---

# 21. Mixed physical schemas across files

A logical request may combine files with different PRTs only when every participating file has a legitimate projection to the requested logical type.

For `TradeBar`:

```text
TradeBarRow → TradeBar
QuoteBarRow → TradeBar
```

Therefore a logical TradeBar stream may contain both physical schemas.

For `QuoteBar`:

```text
QuoteBarRow → QuoteBar
TradeBarRow ✗ QuoteBar
```

Therefore a logical QuoteBar stream must not include `TradeBarRow` files.

The resolver must operate on semantic compatibility.

Do not require physical homogeneity when the logical projection is valid.

---

# 22. Physical schema declaration in filenames

Support optional explicit schema suffixes.

Canonical tags:

```text
_TB → TradeBarRow
_QB → QuoteBarRow
```

Use them only as physical schema tags.

They do not change the requested logical LEAN type.

The filename parser must remain backward-compatible with legacy files that have no suffix.

Conceptually support:

```text
{index}-{market}_{symbol}_{period}_{from}-{to}.bin
{index}-{market}_{symbol}_{period}_{from}-{to}_TB.bin
{index}-{market}_{symbol}_{period}_{from}-{to}_QB.bin
```

Adapt the exact parser cleanly to the current naming implementation.

Do not force the user to rename existing historical files.

---

# 23. Meaning of `_TB` / `_QB`

When a schema suffix exists:

- it is a contractual physical declaration;
- it must be parsed deterministically;
- it must be checked against the supported market/schema contract;
- it must be checked against file length;
- it avoids heuristic PRT resolution;
- it does not prove that every record value is semantically valid.

If the suffix says `_QB` but the file length cannot be a sequence of `QuoteBarRow` records, fail.

If the suffix says `_TB` but the file length cannot be a sequence of `TradeBarRow` records, fail.

Do not silently reinterpret a declared `_QB` file as `_TB` or vice versa.

---

# 24. Legacy files without schema suffixes

Legacy files remain supported.

Do not introduce a mandatory binary header in SDD2.

Do not introduce a mandatory sidecar manifest in SDD2.

Do not require historical file regeneration.

For an unsuffixed legacy file, resolve the PRT using the deterministic process below.

---

# 25. O(1)-per-file structural PRT resolution

Let:

```text
F = number of candidate files
R = total records eventually consumed
```

Normal startup/schema resolution should be:

```text
O(F) total
O(1) work per file with respect to file record count
O(1) extra memory per file
```

Do not describe global startup as O(1), because all candidate files must at least be enumerated.

The goal is O(1) **per file relative to file size/record count**.

---

# 26. File-length rule

File length may eliminate impossible physical schemas.

Examples:

```text
length % 32 == 0
length % 64 != 0
    ↓
cannot be QuoteBarRow
```

However:

```text
64 = 2 × 32
```

therefore:

```text
length % 32 == 0
length % 64 == 0
```

does **not** identify the schema.

Never use divisibility alone as authoritative PRT detection when more than one candidate remains.

Once the PRT is resolved:

```text
length % recordSize == 0
```

is a required structural invariant.

Then:

```text
recordCount = length / recordSize
```

must use 64-bit-safe arithmetic.

---

# 27. Zero-length files

Preserve the existing ability to pass over empty selected files without terminating the complete logical stream.

For an empty legacy file with no schema suffix:

- no payload exists to identify;
- do not invent a PRT;
- treat it as an empty physical source and continue;
- do not let it block later valid files.

For an empty file with an explicit schema suffix, the declaration may be retained as metadata, but the file still produces zero records.

Tests must cover empty files before and between non-empty files.

---

# 28. Fixed 128-byte ambiguity probe

Use a fixed, bounded probe only when all of the following are true:

```text
more than one legal PRT remains
+
no explicit _TB/_QB declaration
+
file length does not resolve the ambiguity
+
file is non-empty
```

Probe at most:

```text
128 bytes
```

The conceptual comparison is:

```text
4 × TradeBarRow
vs
2 × QuoteBarRow
```

Do not construct LEAN `TradeBar` or `QuoteBar` objects for the probe.

Do not allocate collections proportional to file size.

The probe is schema disambiguation, not whole-file validation.

---

# 29. Probe result policy

The probe may produce only these outcomes:

```text
only TradeBarRow candidate valid
    → TradeBarRow

only QuoteBarRow candidate valid
    → QuoteBarRow

no candidate valid
    → invalid binary format

multiple candidates still valid
    → ambiguous binary format
```

Never guess.

Never select based on "most likely."

Never select based only on requested LEAN type if the physical file remains ambiguous.

If the available bytes are too short to distinguish candidates safely, fail as ambiguous rather than inventing a schema.

---

# 30. Probe validation principles

Keep probe validation conservative.

Use only invariants justified by the binary contract and current producer behavior.

At minimum validate candidate interpretation using properties such as:

- timestamp is finite;
- timestamp converts to supported UTC Unix time;
- candidate timestamps are chronologically plausible;
- numeric fields required by the candidate are finite;
- record boundaries are exact.

Do not make PRT resolution depend on fragile market assumptions that valid data could violate.

Do not use `Reserved` as a price discriminator.

If both candidate interpretations remain valid under conservative rules, the correct result is ambiguity and failure.

---

# 31. No startup full scan

Do not perform a complete record scan before starting the stream.

Do not add:

```text
validate every record first
then read every record again for LEAN
```

as the normal runtime path.

That would add O(R) pre-start work and may force the same historical bytes through the CPU twice.

Full dataset audit belongs to a separate future/offline data-validation workflow.

---

# 32. No distributed sampling scheme

Do not implement startup sampling such as:

```text
first N records
middle M records
last K records
```

as proof of whole-file validity.

Sampling cannot guarantee integrity.

The fixed 128-byte probe has a different purpose:

```text
schema disambiguation only
```

Do not conflate it with corruption auditing.

---

# 33. General preflight vs runtime validation

There are two distinct error fronts.

## 33.1 Preflight/file-level validation

Before stream consumption, validate the selected candidate set structurally:

- filename metadata;
- symbol/market metadata;
- optional schema suffix;
- PRT resolution;
- fixed record size;
- file-length divisibility;
- logical projection compatibility;
- timeframe compatibility;
- deterministic request resolution.

Where practical, inspect all selected candidate files and aggregate deterministic preflight errors before aborting.

The user should not need multiple runs to discover one incompatible file at a time.

Keep diagnostic ordering deterministic.

## 33.2 Runtime/record-level validation

During `MoveNext()`:

```text
read record
    ↓
validate current record
    ↓
project current record
    ↓
emit
```

Deep corruption should fail precisely when the corrupt record is reached.

Do not require pre-reading the whole file.

---

# 34. Runtime record diagnostics

A record-level binary failure must provide enough information to identify the exact source.

Diagnostic context should include, when available:

```text
file path
record index
byte offset
PhysicalRecordType
timestamp
reason
```

For duplicate conflicts also include both contributing file paths and enough field detail to understand the mismatch.

Do not emit huge dumps of raw binary data.

---

# 35. Runtime record validation requirements

At minimum preserve or add explicit checks for:

- finite timestamp;
- supported Unix timestamp range;
- checked conversion to `DateTime`;
- 64-bit-safe record offsets;
- monotonic non-decreasing timestamp order within a file;
- finite numeric fields used by the selected logical projection;
- failure wrapping that reports binary context instead of leaking an opaque cast/overflow error.

Do not silently turn NaN/Infinity/cast failures into zero.

Do not silently skip malformed records.

---

# 36. Memory and hot-loop requirements

The hot binary path must remain streaming.

Do not introduce full-request:

```text
ToList()
OrderBy()
GroupBy()
Distinct()
Dictionary<timestamp, all records>
```

Do not allocate an intermediate `QuoteBar` merely to produce a `TradeBar`.

Do not allocate strings for successful records.

Do not log per-record success messages.

Prefer direct fixed-width reads and checked primitive conversion.

Use `long` for file offsets and record counts.

Memory must remain independent of total historical record count, except for LEAN's normal downstream behavior.

---

# 37. Problem 2 — request authority

The LEAN request remains authoritative for:

```text
RequestedLeanType
RequestedPeriod
SecurityType
Symbol
time window
```

The binary backend must not silently mutate the request to match whatever files happen to exist.

Conceptually:

```text
Resolve(
    RequestedLeanType,
    RequestedPeriod,
    SecurityType,
    AvailableDatasets
)
```

must return a legitimate execution plan such as:

```text
selected files
PhysicalRecordType per file
projection per file
NativePeriod
requested/logical period
consolidation requirement
```

or fail.

---

# 38. Request-resolution order

Use the following conceptual sequence:

```text
LEAN request
    ↓
1. identify symbol / market / time range
    ↓
2. discover candidate files
    ↓
3. parse metadata
    ↓
4. resolve PRT for every participating file
    ↓
5. verify PRT → logical type compatibility
    ↓
6. group candidates by native period
    ↓
7. select native period
    ↓
8. determine consolidation
    ↓
9. build chronological raw logical stream
```

Do not select a physical reader before PRT resolution.

---

# 39. Native timeframe selection policy

The official policy is:

```text
1. exact native period wins
2. otherwise choose the largest native period
   that is smaller than the requested period
   and exactly divides the requested period
```

Example:

```text
available:
5M
15M

requested:
30M

selected:
15M
```

Not:

```text
5M
```

The purpose is to reduce:

- records read;
- `MoveNext()` calls;
- primitive parsing;
- LEAN object allocations;
- consolidation work.

Correctness still has priority over fewer records.

---

# 40. Aggregation-equivalence invariant

Using the nearest compatible native period is valid only for a dataset family whose timeframes obey the same aggregation semantics.

Conceptually:

```text
Aggregate(5M → 30M)
==
Aggregate(15M → 30M)
==
native 30M
```

within the data contract's precision and gap rules.

Required semantic compatibility includes:

- aligned temporal boundaries;
- same UTC basis;
- same logical price source;
- compatible OHLC behavior;
- compatible volume behavior;
- compatible gap treatment;
- compatible Bid/Ask policy.

Add generated tests for this invariant.

Where the local real Auroboros dataset contains multiple equivalent timeframes, add a practical characterization.

If real-data characterization disproves the assumption for the current supported dataset family, do not hide the mismatch. Fail the relevant characterization and document the blocker in the Action Results.

Do not silently choose a faster source that changes logical output.

---

# 41. Finer-than-native requests

Example:

```text
smallest available native period = 5M
requested period                 = 1M
```

Required behavior:

```text
REJECT
```

Do not:

```text
return 5M while LEAN believes it requested 1M
```

Do not:

```text
fabricate 1M bars from a 5M bar
```

Do not silently change `SubscriptionDataConfig`.

The failure must clearly state:

- requested period;
- available native periods;
- symbol;
- logical type;
- reason.

---

# 42. Non-divisible period requests

Example:

```text
native  = 5M
request = 7M
```

Required behavior:

```text
REJECT
```

A 5-minute physical bar can cross a 7-minute logical boundary and cannot be split without inventing information.

Do not create partial synthetic source bars to satisfy such a request.

---

# 43. Exact period behavior

If an exact compatible native period exists:

```text
requested == native
```

use it.

Do not choose a finer source merely because it also divides the request.

This reduces work while preserving exact dataset semantics.

---

# 44. Consolidation behavior

When:

```text
NativePeriod < RequestedPeriod
```

and:

```text
RequestedPeriod % NativePeriod == 0
```

consolidation is permitted.

Prefer current native LEAN consolidation machinery if there is a clean current seam that preserves the required behavior.

If the current LEAN architecture does not expose a suitable raw-source consolidation seam, keep the custom consolidator:

- forward-only;
- bounded;
- single-bucket;
- no whole-stream sorting;
- no whole-stream buffering;
- consistent between DataFeed and History.

Document in the Action Results why the chosen mechanism is the smallest correct integration with current LEAN.

Do not create a second general consolidation framework.

---

# 45. Consolidation semantics

For `TradeBar`:

```text
Open   = first Open
High   = max High
Low    = min Low
Close  = last Close
Volume = sum Volume
Time   = first logical Time
EndTime = requested bucket end
Period = requested bucket span
Symbol = request Symbol
```

For `QuoteBar`:

```text
Bid.Open   = first Bid.Open
Bid.High   = max Bid.High
Bid.Low    = min Bid.Low
Bid.Close  = last Bid.Close

Ask.Open   = first Ask.Open
Ask.High   = max Ask.High
Ask.Low    = min Ask.Low
Ask.Close  = last Ask.Close

Value      = resulting QuoteBar.Close using vanilla semantics
Symbol     = request Symbol
```

Do not restore global OHLCV close as QuoteBar.Value during consolidation.

---

# 46. Problem 3 — stream integrity

The logical stream observed by LEAN must preserve:

```text
chronological order
completeness
uniqueness
deterministic conflict handling
correct temporal semantics
```

across:

- file boundaries;
- overlapping files;
- mixed PRT files;
- History;
- DataFeed;
- warmup;
- fill-forward;
- timezone conversion;
- DST transitions;
- early disposal.

---

# 47. Remove first-file-wins as overlap policy

The previous logic conceptually did:

```text
if timestamp <= lastEmittedTime
    skip
```

That is not sufficient.

Example:

```text
file A:
00:00 00:05       00:15

file B:
      00:05 00:10 00:15
```

Reading all of A before B and skipping older timestamps loses:

```text
00:10
```

SDD2 must remove that correctness dependency.

---

# 48. Fast path for non-overlapping files

Do not force every normal sequential dataset through a general k-way merge.

After deterministic metadata ordering, detect connected overlap groups.

For a group with:

```text
K = 1
```

use the simple sequential reader path.

Desired behavior:

```text
no overlap
    ↓
one active physical source
    ↓
forward-only read
```

No priority queue is necessary.

This is the common fast path.

---

# 49. Bounded chronological merge for overlap groups

For an overlap group with:

```text
K > 1
```

use a bounded chronological merge.

Conceptually:

```text
file A ──┐
file B ──┼──→ projected logical records ──→ chronological merge
file C ──┘
```

Maintain only the next required logical record/cursor state from each active source.

Do not load complete files.

Expected complexity:

```text
time   O(R log K)
memory O(K)
```

where `R` is the number of records consumed from the overlap group.

For `K = 1`, remain on the sequential fast path.

---

# 50. Merge comparison clock

Perform cross-file chronology, duplicate grouping, and conflict comparison on a canonical UTC timestamp.

The physical ABI is UTC.

Do not compare one source in UTC and another after exchange-time conversion.

Fix chronology first.

Then pass the resulting logical stream through the time normalization layer required by current LEAN.

---

# 51. Mixed PRT overlap merge

Projection occurs before logical duplicate comparison.

Example:

```text
file A
TradeBarRow
    ↓
TradeBar

file B
QuoteBarRow
    ↓
TradeBar
```

If both represent the same timestamp in a TradeBar request, compare:

```text
TradeBar
vs
TradeBar
```

Do not compare the physical bytes.

Different physical layouts may legitimately produce the same logical value.

---

# 52. Duplicate timestamp policy

When two pending records have the same logical timestamp, evaluate logical equivalence.

## 52.1 Equivalent duplicate

If:

```text
same timestamp
+
same logical projected data
```

emit exactly one logical record.

Advance every source that contributed that equivalent timestamp before selecting the next emitted timestamp.

This prevents the same timestamp from being reconsidered repeatedly.

## 52.2 Conflicting duplicate

If:

```text
same timestamp
+
different logical projected data
```

fail with a data conflict.

Do not use:

```text
first-file-wins
last-file-wins
lexicographically-smallest-path-wins
```

for conflicting prices.

Filename order must not choose the price used by a backtest.

---

# 53. Logical duplicate equality

Compare only fields that belong to the requested logical representation.

For a `TradeBar` logical stream, compare at least:

- timestamp/time;
- period;
- Open;
- High;
- Low;
- Close;
- Volume;
- Symbol identity where relevant.

For a `QuoteBar` logical stream, compare at least:

- timestamp/time;
- period;
- Bid OHLC;
- Ask OHLC;
- logical QuoteBar close/value semantics;
- Symbol identity where relevant.

Do not treat differences in unused physical fields as a logical conflict.

Example:

If a `QuoteBarRow` is projected to `QuoteBar`, global OHLCV fields that are not part of the QuoteBar projection do not independently determine logical equality.

---

# 54. Intra-file chronology

Each file remains responsible for its own timestamp order.

Required invariant:

```text
T[n+1] >= T[n]
```

If:

```text
T[n+1] < T[n]
```

the file is invalid.

Do not use the cross-file merge to repair a physically unordered file.

Equal consecutive timestamps may be processed through the same logical duplicate/conflict policy.

---

# 55. Overlap group construction

Use metadata intervals to avoid opening unrelated files simultaneously.

Sort deterministically by:

```text
FromUtc
ToUtc
path
```

Partition the selected files into connected temporal-overlap groups.

A non-overlap group should not keep future files mapped unnecessarily.

An overlap group should map/open only the sources necessary for that group.

Do not keep every file in a multi-year request open just because a later group may overlap.

---

# 56. Resource ownership in merged streams

Every physical cursor must have deterministic ownership.

Disposal must be correct for:

- normal end-of-file;
- moving to the next sequential file;
- completion of an overlap group;
- early History enumeration termination;
- subscription cancellation;
- DataFeed shutdown;
- thrown record validation error;
- thrown duplicate conflict;
- thrown projection error.

An exception in one cursor must dispose the remaining active overlap cursors.

Do not leak acquired MMF pointers or view accessors.

---

# 57. Shared raw semantics for History and DataFeed

Both:

```text
BinaryDataFeed
```

and:

```text
BinaryHistoryProvider
```

must consume the same fundamental binary source semantics:

```text
discovery
    ↓
PRT resolution
    ↓
projection
    ↓
chronology
    ↓
overlap merge
    ↓
dedupe/conflict policy
```

Do not create one merge policy for History and another for DataFeed.

Equivalent requests should not receive different raw prices because they entered through different engine paths.

---

# 58. Preserve native LEAN lifecycle ownership

The custom binary layer should own only responsibilities that are specific to reading the binary source.

Where current LEAN already owns behavior, continue to use LEAN's lifecycle.

Preserve native behavior for:

- warmup;
- `LastPointTracker`;
- fill-forward;
- quote fill-forward;
- exchange hours;
- strict daily end times;
- subscription filters;
- schedules;
- universe routing;
- Slice synchronization;
- map/factor processing where applicable;
- subscription lifetime.

Do not move these responsibilities into the physical record readers.

---

# 59. BinaryDataFeed requirement

`BinaryDataFeed` should remain a narrow specialization of current `FileSystemDataFeed`.

Its primary difference should be:

```text
how the underlying raw market-data enumerator is created
```

Do not copy the full current `FileSystemDataFeed` implementation into `BinaryDataFeed`.

Do not reintroduce private duplicate implementations of warmup/fill-forward/schedule handling.

If the existing protected seam added by SDD1 is still sufficient, use it without expanding upstream modifications.

---

# 60. BinaryHistoryProvider requirement

`BinaryHistoryProvider` should remain a narrow specialization of current `SubscriptionDataReaderHistoryProvider`.

Its primary difference should be:

```text
how the raw historical market-data reader is created
```

Preserve the native wrapper order after the raw binary source.

Do not duplicate the whole history provider.

If the existing protected raw-reader seam added by SDD1 is sufficient, do not widen it.

---

# 61. Upstream seam minimization

SDD1 added small extension seams to native LEAN classes.

Review those seams against current official LEAN.

Required goal:

```text
smallest custom surface
```

Do not expand upstream-native modifications unless SDD2 cannot be implemented cleanly otherwise.

If a native LEAN file must change:

1. explain why the existing seam is insufficient;
2. keep the change minimal;
3. preserve vanilla behavior for non-binary users;
4. add regression coverage;
5. document the justification in the Action Results.

---

# 62. Timezone semantics

Physical timestamps remain UTC Unix seconds.

Cross-file chronology and dedupe happen in UTC.

After the raw logical stream is chronologically correct, use the current LEAN time normalization path expected by downstream enumerators.

Do not create a custom DST table.

Do not hardcode timezone offsets.

Use NodaTime/LEAN conversion utilities already present in the engine.

History and DataFeed must apply compatible semantics.

---

# 63. DST requirements

Add explicit tests for both kinds of daylight-saving transition in a timezone where they occur, preferably a currently relevant LEAN timezone such as New York if compatible with the tested request.

Cover:

```text
DST forward transition
DST backward transition
```

Validate:

- chronological output;
- no accidental duplicate UTC source record;
- no lost source record;
- correct local `Time` / `EndTime`;
- downstream filtering remains correct.

Do not treat repeated local clock labels during DST fallback as duplicate physical UTC records.

Duplicate identity for binary-source merge remains based on canonical UTC timestamp.

---

# 64. Warmup → normal transition

Add a binary-backend end-to-end test that explicitly crosses:

```text
warmup
    ↓
normal data
```

Verify:

- no duplicate transition point;
- no missing first normal point;
- no backward time movement;
- correct fill-forward behavior where enabled;
- `LastPointTracker` behavior remains native;
- no binary reader global state leaks from warmup into normal execution.

Do not solve the boundary with a custom global static last timestamp.

Let the native LEAN lifecycle own the warmup boundary.

---

# 65. Fill-forward ordering

The conceptual order must remain:

```text
physical binary records
    ↓
physical validation
    ↓
logical projection
    ↓
chronological merge / logical dedupe
    ↓
correct raw logical stream
    ↓
LEAN time/lifecycle wrappers
    ↓
fill-forward
```

The binary reader must not manufacture fill-forward records directly from the file.

---

# 66. Universe requests

Universe subscriptions are not binary Forex market bars.

Preserve current `FileSystemDataFeed` universe behavior.

Do not route:

```text
FundamentalUniverse
UserDefinedUniverse
TimeTriggeredUniverse
```

through `TradeBarRow` or `QuoteBarRow` readers unless existing current code explicitly and correctly requires it.

SDD2 should not broaden binary market-data scope into universe infrastructure.

---

# 67. Symbol identity

Continue preserving the exact `Symbol` supplied by LEAN.

Do not recreate symbols using:

```text
Symbol.Create(... Market.Oanda ...)
```

inside the physical reader.

The reader must not replace:

- market;
- security type;
- SID;
- mapping identity;
- canonical identity.

Any current hardcoded market construction in the binary hot path must be treated as a bug.

---

# 68. Error policy

Fail explicitly for:

- unsupported SecurityType;
- unsupported logical data type;
- malformed filename metadata;
- contradictory explicit PRT suffix;
- unresolved ambiguous PRT;
- invalid file-length/record-size relationship;
- PRT incompatible with requested logical type;
- request finer than available native period;
- non-divisible period relation;
- decreasing timestamp inside a file;
- invalid timestamp;
- non-finite required numeric field;
- logical duplicate conflict;
- projection failure;
- impossible stream invariant.

Do not silently:

- choose another physical schema;
- choose another requested type;
- change resolution;
- skip corrupt records;
- substitute a conflicting duplicate;
- fabricate data.

---

# 69. Preflight error aggregation

Where errors are discoverable from metadata/constant probes before streaming, inspect all selected candidate files and report the set deterministically before aborting.

Good examples:

- multiple incompatible suffixes;
- multiple malformed lengths;
- multiple ambiguous legacy files;
- multiple physical-to-logical incompatibilities.

This is an O(F) metadata/preflight pass.

Do not turn this into an O(R) full-data pass.

Runtime data corruption discovered only while streaming should still fail at the exact record where it is encountered.

---

# 70. Logging and diagnostic style

Follow current LEAN logging/error conventions.

Do not invent a noisy parallel custom logging framework.

Use concise deterministic messages.

For binary diagnostics include high-value context:

```text
symbol
requested type
requested period
native period
file path
PRT
record index
byte offset
timestamp
reason
```

Only include fields relevant to the failure.

Do not log every successful record.

---

# 71. Implementation style

Prefer:

- current LEAN abstractions;
- small types with one responsibility;
- forward-only enumerators;
- deterministic behavior;
- immutable metadata where practical;
- bounded memory;
- explicit disposal ownership;
- direct primitive parsing;
- clear error messages;
- focused tests.

Avoid:

- generic plugin frameworks;
- dependency-injection redesign;
- reflection-based schema discovery;
- full-history caches;
- new global mutable state;
- hidden configuration;
- duplicated vanilla LEAN lifecycle code;
- one thread per file;
- new background worker subsystems;
- premature microservices;
- broad unrelated refactors.

---

# 72. Suggested internal decomposition

The exact type names may adapt to current code, but a maintainable implementation will likely have responsibilities equivalent to:

```text
BinaryFileResolver
    filesystem discovery
    filename metadata
    optional PRT tag parsing
    timeframe metadata
    overlap metadata grouping

PhysicalRecordType / schema helper
    record size
    supported projections
    constant-time schema validation/probe

physical file cursor
    MMF/view ownership
    fixed-width read
    per-file chronology
    record-level validation

projection layer
    TradeBarRow → TradeBar
    QuoteBarRow → TradeBar
    QuoteBarRow → QuoteBar

logical stream combiner
    sequential fast path
    overlap merge path
    logical duplicate equality/conflict detection

BinarySubscriptionEnumeratorFactory
    request resolution
    source composition
    shared History/DataFeed raw semantics
```

Do not force this exact class count if a simpler current-code arrangement is clearer.

Preserve the responsibility boundaries.

---

# 73. Files allowed to change

Primary expected source files:

```text
Engine/DataFeeds/BinaryFileResolver.cs
Engine/DataFeeds/BinaryTradeBarSubscriptionReader.cs
Engine/DataFeeds/BinaryQuoteBarSubscriptionReader.cs
Engine/DataFeeds/Enumerators/Factories/BinarySubscriptionEnumeratorFactory.cs
Engine/DataFeeds/BinaryDataFeed.cs
Engine/HistoricalData/BinaryHistoryProvider.cs
```

Primary expected tests:

```text
Tests/Engine/DataFeeds/BinaryFileResolverTests.cs
Tests/Engine/DataFeeds/BinarySubscriptionEnumeratorFactoryTests.cs
Tests/Engine/DataFeeds/BinaryRealDataTests.cs
Tests/Engine/HistoricalData/BinaryHistoryProviderTests.cs
```

New narrowly scoped Auroboros binary source/test files are allowed if they materially improve responsibility separation.

The repository-root report file is required:

```text
SDD2-CODEX-Chat-Action results.md
```

---

# 74. Files conditionally allowed to change

These native LEAN seam files may change **only if strictly required**:

```text
Engine/DataFeeds/FileSystemDataFeed.cs
Engine/HistoricalData/SubscriptionDataReaderHistoryProvider.cs
```

Prefer no new changes if the SDD1 seams already support the implementation.

If changed, document why.

Tests for their native behavior must still pass.

---

# 75. Files not to change for feature semantics

Do not modify these merely to make the custom source fit:

```text
Common/Data/Market/QuoteBar.cs
Common/Data/Market/TradeBar.cs
core portfolio logic
order logic
risk logic
strategy algorithms
optimizer algorithms
unrelated Launcher behavior
unrelated configuration system
```

Do not modify Data Engineering in this SDD.

Do not rewrite the historical binary dataset.

Do not alter the binary producer unless the user separately requests it.

---

# 76. Required test strategy

Run focused tests first.

Then run broader relevant native LEAN regression tests.

Then run real-data characterization if the local data exists.

Every test report must distinguish:

```text
passed
failed because of SDD2 regression
pre-existing failure
environment/test-host failure
missing local data
not executed
```

Never report an unexecuted test as passing.

---

# 77. Problem 1 unit-test matrix

Add or update tests for:

```text
legacy filename without PRT suffix
filename with _TB
filename with _QB
invalid PRT suffix
suffix parsed without changing logical requested type

TradeBarRow fixed 32-byte ABI
QuoteBarRow fixed 64-byte ABI
QuoteBarRow bytes 60..63 represented as Reserved
Spread is not part of the QuoteBarRow reader contract

file length valid for only TradeBarRow
file length valid for only QuoteBarRow
file length ambiguous for both
explicit suffix resolves ambiguity
128-byte probe resolves TradeBarRow
128-byte probe resolves QuoteBarRow
128-byte probe rejects invalid payload
128-byte probe rejects unresolved ambiguity
empty legacy file does not terminate later stream

TradeBarRow → TradeBar
QuoteBarRow → TradeBar
QuoteBarRow → QuoteBar
TradeBarRow → QuoteBar fails

mixed TradeBarRow/QuoteBarRow → TradeBar
mixed set containing TradeBarRow → QuoteBar fails

QuoteBar.Value == QuoteBar.Close
QuoteBar.Value does not use global record.Close
Reserved does not affect projection

invalid timestamp diagnostics
non-finite required field diagnostics
record index and byte offset included in deep runtime failure
```

---

# 78. Problem 2 unit-test matrix

Required coverage:

```text
exact native period wins

available 5M + 15M
request 30M
select 15M

available 5M
request 30M
select 5M and consolidate

available 5M
request 1M
fail

available 5M
request 7M
fail

available 5M + 15M + 1H
request 1H
exact 1H wins

explicit Forex QuoteBar request resolves only compatible QuoteBar projection
explicit TradeBar request may use QuoteBarRow projection
request is not silently rewritten

aggregation-equivalence generated characterization
multi-timeframe real-data characterization when data exists
```

Update old SDD1 tests that intentionally expected the now-rejected behavior.

In particular, any test that expects a finer request to receive a coarser native period must be changed to expect explicit failure.

Any test that expects the smallest compatible divisor instead of the largest compatible divisor must be changed.

---

# 79. Problem 3 stream-test matrix

Required coverage:

```text
single file
multiple sequential files
empty file between valid files

file boundary without overlap

identical overlap
    → one output per timestamp

complementary overlap
    → no valid timestamp lost

conflicting overlap
    → explicit data conflict

three or more overlapping files
    → chronological merged output

mixed PRT overlap projected to TradeBar
    → compare logical TradeBar values

decreasing timestamp inside one file
    → fail

equal same-file timestamp with equal logical data
    → dedupe

equal same-file timestamp with conflicting logical data
    → fail

early disposal
exception disposal
file transition disposal
overlap-group disposal
```

---

# 80. History test matrix

Test:

- QuoteBar history;
- TradeBar history from `TradeBarRow`;
- TradeBar history projected from `QuoteBarRow`;
- mixed-PRT TradeBar history;
- exact request window;
- `[start, end)` behavior;
- consolidation;
- unsupported finer request;
- non-divisible request;
- fill-forward;
- non-fill-forward;
- parallel History path if currently supported by the base provider;
- synchronous History path;
- timezone conversion;
- DST forward;
- DST backward;
- overlap semantics;
- early enumeration termination.

History must not have a separate raw duplicate policy.

---

# 81. DataFeed test matrix

Test:

- TradeBar request;
- QuoteBar request;
- request type controls logical projection, not physical schema;
- warmup;
- changed warmup resolution where supported;
- warmup → normal transition;
- no duplicate transition point;
- no gap at transition;
- fill-forward;
- quote fill-forward;
- filters;
- schedule/universe routing remains native;
- no binary routing for universe types;
- overlap stream behavior through the feed;
- disposal at feed shutdown.

If the full native `FileSystemDataFeedTests` fixture is still affected by the previously observed Python.NET/GIL host issue, report the exact observed failure separately from SDD2 assertions.

Do not suppress a real SDD2 regression by attributing it to the environment without evidence.

---

# 82. Native LEAN regression coverage

Run the relevant existing tests around:

```text
FileSystemDataFeed
SubscriptionDataReaderHistoryProvider
warmup
fill-forward
map files
strict daily end times
subscription filtering
```

The custom seams must not break ordinary non-binary LEAN behavior.

---

# 83. Real-data characterization

Use the existing local Auroboros binary historical data under:

```text
Data/historical_data
```

if available.

Do not modify those files.

Prefer small deterministic ranges first.

For the available real Forex dataset, validate at least:

- records are emitted;
- timestamps are strictly chronological after logical dedupe;
- Symbol identity is preserved;
- QuoteBar Bid and Ask are non-null where the contract expects them;
- `QuoteBar.Value == QuoteBar.Close`;
- global close is not incorrectly forced into `Value`;
- multiple physical files can be crossed;
- no duplicate output timestamp is observed;
- no obvious gap is introduced by overlap handling;
- memory does not grow with retained full-history objects.

If multiple native timeframes are locally available, characterize aggregation equivalence.

---

# 84. Real TradeBar file availability

SDD1 had no real producer-generated TradeBar binary file available.

Do not fabricate a claim that a real TradeBar producer file was validated.

If a real file is now available:

- validate it end-to-end;
- report its source/path category without committing the data.

If it is not available:

- retain generated ABI tests;
- clearly state this limitation in the Action Results.

This does not invalidate `QuoteBarRow → TradeBar`, which can be exercised using the real QuoteBar-row dataset plus generated focused cases.

---

# 85. Performance requirements inside SDD2

Problem 4 benchmarking is deferred, but SDD2 must not introduce obvious performance regressions.

Required structural properties:

```text
no full-history materialization
no mandatory full-file pre-scan
O(1) PRT resolution per file with respect to record count
O(1) normal sequential reader state
O(K) overlap merge state
O(R) sequential stream work
O(R log K) only inside overlap groups
```

The common no-overlap path should remain simple.

Do not optimize away correctness.

Do not add parallel file-validation complexity unless current profiling already proves it necessary.

---

# 86. No numeric speed claims in SDD2

Do not claim:

```text
5x
7x
10x
fastest in the world
```

No apples-to-apples old-vs-new benchmark is part of this implementation pass.

Existing runtime timings may be recorded as characterization only.

Do not interpret them as the final comparative benchmark.

---

# 87. Build and static validation

Before implementation, if the working environment allows it, establish a baseline build or targeted baseline tests.

After implementation run, at minimum, the current repository equivalents of:

```bash
git diff --check
```

and build the affected solution/projects.

Prefer:

```bash
dotnet build QuantConnect.Lean.sln --no-restore -c Release
dotnet build Engine/QuantConnect.Lean.Engine.csproj --no-restore -c Release
dotnet build Tests/QuantConnect.Tests.csproj --no-restore -c Release
```

Adapt path separators and flags to the actual environment.

If restore state prevents `--no-restore`, report what was required rather than hiding it.

---

# 88. Targeted test execution

Run the binary-focused tests after building.

Use the actual test runner syntax supported by the repository.

Target, at minimum, the fixtures equivalent to:

```text
BinaryFileResolverTests
BinarySubscriptionEnumeratorFactoryTests
BinaryHistoryProviderTests
BinaryRealDataTests
```

Then run the relevant native regression subsets.

Do not run an enormous unrelated test matrix merely for appearance if it provides no useful signal.

Do run enough native coverage to protect the two integration seams.

---

# 89. Runtime validation

If the local real data is available, execute at least one short deterministic binary-backed runtime/backtest path after the tests.

Validate data behavior, not profitability.

Check:

```text
warmup completes
History returns expected logical type
OnData receives expected logical type
clock advances
no unexpected duplicate timestamps
no obvious missing boundary records
correct QuoteBar Value semantics
clean process exit
```

Run a longer range only if useful and practical after the short run passes.

---

# 90. Search-based final review

Before stopping, inspect the affected runtime path for regression patterns.

At minimum search for relevant matches of:

```text
BinaryDataLoader
BarDataMode
bar-type
Market.Oanda
Value = (decimal)record.Close
Spread
ToList(
OrderBy(
GroupBy(
first-file-wins
```

Not every repository-wide match is automatically wrong.

Review each match in the binary runtime path manually.

Also verify that no newly created PRT type name includes `32` or `64`.

---

# 91. Required implementation outcome — Problem 1

Problem 1 is complete only when all applicable statements are true:

- physical schema and requested LEAN type are separate concepts in code;
- `TradeBarRow` and `QuoteBarRow` exist as physical schema concepts without numeric suffixes;
- record sizes are explicit in the ABI, not type names;
- legacy unsuffixed files remain supported;
- `_TB` and `_QB` are optional explicit physical declarations;
- ambiguous length is not resolved by divisibility alone;
- the bounded 128-byte probe resolves only when one candidate is valid;
- ambiguity fails instead of guessing;
- preflight remains O(1) per file relative to record count;
- no mandatory full scan was introduced;
- `QuoteBarRow` ends with `Reserved`, not `Spread`;
- `QuoteBarRow → TradeBar` uses global OHLCV;
- `QuoteBarRow → QuoteBar` uses Bid/Ask;
- `QuoteBar.Value` follows vanilla `QuoteBar.Close`;
- `TradeBarRow → QuoteBar` is rejected;
- runtime corruption errors identify exact source context.

---

# 92. Required implementation outcome — Problem 2

Problem 2 is complete only when:

- request semantics remain authoritative;
- exact native period wins;
- otherwise the largest smaller exact divisor wins;
- request finer than all available data fails;
- non-divisible period relation fails;
- no coarser source is silently exposed as a finer request;
- mixed PRT files may satisfy TradeBar when both projections are valid;
- incompatible PRT files cannot satisfy QuoteBar;
- consolidation remains streaming;
- aggregation-equivalence tests exist;
- no hidden global `bar-type`/`timeframe` source of truth returns.

---

# 93. Required implementation outcome — Problem 3

Problem 3 is complete only when:

- non-overlap files use the sequential fast path;
- temporal overlap groups use bounded chronological merge;
- complementary overlap cannot lose an older missing timestamp;
- identical logical duplicates emit once;
- conflicting logical duplicates fail;
- physical filename order cannot choose a conflicting price;
- mixed PRT duplicate comparison occurs after projection;
- per-file decreasing timestamps fail;
- merge state is O(K), not O(total records);
- History and DataFeed share the same raw source semantics;
- warmup → normal has dedicated binary-backend coverage;
- fill-forward remains native;
- DST forward/backward are covered;
- resource disposal remains deterministic.

---

# 94. Problem 4 — deferred, informational only

Do not implement this section.

After Problems 1–3 are complete, inspect the final code and provide an engineering diagnosis of the proposed next phase.

The currently proposed Problem 4 plan is:

```text
1. synchronize fork/master with current official upstream at a controlled stabilization point
2. rebase/position auroboros on that master
3. run baseline suite
4. add direct fixed-width timestamp binary search for deep-in-file requests
5. do not add a persistent index initially
6. run the complete correctness suite
7. execute an apples-to-apples old BinaryDataLoader vs new streaming benchmark
8. quantify time-to-first-bar, elapsed time, heap, allocations, GC, working set, private bytes,
   and optional page faults / I/O / CPU time
9. make no predetermined speed claim
10. clean temporary documentation
11. fixup/squash the temporary patch stack into a maintainable final Auroboros patch
```

Codex must **not** execute these changes during SDD2.

---

# 95. Required Problem 4 diagnosis

In the SDD2 Action Results, include a dedicated section:

```text
Problem 4 — Deferred Engineering Diagnosis
```

Evaluate:

- whether direct binary search is still the correct first seek optimization;
- whether fixed-width records and monotonic timestamps are sufficient;
- whether the seek must start before `StartUtc` for correct consolidation;
- whether a persistent index is still unnecessary;
- whether overlap groups alter the seek design;
- how to benchmark old vs new fairly;
- whether QuoteBar is the cleanest old-vs-new benchmark baseline;
- how to avoid filesystem-cache bias;
- whether upstream changes since the current fork affect the two native seams;
- what should be cleaned/squashed later;
- any improvement you recommend to the proposed Problem 4 sequence.

Do not write Problem 4 code.

Do not rebase.

Do not benchmark old vs new in this task.

The output is analysis only.

---

# 96. Deferred old-vs-new benchmark guidance

For the future Problem 4 diagnosis, reason about the benchmark using this target design:

```text
OLD
previous BinaryDataLoader-based implementation

vs

NEW
final post-SDD2 streaming implementation
```

Keep constant:

```text
same real dataset
same symbol
same date range
same logical QuoteBar workload where possible
same machine
same build configuration
same consumer
```

The consumer should not retain all bars in a list.

It should consume and discard while computing a deterministic count/checksum.

Measure at least:

```text
time-to-first-bar
total elapsed time
records per second
CPU time
peak managed heap
allocated bytes
GC Gen0/1/2
working set
private bytes
```

Optional:

```text
page faults
bytes read
```

The future benchmark should distinguish warm filesystem cache from cold-cache conditions where practical.

Do not implement this benchmark now.

---

# 97. Required Action Results file

Create this file in the repository root:

`SDD2-CODEX-Chat-Action results.md`

It must be detailed enough that the user does not need the full Codex chat transcript to understand what happened.

The report must be written in English.

---

# 98. Action Results required structure

Use at least the following sections.

## 1. Repository state

Report:

- local repository path;
- branch;
- starting HEAD;
- ending working-tree HEAD;
- fork master reference;
- upstream reference inspected;
- branch divergence;
- pre-existing working-tree changes;
- confirmation that no commit/push/rebase/merge was performed.

## 2. SDD1 baseline audit

Explain:

- what the previous commit implemented;
- what was preserved;
- what SDD2 intentionally changed.

Include the actual execution-time commit SHAs.

## 3. Final architecture

Show the final DataFeed and History paths.

Example:

```text
BinaryDataFeed
  → shared binary request/source factory
  → file/PRT resolver
  → physical cursors
  → projection
  → sequential fast path OR overlap merge
  → LEAN time/lifecycle wrappers
  → Subscription
```

and equivalent History path.

## 4. Problem 1 implementation

Document:

- PRT model;
- filename tags;
- legacy fallback;
- 128-byte probe;
- ABI;
- Reserved;
- projection matrix;
- QuoteBar.Value;
- validation/error behavior.

## 5. Problem 2 implementation

Document:

- exact/native period policy;
- nearest exact divisor policy;
- finer request rejection;
- non-divisible rejection;
- mixed PRT behavior;
- consolidation choice;
- aggregation-equivalence findings.

## 6. Problem 3 implementation

Document:

- sequential fast path;
- overlap group detection;
- merge algorithm;
- duplicate equality;
- conflict errors;
- lifecycle integration;
- DST;
- warmup;
- disposal.

## 7. Files changed

For every changed file:

```text
path
why it changed
main behavioral change
```

## 8. Files added

Explain every new source/test/report file.

## 9. Files removed

Explain every deletion.

Do not delete files merely to simplify the report.

## 10. Tests

For every executed command provide:

```text
command
result
pass/fail/skip counts when available
```

State explicitly when a test was not executed.

## 11. Real-data validation

State:

- dataset used;
- logical type;
- date/range;
- output count where useful;
- whether multiple physical files were crossed;
- QuoteBar.Value behavior;
- overlap observations;
- real TradeBar availability.

## 12. Runtime/memory observations

Record actual observations only.

Do not imply a comparative benchmark occurred.

## 13. Remaining risks

Be specific.

## 14. Problem 4 — Deferred Engineering Diagnosis

Provide the required analysis without implementing it.

## 15. Git diff

Include:

```text
git status --short
git diff --stat
git diff --check
```

Explain how the user can inspect the full diff.

## 16. Completion checklist

Mark every SDD2 acceptance item as:

```text
PASS
FAIL
NOT APPLICABLE
BLOCKED
```

with a short explanation for anything not PASS.

---

# 99. Final chat response from Codex

Do not paste the entire engineering report into chat.

The final chat response should be compact and include:

- whether Problems 1–3 were implemented successfully;
- whether the required build/tests passed;
- whether any blocker remains;
- confirmation that Problem 4 was not implemented;
- the path:

`SDD2-CODEX-Chat-Action results.md`

Example shape:

```text
SDD2 implementation is complete. Problems 1–3 were implemented and validated; Problem 4 was analyzed only and left untouched as required. No commit or push was performed.

Full engineering report:
SDD2-CODEX-Chat-Action results.md
```

Add one short blocker sentence if the result is not fully successful.

---

# 100. Non-goals

This SDD is not:

- a rewrite of LEAN's data architecture;
- a contribution upstream;
- a live-trading redesign;
- a new data-engineering download job;
- a new binary format version with headers;
- a persistent index project;
- a shared-memory project;
- an IPC project;
- an optimization-engine redesign;
- a strategy logic change;
- a portfolio/risk change;
- a benchmark task;
- an upstream synchronization task;
- a final Git-history cleanup task.

---

# 101. Completion checklist

Do not stop until every applicable item below has been addressed.

## Repository/audit

- [ ] Current branch and working tree inspected.
- [ ] Current HEAD and parent identified.
- [ ] SDD1 diff inspected.
- [ ] Fork master and official upstream inspected read-only.
- [ ] Current vanilla LEAN lifecycle seams inspected.
- [ ] User changes preserved.
- [ ] No commit, stage, push, merge, or rebase performed.

## Problem 1

- [ ] `PhysicalRecordType` separated from requested LEAN type.
- [ ] Type names are `TradeBarRow` / `QuoteBarRow`, without size suffixes.
- [ ] TradeBarRow ABI is 32 bytes.
- [ ] QuoteBarRow ABI is 64 bytes.
- [ ] QuoteBarRow final field is `Reserved`, not `Spread`.
- [ ] `_TB` / `_QB` optional suffix parsing implemented.
- [ ] Legacy filenames remain supported.
- [ ] File length is not used as ambiguous authoritative detection.
- [ ] Fixed 128-byte probe implemented only when required.
- [ ] Ambiguous probe fails.
- [ ] No startup full scan added.
- [ ] No distributed integrity sampling added.
- [ ] `TradeBarRow → TradeBar` works.
- [ ] `QuoteBarRow → TradeBar` works.
- [ ] `QuoteBarRow → QuoteBar` works.
- [ ] `TradeBarRow → QuoteBar` fails.
- [ ] QuoteBar.Value follows vanilla Close semantics.
- [ ] Runtime errors include exact binary context.

## Problem 2

- [ ] Request remains authoritative.
- [ ] Exact period wins.
- [ ] Largest compatible smaller divisor wins.
- [ ] Finer-than-native request fails.
- [ ] Non-divisible request fails.
- [ ] Mixed PRT TradeBar stream works.
- [ ] Incompatible QuoteBar physical source fails.
- [ ] Consolidation remains bounded/streaming.
- [ ] Aggregation-equivalence tests exist.
- [ ] Existing old-policy tests updated.

## Problem 3

- [ ] Sequential no-overlap fast path exists.
- [ ] Overlap groups are detected.
- [ ] Overlap merge is bounded.
- [ ] Complementary overlap preserves missing timestamps.
- [ ] Equivalent duplicates dedupe.
- [ ] Conflicting duplicates fail.
- [ ] Mixed PRT conflicts compare logical projection.
- [ ] Per-file decreasing timestamps fail.
- [ ] History/DataFeed share raw semantics.
- [ ] Warmup → normal binary test exists.
- [ ] Fill-forward remains native.
- [ ] DST forward test exists.
- [ ] DST backward test exists.
- [ ] Early disposal works.
- [ ] Exception disposal works.

## Validation

- [ ] `git diff --check` passes.
- [ ] Engine build passes.
- [ ] Test project build passes.
- [ ] Binary resolver tests pass.
- [ ] Binary factory/reader tests pass.
- [ ] Binary History tests pass.
- [ ] Relevant native regression tests pass or documented environment failures are evidenced.
- [ ] Real-data characterization executed when local data is available.
- [ ] No unsupported performance claim was made.

## Problem 4

- [ ] No Problem 4 runtime implementation was added.
- [ ] No binary-search seek was added.
- [ ] No persistent index was added.
- [ ] No old-vs-new benchmark was implemented.
- [ ] No upstream sync/rebase was performed.
- [ ] Problem 4 diagnosis is included in Action Results.

## Reporting

- [ ] `SDD2-CODEX-Chat-Action results.md` created at repository root.
- [ ] Report includes actual commands/results.
- [ ] Report includes final `git status --short`.
- [ ] Report includes final `git diff --stat`.
- [ ] Report includes final `git diff --check`.
- [ ] Report includes remaining risks.
- [ ] Final chat response points to the report.
- [ ] Working-tree changes remain uncommitted for user review.

---

# 102. Final engineering principle

The desired result is not a second custom LEAN engine living beside LEAN.

The desired result is:

```text
Auroboros fixed-width binary storage
        +
explicit physical schema
        +
deterministic logical projection
        +
minimal-allocation forward-only reading
        +
bounded overlap handling
        +
native LEAN lifecycle semantics
```

The custom implementation should remain narrow enough that a future maintainer can understand where Auroboros differs from vanilla LEAN without reverse-engineering an independent data-feed framework.

Correctness comes first.

After correctness:

```text
avoid unnecessary records
avoid unnecessary allocations
avoid unnecessary copies
avoid unnecessary custom lifecycle logic
```

Problem 4 will later measure and optimize the remaining cost.

For SDD2, implement Problems 1–3 completely, prove them with tests and real-data characterization where available, write the Action Results file, and stop with all changes uncommitted.
