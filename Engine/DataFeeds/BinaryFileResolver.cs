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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Discovers binary historical-data files, resolves their physical contracts, and creates
    /// deterministic execution plans for logical LEAN requests.
    /// </summary>
    public static class BinaryFileResolver
    {
        /// <summary>
        /// Default binary historical-data root.
        /// </summary>
        public static string DataPath => Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Data", "historical_data"));

        /// <summary>
        /// Metadata parsed from a supported binary filename. File ranges use half-open
        /// <c>[FromUtc, ToUtc)</c> semantics.
        /// </summary>
        public sealed record BinMeta(
            string Symbol,
            TimeSpan Period,
            DateTime FromUtc,
            DateTime ToUtc,
            string FullPath,
            PhysicalRecordType? DeclaredPhysicalRecordType = null,
            PhysicalRecordType? PhysicalRecordType = null,
            long FileLength = 0
        );

        /// <summary>
        /// Fully resolved source plan for one logical LEAN request.
        /// </summary>
        public sealed record BinaryResolutionPlan(
            IReadOnlyList<BinMeta> Files,
            TimeSpan? NativePeriod,
            TimeSpan RequestedPeriod,
            Type RequestedLeanType
        );

        /// <summary>
        /// Parses legacy filenames and optional <c>_TB</c>/<c>_QB</c> physical declarations.
        /// </summary>
        public static bool TryParseMeta(string path, string symbol, out BinMeta meta)
        {
            return TryParseMeta(path, symbol, out meta, out _);
        }

        /// <summary>
        /// Parses minute and hour period tokens such as <c>5M</c> and <c>1H</c>.
        /// </summary>
        public static bool TryParsePeriod(string token, out TimeSpan period)
        {
            period = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            token = token.Trim().ToUpperInvariant();
            if (token.Length < 2
                || !int.TryParse(token[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value <= 0)
            {
                return false;
            }

            period = token[^1] switch
            {
                'M' => TimeSpan.FromMinutes(value),
                'H' => TimeSpan.FromHours(value),
                _ => TimeSpan.Zero
            };
            return period > TimeSpan.Zero;
        }

        /// <summary>
        /// Returns every binary file found for a symbol in deterministic path order.
        /// </summary>
        public static List<string> GetAllBinaryFilesForSymbol(string symbol, string dataPath = null)
        {
            var root = Path.GetFullPath(dataPath ?? DataPath);
            if (!Directory.Exists(root))
            {
                return new List<string>();
            }

            var results = new List<string>();

            // Layout A: Data/historical_data/FX_SYMBOL_*_bin_data/*.bin
            foreach (var directory in Directory.EnumerateDirectories(root, $"FX_{symbol}_*_bin_data", SearchOption.TopDirectoryOnly))
            {
                results.AddRange(Directory.EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly));
            }

            // Layout B: Data/historical_data/FX_SYMBOL_data/**/FX_SYMBOL_*_bin_data/*.bin
            var symbolRoot = Path.Combine(root, $"FX_{symbol}_data");
            if (Directory.Exists(symbolRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(symbolRoot, "*_bin_data", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(directory).Contains(symbol, StringComparison.OrdinalIgnoreCase))
                    {
                        results.AddRange(Directory.EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly));
                    }
                }
            }

            return results
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Returns valid metadata for a symbol. Symbol-specific malformed filenames are reported
        /// together in deterministic path order; unrelated files are ignored.
        /// </summary>
        public static List<BinMeta> GetAllMetasForSymbol(string symbol, string dataPath = null)
        {
            var metadata = new List<BinMeta>();
            var errors = new List<string>();
            foreach (var path in GetAllBinaryFilesForSymbol(symbol, dataPath))
            {
                if (TryParseMeta(path, symbol, out var meta, out var error))
                {
                    metadata.Add(meta);
                }
                else if (IsSymbolSpecificCandidate(path, symbol))
                {
                    errors.Add($"{path}: {error}");
                }
            }

            if (errors.Count > 0)
            {
                throw new InvalidDataException(
                    "Malformed Auroboros binary filename metadata:" + Environment.NewLine
                    + string.Join(Environment.NewLine, errors.OrderBy(error => error, StringComparer.OrdinalIgnoreCase)));
            }

            return metadata
                .OrderBy(meta => meta.FromUtc)
                .ThenBy(meta => meta.ToUtc)
                .ThenBy(meta => meta.Period)
                .ThenBy(meta => meta.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Selects the exact native period, or otherwise the largest smaller exact divisor.
        /// Finer-than-native and non-divisible requests fail explicitly.
        /// </summary>
        public static TimeSpan? SelectNativePeriod(IEnumerable<TimeSpan> availablePeriods, TimeSpan requestedPeriod)
        {
            if (requestedPeriod <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(requestedPeriod), "The requested period must be positive.");
            }

            var periods = availablePeriods
                .Where(period => period > TimeSpan.Zero)
                .Distinct()
                .OrderBy(period => period)
                .ToList();

            if (periods.Count == 0)
            {
                return null;
            }

            if (periods.Contains(requestedPeriod))
            {
                return requestedPeriod;
            }

            var consolidatable = periods
                .Where(period => period < requestedPeriod && requestedPeriod.Ticks % period.Ticks == 0)
                .DefaultIfEmpty(TimeSpan.Zero)
                .Max();
            if (consolidatable > TimeSpan.Zero)
            {
                return consolidatable;
            }

            if (requestedPeriod < periods[0])
            {
                throw new InvalidOperationException(
                    $"Requested period {requestedPeriod} is finer than the smallest available native period {periods[0]}.");
            }

            throw new InvalidOperationException(
                $"No available native period exactly divides requested period {requestedPeriod}. " +
                $"Available periods: {string.Join(", ", periods)}.");
        }

        /// <summary>
        /// Resolves physical schemas, logical compatibility, native period, and selected files for a request.
        /// Schema preflight reads at most 128 payload bytes per ambiguous legacy file.
        /// </summary>
        public static BinaryResolutionPlan ResolveRequest(
            string symbol,
            Type requestedLeanType,
            TimeSpan requestedPeriod,
            DateTime startUtc,
            DateTime endUtc,
            string dataPath = null)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                throw new ArgumentException("A binary request requires a symbol.", nameof(symbol));
            }
            if (requestedLeanType == null)
            {
                throw new ArgumentNullException(nameof(requestedLeanType));
            }
            if (startUtc >= endUtc)
            {
                return new BinaryResolutionPlan(Array.Empty<BinMeta>(), null, requestedPeriod, requestedLeanType);
            }

            var intersecting = GetAllMetasForSymbol(symbol, dataPath)
                .Where(meta => meta.FromUtc < endUtc && startUtc < meta.ToUtc)
                .ToList();
            if (intersecting.Count == 0)
            {
                return new BinaryResolutionPlan(Array.Empty<BinMeta>(), null, requestedPeriod, requestedLeanType);
            }

            var resolved = new List<BinMeta>(intersecting.Count);
            var preflightErrors = new List<string>();
            foreach (var metadata in intersecting)
            {
                try
                {
                    var length = new FileInfo(metadata.FullPath).Length;
                    var physicalRecordType = BinaryPhysicalSchema.Resolve(
                        metadata.FullPath,
                        length,
                        metadata.DeclaredPhysicalRecordType,
                        metadata.FromUtc,
                        metadata.ToUtc);
                    resolved.Add(metadata with { PhysicalRecordType = physicalRecordType, FileLength = length });
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    preflightErrors.Add($"{metadata.FullPath}: {exception.Message}");
                }
            }

            if (preflightErrors.Count > 0)
            {
                throw new InvalidDataException(
                    $"Binary preflight failed for symbol '{symbol}' ({requestedLeanType.Name}, {requestedPeriod}):"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine,
                        preflightErrors.OrderBy(error => error, StringComparer.OrdinalIgnoreCase)));
            }

            var periodGroups = resolved.GroupBy(file => file.Period).ToList();
            var compatibleGroups = periodGroups
                .Where(group => group.All(file => file.FileLength == 0
                    || !file.PhysicalRecordType.HasValue
                    || BinaryPhysicalSchema.CanProject(file.PhysicalRecordType.Value, requestedLeanType)))
                .ToList();

            if (compatibleGroups.Count == 0)
            {
                var incompatibilities = resolved
                    .Where(file => file.PhysicalRecordType.HasValue
                        && !BinaryPhysicalSchema.CanProject(file.PhysicalRecordType.Value, requestedLeanType))
                    .OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                    .Select(file => $"{file.FullPath}: {file.PhysicalRecordType} cannot project to {requestedLeanType.Name}");
                throw new NotSupportedException(
                    $"No compatible physical binary dataset can satisfy '{symbol}' as {requestedLeanType.Name}."
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, incompatibilities));
            }

            TimeSpan? nativePeriod;
            try
            {
                nativePeriod = SelectNativePeriod(compatibleGroups.Select(group => group.Key), requestedPeriod);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Cannot satisfy binary request for '{symbol}' as {requestedLeanType.Name}: requested period " +
                    $"{requestedPeriod}; compatible native periods: " +
                    $"{string.Join(", ", compatibleGroups.Select(group => group.Key).OrderBy(period => period))}. " +
                    exception.Message,
                    exception);
            }

            var selected = compatibleGroups
                .Single(group => group.Key == nativePeriod.Value)
                .OrderBy(file => file.FromUtc)
                .ThenBy(file => file.ToUtc)
                .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new BinaryResolutionPlan(selected, nativePeriod, requestedPeriod, requestedLeanType);
        }

        private static bool TryParseMeta(string path, string symbol, out BinMeta meta, out string error)
        {
            meta = null;
            error = null;
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(symbol))
            {
                error = "Path and symbol are required.";
                return false;
            }

            var parts = Path.GetFileNameWithoutExtension(path).Split('_');
            if (parts.Length is not (4 or 5))
            {
                error = "Expected legacy metadata or one optional _TB/_QB physical schema suffix.";
                return false;
            }

            PhysicalRecordType? declaredRecordType = null;
            if (parts.Length == 5)
            {
                declaredRecordType = parts[4].ToUpperInvariant() switch
                {
                    "TB" => PhysicalRecordType.TradeBarRow,
                    "QB" => PhysicalRecordType.QuoteBarRow,
                    _ => null
                };
                if (!declaredRecordType.HasValue)
                {
                    error = $"Unsupported physical schema suffix '_{parts[4]}'; expected _TB or _QB.";
                    return false;
                }
            }

            var prefix = parts[0].Split('-');
            if (prefix.Length != 2
                || !int.TryParse(prefix[0], NumberStyles.None, CultureInfo.InvariantCulture, out var fileIndex)
                || fileIndex < 0
                || !prefix[1].Equals("FX", StringComparison.OrdinalIgnoreCase))
            {
                error = "Expected a non-negative file index followed by the FX market tag.";
                return false;
            }

            var parsedSymbol = parts[1];
            if (!parsedSymbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Filename symbol '{parsedSymbol}' does not match requested symbol '{symbol}'.";
                return false;
            }
            if (!TryParsePeriod(parts[2], out var period))
            {
                error = $"Unsupported period token '{parts[2]}'.";
                return false;
            }

            var dates = parts[3].Split('-');
            if (dates.Length != 2
                || !DateTime.TryParseExact(dates[0], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var fromDay)
                || !DateTime.TryParseExact(dates[1], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var toDay))
            {
                error = $"Invalid UTC date range token '{parts[3]}'.";
                return false;
            }

            var fromUtc = DateTime.SpecifyKind(fromDay, DateTimeKind.Utc);
            var toUtc = DateTime.SpecifyKind(toDay, DateTimeKind.Utc);
            if (fromUtc >= toUtc)
            {
                error = "Filename date range must be increasing and half-open.";
                return false;
            }

            meta = new BinMeta(
                parsedSymbol,
                period,
                fromUtc,
                toUtc,
                Path.GetFullPath(path),
                declaredRecordType);
            return true;
        }

        private static bool IsSymbolSpecificCandidate(string path, string symbol)
        {
            return Path.GetFileNameWithoutExtension(path)
                .Contains($"_{symbol}_", StringComparison.OrdinalIgnoreCase);
        }
    }
}
