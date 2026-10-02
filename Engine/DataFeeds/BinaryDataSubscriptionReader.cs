/*
 * AUROBOROS
 * High-performance algorithmic trading and research infrastructure.
 *
 * Copyright (c) 2026 Juan Fernando Alzate Gomez.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Shared logical stream for TradeBar and QuoteBar requests over typed physical binary files.
    /// Non-overlapping files use one cursor at a time; connected overlap groups use a bounded merge.
    /// </summary>
    internal sealed class BinaryDataSubscriptionReader : IEnumerator<BaseData>
    {
        private readonly Symbol _symbol;
        private readonly Type _requestedLeanType;
        private readonly DateTime _startUtc;
        private readonly DateTime _endUtc;
        private readonly IReadOnlyList<IReadOnlyList<BinaryFileResolver.BinMeta>> _groups;

        private int _groupIndex = -1;
        private IBinaryLogicalEnumerator _groupEnumerator;
        private BinaryLogicalRecord? _lastEmitted;

        public BinaryDataSubscriptionReader(
            Symbol symbol,
            Type requestedLeanType,
            IReadOnlyList<BinaryFileResolver.BinMeta> files,
            DateTime startUtc,
            DateTime endUtc)
        {
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _requestedLeanType = requestedLeanType ?? throw new ArgumentNullException(nameof(requestedLeanType));
            _startUtc = NormalizeUtc(startUtc);
            _endUtc = NormalizeUtc(endUtc);

            if (_requestedLeanType != typeof(TradeBar) && _requestedLeanType != typeof(QuoteBar))
            {
                throw new NotSupportedException($"Unsupported logical binary type '{_requestedLeanType}'.");
            }
            if (_startUtc > _endUtc)
            {
                throw new ArgumentException("The binary reader start time must not be after the end time.");
            }
            if (files == null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            var nonEmptyFiles = files
                .Where(file => file.FileLength > 0)
                .OrderBy(file => file.FromUtc)
                .ThenBy(file => file.ToUtc)
                .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (nonEmptyFiles.Any(file => !file.PhysicalRecordType.HasValue))
            {
                throw new InvalidOperationException("A non-empty binary file reached the reader without a resolved physical schema.");
            }
            if (nonEmptyFiles.Any(file => !BinaryPhysicalSchema.CanProject(file.PhysicalRecordType.Value, _requestedLeanType)))
            {
                throw new NotSupportedException($"A physical binary source cannot project to {_requestedLeanType.Name}.");
            }
            if (nonEmptyFiles.Select(file => file.Period).Distinct().Skip(1).Any())
            {
                throw new InvalidOperationException("One binary logical stream cannot contain heterogeneous native periods.");
            }

            _groups = CreateOverlapGroups(nonEmptyFiles);
        }

        public BaseData Current { get; private set; }

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            Current = null;
            try
            {
                while (true)
                {
                    if (_groupEnumerator == null)
                    {
                        if (!MoveToNextGroup())
                        {
                            return false;
                        }
                    }

                    if (!_groupEnumerator.MoveNext())
                    {
                        _groupEnumerator.Dispose();
                        _groupEnumerator = null;
                        continue;
                    }

                    var candidate = _groupEnumerator.Current;
                    if (_lastEmitted.HasValue)
                    {
                        var comparison = candidate.UtcTime.CompareTo(_lastEmitted.Value.UtcTime);
                        if (comparison < 0)
                        {
                            throw new InvalidDataException(
                                $"Binary stream chronology moved backwards from {_lastEmitted.Value.UtcTime:o} " +
                                $"in '{_lastEmitted.Value.SourcePath}' to {candidate.UtcTime:o} in '{candidate.SourcePath}'. " +
                                "The filename intervals do not describe the physical record chronology.");
                        }
                        if (comparison == 0)
                        {
                            if (!BinaryLogicalEquality.Equals(_lastEmitted.Value.Data, candidate.Data))
                            {
                                throw BinaryLogicalEquality.CreateConflict(_lastEmitted.Value, candidate);
                            }

                            continue;
                        }
                    }

                    _lastEmitted = candidate;
                    Current = candidate.Data;
                    return true;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Reset()
        {
            throw new NotSupportedException($"{nameof(BinaryDataSubscriptionReader)} is forward-only.");
        }

        public void Dispose()
        {
            _groupEnumerator?.Dispose();
            _groupEnumerator = null;
            Current = null;
        }

        private bool MoveToNextGroup()
        {
            if (++_groupIndex >= _groups.Count)
            {
                return false;
            }

            var group = _groups[_groupIndex];
            _groupEnumerator = group.Count == 1
                ? new BinaryPhysicalFileCursor(_symbol, _requestedLeanType, group[0], _startUtc, _endUtc, 0)
                : new BinaryOverlapMergeEnumerator(_symbol, _requestedLeanType, group, _startUtc, _endUtc);
            return true;
        }

        private static IReadOnlyList<IReadOnlyList<BinaryFileResolver.BinMeta>> CreateOverlapGroups(
            IReadOnlyList<BinaryFileResolver.BinMeta> files)
        {
            var result = new List<IReadOnlyList<BinaryFileResolver.BinMeta>>();
            if (files.Count == 0)
            {
                return result;
            }

            var current = new List<BinaryFileResolver.BinMeta> { files[0] };
            var groupEnd = files[0].ToUtc;
            for (var index = 1; index < files.Count; index++)
            {
                var file = files[index];
                if (file.FromUtc < groupEnd)
                {
                    current.Add(file);
                    if (file.ToUtc > groupEnd)
                    {
                        groupEnd = file.ToUtc;
                    }
                    continue;
                }

                result.Add(current);
                current = new List<BinaryFileResolver.BinMeta> { file };
                groupEnd = file.ToUtc;
            }

            result.Add(current);
            return result;
        }

        private static DateTime NormalizeUtc(DateTime time)
        {
            return time.Kind switch
            {
                DateTimeKind.Utc => time,
                DateTimeKind.Local => time.ToUniversalTime(),
                _ => DateTime.SpecifyKind(time, DateTimeKind.Utc)
            };
        }
    }

    internal readonly record struct BinaryLogicalRecord(
        BaseData Data,
        DateTime UtcTime,
        string SourcePath,
        long RecordIndex,
        long ByteOffset);

    internal interface IBinaryLogicalEnumerator : IDisposable
    {
        BinaryLogicalRecord Current { get; }
        bool MoveNext();
    }

    /// <summary>
    /// Owns one mapped file and projects its fixed-width physical rows one at a time.
    /// </summary>
    internal sealed class BinaryPhysicalFileCursor : IBinaryLogicalEnumerator
    {
        private readonly Symbol _symbol;
        private readonly Type _requestedLeanType;
        private readonly BinaryFileResolver.BinMeta _file;
        private readonly DateTime _startUtc;
        private readonly DateTime _endUtc;
        private readonly int _fileOrder;
        private readonly int _recordSize;

        private MemoryMappedFile _memoryMappedFile;
        private MemoryMappedViewAccessor _accessor;
        private unsafe byte* _basePointer;
        private bool _pointerAcquired;
        private bool _opened;
        private long _recordIndex;
        private long _recordCount;
        private DateTime? _previousRecordTime;

        public BinaryPhysicalFileCursor(
            Symbol symbol,
            Type requestedLeanType,
            BinaryFileResolver.BinMeta file,
            DateTime startUtc,
            DateTime endUtc,
            int fileOrder)
        {
            _symbol = symbol;
            _requestedLeanType = requestedLeanType;
            _file = file;
            _startUtc = startUtc;
            _endUtc = endUtc;
            _fileOrder = fileOrder;
            _recordSize = BinaryPhysicalSchema.GetRecordSize(file.PhysicalRecordType.Value);
        }

        public BinaryLogicalRecord Current { get; private set; }

        public int FileOrder => _fileOrder;

        public bool MoveNext()
        {
            try
            {
                if (!_opened)
                {
                    Open();
                }

                while (_recordIndex < _recordCount)
                {
                    var index = _recordIndex++;
                    var offset = checked(index * (long)_recordSize);
                    var timestamp = ReadTimestamp(offset);
                    if (!BinaryPhysicalSchema.TryConvertTimestamp(timestamp, out var utcTime))
                    {
                        throw CreateRecordError(index, offset, timestamp, "timestamp is not a finite supported UTC Unix value");
                    }
                    if (_previousRecordTime.HasValue && utcTime < _previousRecordTime.Value)
                    {
                        throw CreateRecordError(
                            index,
                            offset,
                            timestamp,
                            $"timestamp {utcTime:o} decreases after {_previousRecordTime.Value:o}");
                    }
                    _previousRecordTime = utcTime;

                    if (utcTime < _startUtc)
                    {
                        continue;
                    }
                    if (utcTime >= _endUtc)
                    {
                        Dispose();
                        return false;
                    }

                    var data = Project(index, offset, timestamp, utcTime);
                    Current = new BinaryLogicalRecord(data, utcTime, _file.FullPath, index, offset);
                    return true;
                }

                Dispose();
                return false;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_accessor != null)
            {
                if (_pointerAcquired)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    _pointerAcquired = false;
                }

                unsafe
                {
                    _basePointer = null;
                }
                _accessor.Dispose();
                _accessor = null;
            }

            _memoryMappedFile?.Dispose();
            _memoryMappedFile = null;
            _recordCount = 0;
        }

        private void Open()
        {
            _opened = true;
            var length = new FileInfo(_file.FullPath).Length;
            if (length != _file.FileLength)
            {
                throw new InvalidDataException(
                    $"Binary file '{_file.FullPath}' changed length after preflight: " +
                    $"expected {_file.FileLength} bytes, found {length} bytes.");
            }
            if (length <= 0 || length % _recordSize != 0)
            {
                throw new InvalidDataException(
                    $"Binary file '{_file.FullPath}' no longer satisfies {_file.PhysicalRecordType} " +
                    $"record size {_recordSize}; current length is {length} bytes.");
            }

            try
            {
                _memoryMappedFile = MemoryMappedFile.CreateFromFile(
                    _file.FullPath,
                    FileMode.Open,
                    null,
                    0,
                    MemoryMappedFileAccess.Read);
                _accessor = _memoryMappedFile.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
                unsafe
                {
                    _basePointer = null;
                    _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePointer);
                }
                _pointerAcquired = true;
                _recordCount = length / _recordSize;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private unsafe double ReadTimestamp(long offset)
        {
            return MemoryMarshal.Read<double>(new ReadOnlySpan<byte>(_basePointer + offset, sizeof(double)));
        }

        private unsafe BaseData Project(long index, long offset, double timestamp, DateTime utcTime)
        {
            if (_file.PhysicalRecordType == QuantConnect.Lean.Engine.DataFeeds.PhysicalRecordType.TradeBarRow)
            {
                var row = MemoryMarshal.Read<TradeBarRow>(new ReadOnlySpan<byte>(_basePointer + offset, _recordSize));
                if (_requestedLeanType != typeof(TradeBar))
                {
                    throw CreateRecordError(index, offset, timestamp, "TradeBarRow cannot project to QuoteBar");
                }

                return CreateTradeBar(
                    utcTime,
                    index,
                    offset,
                    timestamp,
                    row.Open,
                    row.High,
                    row.Low,
                    row.Close,
                    row.Volume);
            }

            var quoteRow = MemoryMarshal.Read<QuoteBarRow>(new ReadOnlySpan<byte>(_basePointer + offset, _recordSize));
            if (_requestedLeanType == typeof(TradeBar))
            {
                return CreateTradeBar(
                    utcTime,
                    index,
                    offset,
                    timestamp,
                    quoteRow.Open,
                    quoteRow.High,
                    quoteRow.Low,
                    quoteRow.Close,
                    quoteRow.Volume);
            }

            var bid = new Bar(
                ToDecimal(quoteRow.BidOpen, nameof(QuoteBarRow.BidOpen), index, offset, timestamp),
                ToDecimal(quoteRow.BidHigh, nameof(QuoteBarRow.BidHigh), index, offset, timestamp),
                ToDecimal(quoteRow.BidLow, nameof(QuoteBarRow.BidLow), index, offset, timestamp),
                ToDecimal(quoteRow.BidClose, nameof(QuoteBarRow.BidClose), index, offset, timestamp));
            var ask = new Bar(
                ToDecimal(quoteRow.AskOpen, nameof(QuoteBarRow.AskOpen), index, offset, timestamp),
                ToDecimal(quoteRow.AskHigh, nameof(QuoteBarRow.AskHigh), index, offset, timestamp),
                ToDecimal(quoteRow.AskLow, nameof(QuoteBarRow.AskLow), index, offset, timestamp),
                ToDecimal(quoteRow.AskClose, nameof(QuoteBarRow.AskClose), index, offset, timestamp));

            // The native constructor derives Value from the bid/ask Close midpoint.
            return new QuoteBar(utcTime, _symbol, bid, 0m, ask, 0m, _file.Period);
        }

        private TradeBar CreateTradeBar(
            DateTime utcTime,
            long index,
            long offset,
            double timestamp,
            float open,
            float high,
            float low,
            float close,
            float volume)
        {
            return new TradeBar(
                utcTime,
                _symbol,
                ToDecimal(open, "Open", index, offset, timestamp),
                ToDecimal(high, "High", index, offset, timestamp),
                ToDecimal(low, "Low", index, offset, timestamp),
                ToDecimal(close, "Close", index, offset, timestamp),
                ToDecimal(volume, "Volume", index, offset, timestamp),
                _file.Period);
        }

        private decimal ToDecimal(float value, string field, long index, long offset, double timestamp)
        {
            if (!float.IsFinite(value))
            {
                throw CreateRecordError(index, offset, timestamp, $"{field} is not finite ({value})");
            }

            try
            {
                return checked((decimal)value);
            }
            catch (OverflowException exception)
            {
                throw CreateRecordError(index, offset, timestamp, $"{field} cannot be represented as decimal ({value})", exception);
            }
        }

        private InvalidDataException CreateRecordError(
            long index,
            long offset,
            double timestamp,
            string reason,
            Exception innerException = null)
        {
            var message =
                $"Invalid binary record: file='{_file.FullPath}', recordIndex={index}, byteOffset={offset}, " +
                $"physicalRecordType={_file.PhysicalRecordType}, timestamp={timestamp:R}; {reason}.";
            return innerException == null
                ? new InvalidDataException(message)
                : new InvalidDataException(message, innerException);
        }
    }

    /// <summary>
    /// Chronologically merges a connected overlap group while retaining one pending record per file.
    /// </summary>
    internal sealed class BinaryOverlapMergeEnumerator : IBinaryLogicalEnumerator
    {
        private readonly List<BinaryPhysicalFileCursor> _cursors;
        private readonly PriorityQueue<BinaryPhysicalFileCursor, MergePriority> _queue = new();
        private bool _initialized;

        public BinaryOverlapMergeEnumerator(
            Symbol symbol,
            Type requestedLeanType,
            IReadOnlyList<BinaryFileResolver.BinMeta> files,
            DateTime startUtc,
            DateTime endUtc)
        {
            _cursors = files
                .Select((file, index) => new BinaryPhysicalFileCursor(
                    symbol,
                    requestedLeanType,
                    file,
                    startUtc,
                    endUtc,
                    index))
                .ToList();
        }

        public BinaryLogicalRecord Current { get; private set; }

        public bool MoveNext()
        {
            try
            {
                if (!_initialized)
                {
                    Initialize();
                }
                if (_queue.Count == 0)
                {
                    return false;
                }

                _queue.TryPeek(out _, out var firstPriority);
                var timestampTicks = firstPriority.TimestampTicks;
                BinaryLogicalRecord? canonical = null;

                while (_queue.TryPeek(out _, out var priority) && priority.TimestampTicks == timestampTicks)
                {
                    var cursor = _queue.Dequeue();
                    var candidate = cursor.Current;
                    if (canonical.HasValue && !BinaryLogicalEquality.Equals(canonical.Value.Data, candidate.Data))
                    {
                        throw BinaryLogicalEquality.CreateConflict(canonical.Value, candidate);
                    }
                    canonical ??= candidate;

                    if (cursor.MoveNext())
                    {
                        Enqueue(cursor);
                    }
                }

                Current = canonical.Value;
                return true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var cursor in _cursors)
            {
                cursor.Dispose();
            }
            _queue.Clear();
        }

        private void Initialize()
        {
            _initialized = true;
            foreach (var cursor in _cursors)
            {
                if (cursor.MoveNext())
                {
                    Enqueue(cursor);
                }
            }
        }

        private void Enqueue(BinaryPhysicalFileCursor cursor)
        {
            _queue.Enqueue(
                cursor,
                new MergePriority(cursor.Current.UtcTime.Ticks, cursor.FileOrder, cursor.Current.RecordIndex));
        }

        private readonly record struct MergePriority(long TimestampTicks, int FileOrder, long RecordIndex)
            : IComparable<MergePriority>
        {
            public int CompareTo(MergePriority other)
            {
                var comparison = TimestampTicks.CompareTo(other.TimestampTicks);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = FileOrder.CompareTo(other.FileOrder);
                return comparison != 0 ? comparison : RecordIndex.CompareTo(other.RecordIndex);
            }
        }
    }

    internal static class BinaryLogicalEquality
    {
        public static bool Equals(BaseData left, BaseData right)
        {
            if (left == null || right == null || left.GetType() != right.GetType())
            {
                return false;
            }
            if (left.Time != right.Time || left.EndTime != right.EndTime || left.Symbol != right.Symbol)
            {
                return false;
            }

            if (left is TradeBar leftTrade && right is TradeBar rightTrade)
            {
                return leftTrade.Period == rightTrade.Period
                    && leftTrade.Open == rightTrade.Open
                    && leftTrade.High == rightTrade.High
                    && leftTrade.Low == rightTrade.Low
                    && leftTrade.Close == rightTrade.Close
                    && leftTrade.Volume == rightTrade.Volume;
            }

            if (left is QuoteBar leftQuote && right is QuoteBar rightQuote)
            {
                return leftQuote.Period == rightQuote.Period
                    && BarsEqual(leftQuote.Bid, rightQuote.Bid)
                    && BarsEqual(leftQuote.Ask, rightQuote.Ask)
                    && leftQuote.Close == rightQuote.Close
                    && leftQuote.Value == rightQuote.Value;
            }

            return false;
        }

        public static InvalidDataException CreateConflict(BinaryLogicalRecord left, BinaryLogicalRecord right)
        {
            return new InvalidDataException(
                $"Conflicting logical binary duplicates at {left.UtcTime:o}: " +
                $"file='{left.SourcePath}', recordIndex={left.RecordIndex}, byteOffset={left.ByteOffset}, " +
                $"value={Describe(left.Data)}; file='{right.SourcePath}', recordIndex={right.RecordIndex}, " +
                $"byteOffset={right.ByteOffset}, value={Describe(right.Data)}. Filename order cannot select a price.");
        }

        private static bool BarsEqual(IBar left, IBar right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            return left != null
                && right != null
                && left.Open == right.Open
                && left.High == right.High
                && left.Low == right.Low
                && left.Close == right.Close;
        }

        private static string Describe(BaseData data)
        {
            return data switch
            {
                TradeBar tradeBar =>
                    $"TradeBar(O={tradeBar.Open},H={tradeBar.High},L={tradeBar.Low},C={tradeBar.Close},V={tradeBar.Volume},P={tradeBar.Period})",
                QuoteBar quoteBar =>
                    $"QuoteBar(B={DescribeBar(quoteBar.Bid)},A={DescribeBar(quoteBar.Ask)},C={quoteBar.Close},V={quoteBar.Value},P={quoteBar.Period})",
                _ => data?.GetType().Name ?? "null"
            };
        }

        private static string DescribeBar(IBar bar)
        {
            return bar == null ? "null" : $"[{bar.Open},{bar.High},{bar.Low},{bar.Close}]";
        }
    }
}
