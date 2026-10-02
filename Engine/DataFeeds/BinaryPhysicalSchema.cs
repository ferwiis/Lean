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
using System.IO;
using System.Runtime.InteropServices;
using QuantConnect.Data.Market;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Identifies the fixed-width physical row stored in one Auroboros binary file.
    /// This is intentionally independent from the logical LEAN type requested by a subscription.
    /// </summary>
    public enum PhysicalRecordType
    {
        /// <summary>Native 32-byte OHLCV row.</summary>
        TradeBarRow,
        /// <summary>Native 64-byte global/bid/ask row.</summary>
        QuoteBarRow
    }

    /// <summary>
    /// Native producer ABI for an OHLCV row. The final four bytes are alignment padding,
    /// not an explicitly declared producer field.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = BinaryPhysicalSchema.TradeBarRecordSize)]
    public readonly struct TradeBarRow
    {
        /// <summary>UTC Unix seconds.</summary>
        [FieldOffset(0)] public readonly double Timestamp;
        /// <summary>Open.</summary>
        [FieldOffset(8)] public readonly float Open;
        /// <summary>High.</summary>
        [FieldOffset(12)] public readonly float High;
        /// <summary>Low.</summary>
        [FieldOffset(16)] public readonly float Low;
        /// <summary>Close.</summary>
        [FieldOffset(20)] public readonly float Close;
        /// <summary>Volume.</summary>
        [FieldOffset(24)] public readonly float Volume;
    }

    /// <summary>
    /// Native producer ABI for a global OHLCV row with bid and ask OHLC values.
    /// Bytes 60 through 63 are the producer-declared reserved field.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = BinaryPhysicalSchema.QuoteBarRecordSize)]
    public readonly struct QuoteBarRow
    {
        /// <summary>UTC Unix seconds.</summary>
        [FieldOffset(0)] public readonly double Timestamp;

        /// <summary>Global open.</summary>
        [FieldOffset(8)] public readonly float Open;
        /// <summary>Global high.</summary>
        [FieldOffset(12)] public readonly float High;
        /// <summary>Global low.</summary>
        [FieldOffset(16)] public readonly float Low;
        /// <summary>Global close.</summary>
        [FieldOffset(20)] public readonly float Close;
        /// <summary>Global volume.</summary>
        [FieldOffset(24)] public readonly float Volume;

        /// <summary>Bid open.</summary>
        [FieldOffset(28)] public readonly float BidOpen;
        /// <summary>Bid high.</summary>
        [FieldOffset(32)] public readonly float BidHigh;
        /// <summary>Bid low.</summary>
        [FieldOffset(36)] public readonly float BidLow;
        /// <summary>Bid close.</summary>
        [FieldOffset(40)] public readonly float BidClose;

        /// <summary>Ask open.</summary>
        [FieldOffset(44)] public readonly float AskOpen;
        /// <summary>Ask high.</summary>
        [FieldOffset(48)] public readonly float AskHigh;
        /// <summary>Ask low.</summary>
        [FieldOffset(52)] public readonly float AskLow;
        /// <summary>Ask close.</summary>
        [FieldOffset(56)] public readonly float AskClose;

        /// <summary>Producer-declared reserved value with no market-data semantics.</summary>
        [FieldOffset(60)] public readonly uint Reserved;
    }

    /// <summary>
    /// Defines the supported physical ABI and performs bounded legacy schema resolution.
    /// </summary>
    public static class BinaryPhysicalSchema
    {
        /// <summary>Native TradeBarRow byte width.</summary>
        public const int TradeBarRecordSize = 32;
        /// <summary>Native QuoteBarRow byte width.</summary>
        public const int QuoteBarRecordSize = 64;
        /// <summary>Maximum payload bytes inspected to resolve a legacy ambiguous file.</summary>
        public const int AmbiguityProbeSize = 128;

        /// <summary>
        /// Gets the fixed byte width for a physical row type.
        /// </summary>
        public static int GetRecordSize(PhysicalRecordType recordType)
        {
            return recordType switch
            {
                PhysicalRecordType.TradeBarRow => TradeBarRecordSize,
                PhysicalRecordType.QuoteBarRow => QuoteBarRecordSize,
                _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, null)
            };
        }

        /// <summary>
        /// Returns whether the physical row has a legitimate direct projection to the requested LEAN type.
        /// </summary>
        public static bool CanProject(PhysicalRecordType recordType, Type requestedLeanType)
        {
            if (requestedLeanType == typeof(TradeBar))
            {
                return recordType is PhysicalRecordType.TradeBarRow or PhysicalRecordType.QuoteBarRow;
            }

            return requestedLeanType == typeof(QuoteBar) && recordType == PhysicalRecordType.QuoteBarRow;
        }

        /// <summary>
        /// Resolves a file's physical row contract using its declaration, length, or a fixed 128-byte probe.
        /// Empty legacy files intentionally remain unresolved because they contain no payload.
        /// </summary>
        public static PhysicalRecordType? Resolve(
            string path,
            long fileLength,
            PhysicalRecordType? declaredRecordType,
            DateTime fromUtc,
            DateTime toUtc)
        {
            if (fileLength < 0)
            {
                throw new InvalidDataException($"Binary file '{path}' reported a negative length {fileLength}.");
            }

            if (declaredRecordType.HasValue)
            {
                var declaredSize = GetRecordSize(declaredRecordType.Value);
                if (fileLength % declaredSize != 0)
                {
                    throw new InvalidDataException(
                        $"Binary file '{path}' declares {declaredRecordType.Value} but has {fileLength} bytes; " +
                        $"expected a multiple of {declaredSize} bytes.");
                }

                return declaredRecordType;
            }

            if (fileLength == 0)
            {
                return null;
            }

            var canBeTradeBar = fileLength % TradeBarRecordSize == 0;
            var canBeQuoteBar = fileLength % QuoteBarRecordSize == 0;

            if (canBeTradeBar && !canBeQuoteBar)
            {
                return PhysicalRecordType.TradeBarRow;
            }

            if (!canBeTradeBar && !canBeQuoteBar)
            {
                throw new InvalidDataException(
                    $"Binary file '{path}' has {fileLength} bytes, which is not divisible by either supported " +
                    $"record size ({TradeBarRecordSize} or {QuoteBarRecordSize}).");
            }

            if (fileLength < AmbiguityProbeSize)
            {
                throw new InvalidDataException(
                    $"Binary file '{path}' is ambiguous: its {fileLength} bytes fit both supported schemas, " +
                    $"but fewer than {AmbiguityProbeSize} bytes are available for the bounded legacy probe. " +
                    "Add an explicit _TB or _QB filename declaration.");
            }

            Span<byte> probe = stackalloc byte[AmbiguityProbeSize];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, AmbiguityProbeSize,
                       FileOptions.SequentialScan))
            {
                stream.ReadExactly(probe);
            }

            var tradeBarValid = IsValidTradeBarProbe(probe, fromUtc, toUtc);
            var quoteBarValid = IsValidQuoteBarProbe(probe, fromUtc, toUtc);

            if (tradeBarValid == quoteBarValid)
            {
                var outcome = tradeBarValid ? "both schemas remain valid" : "neither schema is valid";
                throw new InvalidDataException(
                    $"Binary file '{path}' could not be resolved by the bounded {AmbiguityProbeSize}-byte probe: {outcome}. " +
                    "Add an explicit _TB or _QB filename declaration or repair the binary payload.");
            }

            return tradeBarValid ? PhysicalRecordType.TradeBarRow : PhysicalRecordType.QuoteBarRow;
        }

        internal static bool TryConvertTimestamp(double timestamp, out DateTime utcTime)
        {
            utcTime = default;
            if (!double.IsFinite(timestamp))
            {
                return false;
            }

            try
            {
                utcTime = DateTimeOffset.FromUnixTimeSeconds(checked((long)timestamp)).UtcDateTime;
                return true;
            }
            catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static bool IsValidTradeBarProbe(ReadOnlySpan<byte> bytes, DateTime fromUtc, DateTime toUtc)
        {
            DateTime? previous = null;
            for (var offset = 0; offset < bytes.Length; offset += TradeBarRecordSize)
            {
                var row = MemoryMarshal.Read<TradeBarRow>(bytes.Slice(offset, TradeBarRecordSize));
                if (!IsPlausibleTimestamp(row.Timestamp, fromUtc, toUtc, previous, out var utcTime)
                    || !IsPlausibleOhlc(row.Open, row.High, row.Low, row.Close)
                    || !float.IsFinite(row.Volume)
                    || row.Volume < 0)
                {
                    return false;
                }

                previous = utcTime;
            }

            return true;
        }

        private static bool IsValidQuoteBarProbe(ReadOnlySpan<byte> bytes, DateTime fromUtc, DateTime toUtc)
        {
            DateTime? previous = null;
            for (var offset = 0; offset < bytes.Length; offset += QuoteBarRecordSize)
            {
                var row = MemoryMarshal.Read<QuoteBarRow>(bytes.Slice(offset, QuoteBarRecordSize));
                if (!IsPlausibleTimestamp(row.Timestamp, fromUtc, toUtc, previous, out var utcTime)
                    || !IsPlausibleOhlc(row.Open, row.High, row.Low, row.Close)
                    || !float.IsFinite(row.Volume)
                    || row.Volume < 0
                    || !IsPlausibleOhlc(row.BidOpen, row.BidHigh, row.BidLow, row.BidClose)
                    || !IsPlausibleOhlc(row.AskOpen, row.AskHigh, row.AskLow, row.AskClose))
                {
                    return false;
                }

                previous = utcTime;
            }

            return true;
        }

        private static bool IsPlausibleTimestamp(
            double timestamp,
            DateTime fromUtc,
            DateTime toUtc,
            DateTime? previous,
            out DateTime utcTime)
        {
            if (!TryConvertTimestamp(timestamp, out utcTime)
                || utcTime < fromUtc
                || utcTime >= toUtc
                || previous.HasValue && utcTime < previous.Value)
            {
                return false;
            }

            return true;
        }

        private static bool IsPlausibleOhlc(float open, float high, float low, float close)
        {
            const float tolerance = 0.00001f;
            return float.IsFinite(open)
                && float.IsFinite(high)
                && float.IsFinite(low)
                && float.IsFinite(close)
                && open > 0
                && high > 0
                && low > 0
                && close > 0
                && high >= low - tolerance
                && open <= high + tolerance
                && open >= low - tolerance
                && close <= high + tolerance
                && close >= low - tolerance;
        }
    }
}
