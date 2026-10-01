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
using System.Runtime.InteropServices;
using QuantConnect.Data;
using QuantConnect.Data.Market;

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

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Forward-only, memory-mapped reader for the Auroboros QuoteBar binary ABI.
    /// Records are created one at a time and the supplied LEAN symbol is preserved.
    /// </summary>
    public sealed class BinaryQuoteBarSubscriptionReader : IEnumerator<BaseData>
    {
        /// <summary>
        /// Producer layout: Unix-seconds double, absolute OHLCV, bid OHLC, ask OHLC,
        /// and spread (fourteen floats total). The resulting record is exactly 64 bytes.
        /// </summary>
        public const int RecordSize = 64;

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = RecordSize)]
        private readonly struct QuoteBarRecord
        {
            public readonly double Timestamp;
            public readonly float Open;
            public readonly float High;
            public readonly float Low;
            public readonly float Close;
            public readonly float Volume;
            public readonly float BidOpen;
            public readonly float BidHigh;
            public readonly float BidLow;
            public readonly float BidClose;
            public readonly float AskOpen;
            public readonly float AskHigh;
            public readonly float AskLow;
            public readonly float AskClose;
            public readonly float Spread;
        }

        private readonly Symbol _symbol;
        private readonly DateTime _startUtc;
        private readonly DateTime _endUtc;
        private readonly IReadOnlyList<BinaryFileResolver.BinMeta> _files;

        private MemoryMappedFile _memoryMappedFile;
        private MemoryMappedViewAccessor _accessor;
        private unsafe byte* _basePointer;
        private bool _pointerAcquired;
        private int _fileIndex = -1;
        private long _recordIndex;
        private long _recordCount;
        private DateTime? _previousRecordTime;
        private DateTime? _lastEmittedTime;

        /// <summary>
        /// Creates a reader for pre-resolved files. Files must all have the same native period.
        /// </summary>
        public BinaryQuoteBarSubscriptionReader(
            Symbol symbol,
            IReadOnlyList<BinaryFileResolver.BinMeta> files,
            DateTime startUtc,
            DateTime endUtc)
        {
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _startUtc = NormalizeUtc(startUtc);
            _endUtc = NormalizeUtc(endUtc);

            if (_startUtc > _endUtc)
            {
                throw new ArgumentException("The binary reader start time must not be after the end time.");
            }
        }

        /// <summary>
        /// Gets the current QuoteBar as BaseData.
        /// </summary>
        public BaseData Current { get; private set; }

        object IEnumerator.Current => Current;

        /// <summary>
        /// Advances to the next unique record in <c>[startUtc, endUtc)</c>.
        /// Duplicate and overlapping timestamps use deterministic first-file-wins behavior.
        /// </summary>
        public bool MoveNext()
        {
            Current = null;
            while (true)
            {
                if (_accessor == null && !MoveToNextNonEmptyFile())
                {
                    return false;
                }

                while (_recordIndex < _recordCount)
                {
                    var record = ReadRecord(_recordIndex++);
                    var time = GetUtcTime(record.Timestamp);
                    if (_previousRecordTime.HasValue && time < _previousRecordTime.Value)
                    {
                        throw new InvalidDataException(
                            $"Binary records are not chronological in '{_files[_fileIndex].FullPath}': " +
                            $"{time:o} follows {_previousRecordTime:o}.");
                    }
                    _previousRecordTime = time;

                    if (time < _startUtc)
                    {
                        continue;
                    }
                    if (time >= _endUtc)
                    {
                        CloseCurrentFile();
                        break;
                    }
                    if (_lastEmittedTime.HasValue && time <= _lastEmittedTime.Value)
                    {
                        continue;
                    }

                    var period = _files[_fileIndex].Period;
                    Current = new QuoteBar
                    {
                        Time = time,
                        EndTime = time + period,
                        Period = period,
                        Symbol = _symbol,
                        Value = (decimal)record.Close,
                        Bid = new Bar(
                            (decimal)record.BidOpen,
                            (decimal)record.BidHigh,
                            (decimal)record.BidLow,
                            (decimal)record.BidClose),
                        Ask = new Bar(
                            (decimal)record.AskOpen,
                            (decimal)record.AskHigh,
                            (decimal)record.AskLow,
                            (decimal)record.AskClose)
                    };
                    _lastEmittedTime = time;
                    return true;
                }

                if (_accessor != null && _recordIndex >= _recordCount)
                {
                    CloseCurrentFile();
                }
            }
        }

        /// <summary>
        /// Releases the mapped view and file immediately.
        /// </summary>
        public void Dispose()
        {
            CloseCurrentFile();
        }

        /// <summary>
        /// This reader is forward-only.
        /// </summary>
        public void Reset()
        {
            throw new NotSupportedException($"{nameof(BinaryQuoteBarSubscriptionReader)} is forward-only.");
        }

        private bool MoveToNextNonEmptyFile()
        {
            // This deliberately scans from record zero. A timestamp index/binary seek can be added later
            // if profiling justifies it, without changing the streaming object-lifetime contract.
            CloseCurrentFile();
            while (++_fileIndex < _files.Count)
            {
                var file = _files[_fileIndex];
                try
                {
                    var length = new FileInfo(file.FullPath).Length;
                    if (length == 0)
                    {
                        continue;
                    }
                    if (length % RecordSize != 0)
                    {
                        throw new InvalidDataException(
                            $"QuoteBar binary file '{file.FullPath}' has {length} bytes; expected a multiple of {RecordSize}.");
                    }

                    _memoryMappedFile = MemoryMappedFile.CreateFromFile(file.FullPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                    _accessor = _memoryMappedFile.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);

                    unsafe
                    {
                        _basePointer = null;
                        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePointer);
                    }
                    _pointerAcquired = true;
                    _recordCount = length / RecordSize;
                    _recordIndex = 0;
                    _previousRecordTime = null;

                    if (_recordCount > 0)
                    {
                        return true;
                    }

                    CloseCurrentFile();
                }
                catch
                {
                    CloseCurrentFile();
                    throw;
                }
            }
            return false;
        }

        private unsafe QuoteBarRecord ReadRecord(long index)
        {
            var offset = checked(index * RecordSize);
            return MemoryMarshal.Read<QuoteBarRecord>(new ReadOnlySpan<byte>(_basePointer + offset, RecordSize));
        }

        private static DateTime GetUtcTime(double timestamp)
        {
            if (double.IsNaN(timestamp) || double.IsInfinity(timestamp))
            {
                throw new InvalidDataException($"Invalid binary Unix timestamp '{timestamp}'.");
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(checked((long)timestamp)).UtcDateTime;
            }
            catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
            {
                throw new InvalidDataException($"Binary Unix timestamp '{timestamp}' is outside the supported DateTime range.", exception);
            }
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

        private void CloseCurrentFile()
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
            _recordIndex = 0;
            _recordCount = 0;
            _previousRecordTime = null;
        }
    }
}
