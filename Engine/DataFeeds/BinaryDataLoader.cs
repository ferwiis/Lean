using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Globalization;
using QuantConnect.Data;
using QuantConnect.Data.Market;

/** 

✅ Revisión futura:
- Benchmark I/O frente a procesamiento.
- Migrar BinaryDataLoader a usar MemoryMappedFile o Span<T> si el bottleneck está en carga de velas.
- Opcional: mover lectura a C puro con bindings C# si el entorno requiere ultra rendimiento (Linux headless o GCP).

 🔧 OPTIMIZACIÓN EXTREMA - ROADMAP FUTURO

✔ Estado actual:
- Lectura de 32M velas ≈ 35 segundos en entorno C# multihilo.
- Lectura paralela por archivo y uso de ArrayPool<byte> eficiente.
- Uso de Stream secuencial + buffer fijo de 8KB.
- Filtrado por rango temporal ya incluido.

🧠 Hipótesis de cuello de botella:
- Latencia por I/O tradicional (FileStream).
- Overhead en parseo desde byte[] a TradeBar C#.
- Coste acumulado de GC en millones de objetos.

🚀 Objetivo:
Reducir tiempo de carga de 32M velas a **< 10 segundos**, idealmente **~7s** o menos.

🛠️ Plan de acción por fases:

1. ✅ **Benchmark base actual** (ya logrado)
2. 🔄 **Fase 1: Refinamiento C#** (MMF/Span/stackalloc)
3. ⚙️ **Fase 2: Binding C** (lectura directa sin GC)
4. 🧬 **Condiciones**: Linux headless / RAM disk / NVMe

📌 Consideraciones:
- Este loader ya supera al FileSystemDataFeed + ZIPs de LEAN.
- Puede escalar 4–5x con el roadmap anterior.

🆕 Cambios clave
- ❌ Sin `Config` para timeframe: se **deduce del filename**.
- ✅ Selección de archivos por intersección **[start,end)**.
- ⏱️ Por archivo: **[from,to)** con `to=00:00:00` del día indicado; en el **último archivo**, `to=lastTs+period`.
- 🔄 Si hay mezcla de periodos, cada archivo usa su período real. La consolidación hacia arriba se hace **aquí** (overloads legacy) o en HistoryProvider.

**/

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>Modos de barra disponibles para cargar datos binarios.</summary>
    public enum BarDataMode { TradeBar, QuoteBar }

    /// <summary>
    /// Loader binario optimizado para cargar datos históricos en formato TradeBar o QuoteBar desde archivos .bin.
    /// Utiliza MemoryMappedFile + Span&lt;T&gt; para minimizar el overhead de memoria y maximizar el rendimiento.
    /// </summary>
    public static class BinaryDataLoader
    {
        // ========= Rutas =========

        /// <summary>
        /// Raíz de datos históricos binarios. Estructuras soportadas:
        ///  A) Data/historical_data/FX_{SYMBOL}_data/**/_bin_data/*.bin
        ///  B) Data/historical_data/FX_{SYMBOL}_*_bin_data/*.bin
        /// </summary>
        private static readonly string DataPath = GetDataPath();

        private static string GetDataPath()
            => Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Data", "historical_data"));

        // ========= Layouts binarios =========
        // OJO: ajusta tamaños/structs si el layout real difiere.

        private const int TradeBarRecordSize = 28; // 8 + 5*4
        private const int QuoteBarRecordSize = 64; // ajusta si tu layout real es distinto

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = TradeBarRecordSize)]
        private readonly struct TradeBarRecord
        {
            public readonly double Timestamp; // Unix seconds (UTC)
            public readonly float Open;
            public readonly float High;
            public readonly float Low;
            public readonly float Close;
            public readonly float Volume;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = QuoteBarRecordSize)]
        private readonly struct QuoteBarRecord
        {
            public readonly double Timestamp; // Unix seconds (UTC)

            // (Opcional) OHLC absoluto. Elimínalo si no existe en tu binario:
            public readonly float Open;
            public readonly float High;
            public readonly float Low;
            public readonly float Close;
            public readonly float Volume;

            // Bid/Ask OHLC usados por QuoteBar
            public readonly float BidOpen;
            public readonly float BidHigh;
            public readonly float BidLow;
            public readonly float BidClose;
            public readonly float AskOpen;
            public readonly float AskHigh;
            public readonly float AskLow;
            public readonly float AskClose;
        }

        // ========= Metadata por filename =========

        /// <summary>
        /// Metadatos deducidos de "N-FX_{SYMBOL}_{PERIOD}_{YYYYMMDD}-{YYYYMMDD}.bin".
        /// Convención de rango por archivo: [FromUtc, ToUtc) con ToUtc=00:00:00 del día indicado (exclusivo).
        /// El último archivo seleccionado corrige ToUtc = lastTimestamp + Period.
        /// </summary>
        private sealed record BinMeta(
            string Symbol,
            TimeSpan Period,
            DateTime FromUtc,
            DateTime ToUtc,
            string FullPath
        );

        private static bool TryParseMeta(string path, string symbol, out BinMeta meta)
        {
            meta = null;
            var name = Path.GetFileNameWithoutExtension(path);
            var parts = name.Split('_');
            // Esperado: ["3-FX", "{SYMBOL}", "{PERIOD}", "YYYYMMDD-YYYYMMDD"]
            if (parts.Length < 4) return false;

            var sym = parts[1];
            if (!sym.Equals(symbol, StringComparison.OrdinalIgnoreCase)) return false;

            if (!TryParsePeriod(parts[2], out var period)) return false;

            var dates = parts[3].Split('-');
            if (dates.Length != 2) return false;

            if (!DateTime.TryParseExact(dates[0], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var fromDay)) return false;

            if (!DateTime.TryParseExact(dates[1], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var toDayMarker)) return false;

            var fromUtc = DateTime.SpecifyKind(fromDay, DateTimeKind.Utc);
            var toUtc   = DateTime.SpecifyKind(toDayMarker, DateTimeKind.Utc); // exclusivo
            meta = new BinMeta(sym, period, fromUtc, toUtc, path);
            return true;
        }

        private static bool TryParsePeriod(string token, out TimeSpan period)
        {
            period = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(token)) return false;
            token = token.Trim().ToUpperInvariant();
            if (token.EndsWith("M") && int.TryParse(token[..^1], out var m)) { period = TimeSpan.FromMinutes(m); return true; }
            if (token.EndsWith("H") && int.TryParse(token[..^1], out var h)) { period = TimeSpan.FromHours(h);   return true; }
            return false;
        }

        // ========= Descubrimiento y selección =========

        private static List<string> GetAllBinaryFilesForSymbol(string symbol)
        {
            if (!Directory.Exists(DataPath))
                throw new DirectoryNotFoundException($"No se encontró la carpeta raíz de datos: {DataPath}");

            var results = new List<string>(256);

            // Patrón A: FX_{SYMBOL}_*_bin_data
            foreach (var dir in Directory.EnumerateDirectories(DataPath, $"FX_{symbol}_*_bin_data", SearchOption.TopDirectoryOnly))
                results.AddRange(Directory.EnumerateFiles(dir, "*.bin", SearchOption.TopDirectoryOnly));

            // Patrón B: FX_{SYMBOL}_data/**/_bin_data
            var symbolRoot = Path.Combine(DataPath, $"FX_{symbol}_data");
            if (Directory.Exists(symbolRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(symbolRoot, "*_bin_data", SearchOption.AllDirectories))
                {
                    if (!Path.GetFileName(dir).Contains(symbol, StringComparison.OrdinalIgnoreCase)) continue;
                    results.AddRange(Directory.EnumerateFiles(dir, "*.bin", SearchOption.TopDirectoryOnly));
                }
            }

            return results.Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                          .ToList();
        }

        private static List<BinMeta> GetAllMetasForSymbol(string symbol)
        {
            var files = GetAllBinaryFilesForSymbol(symbol);
            var metas = new List<BinMeta>(files.Count);
            foreach (var f in files)
                if (TryParseMeta(f, symbol, out var m)) metas.Add(m);
            return metas.OrderBy(m => m.FromUtc).ToList();
        }

        private static List<BinMeta> ResolveFilesFor(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var metas = GetAllMetasForSymbol(symbol);
            // intersección [from,to) ∩ [start,end)
            return metas.Where(m => m.FromUtc < endUtc && startUtc < m.ToUtc).ToList();
        }

        // ========= Último registro (ajuste del último archivo) =========

        private static DateTime ReadLastTimestamp_Trade(string filePath)
        {
            using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            unsafe
            {
                byte* basePtr = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    int bytes = (int)accessor.Capacity;
                    if (bytes < TradeBarRecordSize) return DateTime.MinValue;
                    var span = new ReadOnlySpan<byte>(basePtr + (bytes - TradeBarRecordSize), TradeBarRecordSize);
                    var rec  = MemoryMarshal.Cast<byte, TradeBarRecord>(span)[0];
                    return DateTimeOffset.FromUnixTimeSeconds((long)rec.Timestamp).UtcDateTime;
                }
                finally { accessor.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
        }

        private static DateTime ReadLastTimestamp_Quote(string filePath)
        {
            using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            unsafe
            {
                byte* basePtr = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    int bytes = (int)accessor.Capacity;
                    if (bytes < QuoteBarRecordSize) return DateTime.MinValue;
                    var span = new ReadOnlySpan<byte>(basePtr + (bytes - QuoteBarRecordSize), QuoteBarRecordSize);
                    var rec  = MemoryMarshal.Cast<byte, QuoteBarRecord>(span)[0];
                    return DateTimeOffset.FromUnixTimeSeconds((long)rec.Timestamp).UtcDateTime;
                }
                finally { accessor.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
        }

        // ========= API NUEVA (para HistoryProvider): sin periodo en parámetros =========

        /// <summary>
        /// Carga TradeBars en [startUtc, endUtc), leyendo sólo archivos que intersecan la ventana.
        /// El periodo se deduce del filename y se asigna en cada barra.
        /// </summary>
        public static IReadOnlyCollection<TradeBar> LoadAsTradeBars(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var metas = ResolveFilesFor(symbol, startUtc, endUtc);
            if (metas.Count == 0) return Array.Empty<TradeBar>();

            // Ajustar ToUtc del último archivo seleccionado a lastTs+period
            var lastByTo = metas.MaxBy(m => m.ToUtc);
            var lastTs = ReadLastTimestamp_Trade(lastByTo.FullPath);
            if (lastTs != DateTime.MinValue)
            {
                var fixedLast = lastByTo with { ToUtc = lastTs + lastByTo.Period };
                metas[metas.IndexOf(lastByTo)] = fixedLast;
            }

            var list = new List<TradeBar>(CalculateInitialCapacity(metas.Select(m => m.FullPath).ToList(), TradeBarRecordSize));
            foreach (var m in metas)
                list.AddRange(ReadTradeBarFile(m.FullPath, symbol, m.Period, startUtc, endUtc));

            return ProcessBars(list).AsReadOnly();
        }

        /// <summary>
        /// Carga QuoteBars en [startUtc, endUtc), leyendo sólo archivos que intersecan la ventana.
        /// El periodo se deduce del filename y se asigna en cada barra.
        /// </summary>
        public static IReadOnlyCollection<QuoteBar> LoadAsQuoteBars(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var metas = ResolveFilesFor(symbol, startUtc, endUtc);
            if (metas.Count == 0) return Array.Empty<QuoteBar>();

            var lastByTo = metas.MaxBy(m => m.ToUtc);
            var lastTs = ReadLastTimestamp_Quote(lastByTo.FullPath);
            if (lastTs != DateTime.MinValue)
            {
                var fixedLast = lastByTo with { ToUtc = lastTs + lastByTo.Period };
                metas[metas.IndexOf(lastByTo)] = fixedLast;
            }

            var list = new List<QuoteBar>(CalculateInitialCapacity(metas.Select(m => m.FullPath).ToList(), QuoteBarRecordSize));
            foreach (var m in metas)
                list.AddRange(ReadQuoteBarFile(m.FullPath, symbol, m.Period, startUtc, endUtc));

            return ProcessBars(list).AsReadOnly();
        }

        // ========= API LEGACY (para BinaryDataFeed/OnData): con periodo solicitado =========
        // Adapta la firma antigua a la nueva lógica: consolida hacia arriba si el periodo pedido > nativo.

        /// <summary>
        /// Versión legacy: si <paramref name="period"/> &gt; período nativo, consolida hacia arriba;
        /// si es igual, devuelve tal cual; si es menor, devuelve el nativo (no se inventa granularidad).
        /// </summary>
        public static IReadOnlyCollection<TradeBar> LoadAsTradeBars(string symbol, TimeSpan period, DateTime startUtc, DateTime endUtc)
        {
            var native = LoadAsTradeBars(symbol, startUtc, endUtc);
            if (native.Count == 0) return native;

            var basePeriod = native.First().Period;
            if (basePeriod == period) return native;
            if (period > basePeriod)
            {
                var symbolObj = native.First().Symbol;
                var consolidated = ConsolidateTradeBars(native, period, symbolObj).ToList();
                return consolidated.AsReadOnly();
            }
            // period < base → no se puede refinar; devolvemos nativo
            return native;
        }

        /// <summary>
        /// Versión legacy: si <paramref name="period"/> &gt; período nativo, consolida hacia arriba;
        /// si es igual, devuelve tal cual; si es menor, devuelve el nativo (no se inventa granularidad).
        /// </summary>
        public static IReadOnlyCollection<QuoteBar> LoadAsQuoteBars(string symbol, TimeSpan period, DateTime startUtc, DateTime endUtc)
        {
            var native = LoadAsQuoteBars(symbol, startUtc, endUtc);
            if (native.Count == 0) return native;

            var basePeriod = native.First().Period;
            if (basePeriod == period) return native;
            if (period > basePeriod)
            {
                var symbolObj = native.First().Symbol;
                var consolidated = ConsolidateQuoteBars(native, period, symbolObj).ToList();
                return consolidated.AsReadOnly();
            }
            // period < base → no se puede refinar; devolvemos nativo
            return native;
        }

        // ========= Lectura de archivos =========

        private static List<TradeBar> ReadTradeBarFile(string filePath, string symbol, TimeSpan period, DateTime startUtc, DateTime endUtc)
        {
            var bars = new List<TradeBar>();
            using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            unsafe
            {
                byte* ptr = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try
                {
                    var span = new ReadOnlySpan<byte>(ptr, (int)accessor.Capacity);
                    var records = MemoryMarshal.Cast<byte, TradeBarRecord>(span);

                    for (int i = 0; i < records.Length; i++)
                    {
                        var rec = records[i];
                        var time = DateTimeOffset.FromUnixTimeSeconds((long)rec.Timestamp).UtcDateTime;
                        if (time < startUtc) continue;
                        if (time >= endUtc) break; // [start,end)

                        bars.Add(new TradeBar
                        {
                            Time   = time,
                            EndTime = time + period,
                            Open   = (decimal)rec.Open,
                            High   = (decimal)rec.High,
                            Low    = (decimal)rec.Low,
                            Close  = (decimal)rec.Close,
                            Volume = (decimal)rec.Volume,
                            Period = period,
                            Symbol = Symbol.Create(symbol, SecurityType.Forex, Market.Oanda)
                        });
                    }
                }
                finally { accessor.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
            return bars;
        }

        private static List<QuoteBar> ReadQuoteBarFile(string filePath, string symbol, TimeSpan period, DateTime startUtc, DateTime endUtc)
        {
            var bars = new List<QuoteBar>();
            using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            unsafe
            {
                byte* ptr = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try
                {
                    var span = new ReadOnlySpan<byte>(ptr, (int)accessor.Capacity);
                    var records = MemoryMarshal.Cast<byte, QuoteBarRecord>(span);

                    for (int i = 0; i < records.Length; i++)
                    {
                        var rec = records[i];
                        var time = DateTimeOffset.FromUnixTimeSeconds((long)rec.Timestamp).UtcDateTime;
                        if (time < startUtc) continue;
                        if (time >= endUtc) break; // [start,end)

                        var qb = new QuoteBar
                        {
                            Time   = time,
                            EndTime = time + period,
                            Period = period,
                            Symbol = Symbol.Create(symbol, SecurityType.Forex, Market.Oanda),

                            // Si tu layout no tiene OHLC absoluto, puedes usar AskClose/BidClose:
                            Value = (decimal)rec.Close,

                            Bid = new Bar(
                                (decimal)rec.BidOpen,
                                (decimal)rec.BidHigh,
                                (decimal)rec.BidLow,
                                (decimal)rec.BidClose
                            ),
                            Ask = new Bar(
                                (decimal)rec.AskOpen,
                                (decimal)rec.AskHigh,
                                (decimal)rec.AskLow,
                                (decimal)rec.AskClose
                            )
                        };

                        // Alternativa si no existe rec.Close:
                        // qb.Value = qb.Ask?.Close ?? qb.Bid?.Close ?? 0m;

                        bars.Add(qb);
                    }
                }
                finally { accessor.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
            return bars;
        }

        // ========= Consolidación hacia arriba (para overloads legacy) =========

        private static IEnumerable<TradeBar> ConsolidateTradeBars(IEnumerable<TradeBar> src, TimeSpan target, Symbol symbol)
        {
            var bucket = new List<TradeBar>();
            DateTime? bucketEnd = null;

            foreach (var b in src.OrderBy(x => x.EndTime))
            {
                if (bucketEnd == null) bucketEnd = AlignUp(b.EndTime, target);

                if (b.EndTime <= bucketEnd.Value) { bucket.Add(b); continue; }

                if (bucket.Count > 0) yield return AggregateTrade(bucket, bucketEnd.Value, symbol);
                bucket.Clear();
                bucketEnd = AlignUp(b.EndTime, target);
                bucket.Add(b);
            }
            if (bucket.Count > 0 && bucketEnd != null)
                yield return AggregateTrade(bucket, bucketEnd.Value, symbol);
        }

        private static IEnumerable<QuoteBar> ConsolidateQuoteBars(IEnumerable<QuoteBar> src, TimeSpan target, Symbol symbol)
        {
            var bucket = new List<QuoteBar>();
            DateTime? bucketEnd = null;

            foreach (var b in src.OrderBy(x => x.EndTime))
            {
                if (bucketEnd == null) bucketEnd = AlignUp(b.EndTime, target);

                if (b.EndTime <= bucketEnd.Value) { bucket.Add(b); continue; }

                if (bucket.Count > 0) yield return AggregateQuote(bucket, bucketEnd.Value, symbol);
                bucket.Clear();
                bucketEnd = AlignUp(b.EndTime, target);
                bucket.Add(b);
            }
            if (bucket.Count > 0 && bucketEnd != null)
                yield return AggregateQuote(bucket, bucketEnd.Value, symbol);
        }

        private static DateTime AlignUp(DateTime t, TimeSpan step)
        {
            var ticks = ((t.Ticks + step.Ticks - 1) / step.Ticks) * step.Ticks;
            return new DateTime(ticks, t.Kind);
        }

        private static TradeBar AggregateTrade(List<TradeBar> w, DateTime end, Symbol s)
        {
            return new TradeBar(w.First().Time, s,
                open:  w.First().Open,
                high:  w.Max(x => x.High),
                low:   w.Min(x => x.Low),
                close: w.Last().Close,
                volume: w.Sum(x => x.Volume),
                period: end - w.First().Time)
            { EndTime = end };
        }

        private static QuoteBar AggregateQuote(List<QuoteBar> w, DateTime end, Symbol s)
        {
            var bidOpen  = w.First().Bid?.Open;
            var bidHigh  = w.Max(x => x.Bid?.High);
            var bidLow   = w.Min(x => x.Bid?.Low);
            var bidClose = w.Last().Bid?.Close;

            var askOpen  = w.First().Ask?.Open;
            var askHigh  = w.Max(x => x.Ask?.High);
            var askLow   = w.Min(x => x.Ask?.Low);
            var askClose = w.Last().Ask?.Close;

            var bidBar = (bidOpen, bidHigh, bidLow, bidClose) is (decimal bo, decimal bh, decimal bl, decimal bc)
                ? new Bar(bo, bh, bl, bc)
                : null;

            var askBar = (askOpen, askHigh, askLow, askClose) is (decimal ao, decimal ah, decimal al, decimal ac)
                ? new Bar(ao, ah, al, ac)
                : null;

            return new QuoteBar
            {
                Time    = w.First().Time,
                EndTime = end,
                Period  = end - w.First().Time,
                Symbol  = s,
                Value   = askClose ?? bidClose ?? 0m,
                Bid     = bidBar,
                Ask     = askBar
            };
        }

        // ========= Utilidades =========

        private static int CalculateInitialCapacity(List<string> files, int recordSize)
        {
            long totalBytes = files.Sum(f => new FileInfo(f).Length);
            if (recordSize <= 0) return 0;
            var approx = totalBytes / recordSize;
            return approx > int.MaxValue ? int.MaxValue : (int)approx;
        }

        private static List<T> ProcessBars<T>(List<T> bars) where T : IBaseData
            => bars.GroupBy(b => b.Time).Select(g => g.First()).OrderBy(b => b.Time).ToList();
    }
}