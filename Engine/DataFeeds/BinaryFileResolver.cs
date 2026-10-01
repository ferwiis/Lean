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
    /// Discovers binary historical-data files and parses metadata encoded in their names.
    /// This type deliberately does not read records or implement subscription semantics.
    /// </summary>
    public static class BinaryFileResolver
    {
        /// <summary>
        /// Default binary historical-data root.
        /// </summary>
        public static string DataPath => Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Data", "historical_data"));

        /// <summary>
        /// Metadata parsed from <c>N-FX_SYMBOL_PERIOD_YYYYMMDD-YYYYMMDD.bin</c>.
        /// File ranges use half-open <c>[FromUtc, ToUtc)</c> semantics.
        /// </summary>
        public sealed record BinMeta(
            string Symbol,
            TimeSpan Period,
            DateTime FromUtc,
            DateTime ToUtc,
            string FullPath
        );

        /// <summary>
        /// Parses binary file metadata and validates the requested symbol.
        /// </summary>
        public static bool TryParseMeta(string path, string symbol, out BinMeta meta)
        {
            meta = null;
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(symbol))
            {
                return false;
            }

            var parts = Path.GetFileNameWithoutExtension(path).Split('_');
            var prefix = parts.Length == 4 ? parts[0].Split('-') : Array.Empty<string>();
            if (parts.Length != 4
                || prefix.Length != 2
                || !int.TryParse(prefix[0], NumberStyles.None, CultureInfo.InvariantCulture, out var fileIndex)
                || fileIndex < 0
                || !prefix[1].Equals("FX", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parsedSymbol = parts[1];
            if (!parsedSymbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                || !TryParsePeriod(parts[2], out var period))
            {
                return false;
            }

            var dates = parts[3].Split('-');
            if (dates.Length != 2
                || !DateTime.TryParseExact(dates[0], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var fromDay)
                || !DateTime.TryParseExact(dates[1], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var toDay))
            {
                return false;
            }

            var fromUtc = DateTime.SpecifyKind(fromDay, DateTimeKind.Utc);
            var toUtc = DateTime.SpecifyKind(toDay, DateTimeKind.Utc);
            if (fromUtc >= toUtc)
            {
                return false;
            }

            meta = new BinMeta(parsedSymbol, period, fromUtc, toUtc, Path.GetFullPath(path));
            return true;
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
            if (token.Length < 2 || !int.TryParse(token[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
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
        /// Returns all valid metadata for a symbol in deterministic chronological order.
        /// </summary>
        public static List<BinMeta> GetAllMetasForSymbol(string symbol, string dataPath = null)
        {
            return GetAllBinaryFilesForSymbol(symbol, dataPath)
                .Select(path => TryParseMeta(path, symbol, out var meta) ? meta : null)
                .Where(meta => meta != null)
                .OrderBy(meta => meta.FromUtc)
                .ThenBy(meta => meta.ToUtc)
                .ThenBy(meta => meta.Period)
                .ThenBy(meta => meta.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Selects the native period used to satisfy a requested period.
        /// Exact matches win. Otherwise, the smallest available finer period that evenly divides
        /// the request is selected for streaming consolidation. If the request is finer than every
        /// available dataset, the smallest available native period is returned without fabricating
        /// finer data. A non-divisible finer dataset cannot safely produce the requested boundaries.
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

            var consolidatable = periods.FirstOrDefault(period =>
                period < requestedPeriod && requestedPeriod.Ticks % period.Ticks == 0);
            if (consolidatable > TimeSpan.Zero)
            {
                return consolidatable;
            }

            if (requestedPeriod < periods[0])
            {
                return periods[0];
            }

            throw new InvalidOperationException(
                $"No native binary period can safely satisfy requested period {requestedPeriod}. " +
                $"Available periods: {string.Join(", ", periods)}.");
        }

        /// <summary>
        /// Resolves files for one native period whose metadata intersects <c>[startUtc, endUtc)</c>.
        /// Overlapping files are ordered by start, end, and path; readers apply first-file-wins deduplication.
        /// </summary>
        public static List<BinMeta> ResolveFilesFor(
            string symbol,
            TimeSpan requestedPeriod,
            DateTime startUtc,
            DateTime endUtc,
            string dataPath = null)
        {
            if (startUtc >= endUtc)
            {
                return new List<BinMeta>();
            }

            var intersecting = GetAllMetasForSymbol(symbol, dataPath)
                .Where(meta => meta.FromUtc < endUtc && startUtc < meta.ToUtc)
                .ToList();
            var nativePeriod = SelectNativePeriod(intersecting.Select(meta => meta.Period), requestedPeriod);

            return nativePeriod == null
                ? new List<BinMeta>()
                : intersecting
                    .Where(meta => meta.Period == nativePeriod.Value)
                    .OrderBy(meta => meta.FromUtc)
                    .ThenBy(meta => meta.ToUtc)
                    .ThenBy(meta => meta.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
    }
}