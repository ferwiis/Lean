using System.Globalization;

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
    /// Resuelve archivos binarios (TradeBar/QuoteBar) en disco y deduce metadatos a partir del filename.
    ///
    /// Este componente es intencionalmente "tonto":
    /// - NO lee registros.
    /// - NO crea TradeBars/QuoteBars.
    /// - NO hace consolidaciones.
    ///
    /// Su única responsabilidad es: filesystem + parsing + selección.
    ///
    /// Se extrae desde <c>BinaryDataLoader</c> para desacoplar responsabilidades y permitir,
    /// en pasos posteriores, implementar un lector streaming tipo <c>SubscriptionDataReader</c>.
    /// </summary>
    /// <remarks>
    /// Estructuras de carpetas soportadas (idénticas a las asumidas por <c>BinaryDataLoader</c>):
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>Data/historical_data/FX_{SYMBOL}_data/**/_bin_data/*.bin</c>
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>Data/historical_data/FX_{SYMBOL}_*_bin_data/*.bin</c>
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public static class BinaryFileResolver
    {
        #region Paths

        /// <summary>
        /// Carpeta raíz donde se esperan los datos binarios históricos.
        /// </summary>
        /// <remarks>
        /// Se calcula usando <see cref="Directory.GetCurrentDirectory"/> y la ruta relativa <c>Data/historical_data</c>.
        /// </remarks>
        public static readonly string DataPath = GetDataPath();

        /// <summary>
        /// Obtiene la ruta absoluta de la carpeta raíz de datos binarios históricos.
        /// </summary>
        /// <returns>Ruta absoluta a <c>Data/historical_data</c>.</returns>
        private static string GetDataPath()
            => Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Data", "historical_data"));

        #endregion

        #region Metadata model

        /// <summary>
        /// Metadatos deducidos a partir del filename de un archivo binario.
        /// </summary>
        /// <param name="Symbol">Símbolo textual (por ejemplo: EURUSD).</param>
        /// <param name="Period">Periodo nativo deducido del filename (ej. 1M, 5M, 1H).</param>
        /// <param name="FromUtc">Inicio (inclusive) del rango temporal representado por el archivo.</param>
        /// <param name="ToUtc">
        /// Fin (exclusive) del rango temporal representado por el archivo.
        /// Por convención, esto suele ser <c>00:00:00</c> del día final indicado en el filename.
        /// </param>
        /// <param name="FullPath">Ruta absoluta al archivo .bin.</param>
        /// <remarks>
        /// Convención de rango por archivo:
        /// - El archivo representa <c>[FromUtc, ToUtc)</c>.
        /// - Donde <c>ToUtc</c> se interpreta como <c>00:00:00</c> del día final indicado (exclusive).
        ///
        /// Nota: En el loader batch actual, el último archivo seleccionado puede “corregir” su <c>ToUtc</c>
        /// a <c>lastTimestamp + Period</c>. Esa corrección NO pertenece al resolver (es lectura de records).
        /// </remarks>
        public sealed record BinMeta(
            string Symbol,
            TimeSpan Period,
            DateTime FromUtc,
            DateTime ToUtc,
            string FullPath
        );

        #endregion

        #region Filename parsing

        /// <summary>
        /// Intenta deducir metadatos desde el filename del archivo binario, validando que corresponda al símbolo solicitado.
        /// </summary>
        /// <param name="path">Ruta del archivo .bin.</param>
        /// <param name="symbol">Símbolo esperado (ej. EURUSD). Se compara case-insensitive.</param>
        /// <param name="meta">Metadatos resultantes si el parseo fue exitoso.</param>
        /// <returns>
        /// True si el filename cumple el formato esperado y corresponde al símbolo; en caso contrario false.
        /// </returns>
        /// <remarks>
        /// Formato esperado de filename (sin extensión):
        /// <c>N-FX_{SYMBOL}_{PERIOD}_{YYYYMMDD}-{YYYYMMDD}.bin</c>
        ///
        /// Ejemplos válidos:
        /// - <c>3-FX_EURUSD_1M_20090101-20090131.bin</c>
        /// - <c>3-FX_GBPUSD_1H_20100101-20100115.bin</c>
        /// </remarks>
        public static bool TryParseMeta(string path, string symbol, out BinMeta meta)
        {
            meta = null;

            var name = Path.GetFileNameWithoutExtension(path);
            var parts = name.Split('_');

            // Esperado: ["3-FX", "{SYMBOL}", "{PERIOD}", "YYYYMMDD-YYYYMMDD"]
            if (parts.Length < 4) return false;

            var sym = parts[1];
            if (!sym.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!TryParsePeriod(parts[2], out var period))
                return false;

            var dates = parts[3].Split('-');
            if (dates.Length != 2) return false;

            if (!DateTime.TryParseExact(
                    dates[0],
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var fromDay))
                return false;

            if (!DateTime.TryParseExact(
                    dates[1],
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var toDayMarker))
                return false;

            var fromUtc = DateTime.SpecifyKind(fromDay, DateTimeKind.Utc);
            var toUtc = DateTime.SpecifyKind(toDayMarker, DateTimeKind.Utc); // exclusive

            meta = new BinMeta(sym, period, fromUtc, toUtc, path);
            return true;
        }

        /// <summary>
        /// Intenta convertir el token de periodo presente en el filename a un <see cref="TimeSpan"/>.
        /// </summary>
        /// <param name="token">Token del filename, ejemplo: "1M", "5M", "1H".</param>
        /// <param name="period">Periodo resultante si el parseo fue exitoso.</param>
        /// <returns>True si el token fue reconocido; de lo contrario false.</returns>
        /// <remarks>
        /// Reglas actuales:
        /// - "XM" => minutos
        /// - "XH" => horas
        ///
        /// Si en el futuro agregas D (días) u otros formatos, este método es el lugar correcto.
        /// </remarks>
        public static bool TryParsePeriod(string token, out TimeSpan period)
        {
            period = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(token))
                return false;

            token = token.Trim().ToUpperInvariant();

            if (token.EndsWith("M") && int.TryParse(token[..^1], out var minutes))
            {
                period = TimeSpan.FromMinutes(minutes);
                return true;
            }

            if (token.EndsWith("H") && int.TryParse(token[..^1], out var hours))
            {
                period = TimeSpan.FromHours(hours);
                return true;
            }

            return false;
        }

        #endregion

        #region Discovery & selection

        /// <summary>
        /// Encuentra en disco todos los archivos <c>.bin</c> asociados a un símbolo dado.
        /// </summary>
        /// <param name="symbol">Símbolo (ej. "EURUSD").</param>
        /// <returns>
        /// Lista ordenada de rutas absolutas a archivos <c>.bin</c> (sin duplicados).
        /// </returns>
        /// <exception cref="DirectoryNotFoundException">
        /// Si no existe la carpeta raíz <see cref="DataPath"/>.
        /// </exception>
        /// <remarks>
        /// Este método implementa dos patrones de búsqueda para soportar distintas estructuras de exportación.
        /// Mantener estos patrones aquí permite evolucionar la organización de carpetas sin tocar el lector.
        /// </remarks>
        public static List<string> GetAllBinaryFilesForSymbol(string symbol)
        {
            if (!Directory.Exists(DataPath))
                throw new DirectoryNotFoundException($"No se encontró la carpeta raíz de datos: {DataPath}");

            var results = new List<string>(256);

            // Patrón A: Data/historical_data/FX_{SYMBOL}_*_bin_data/*.bin
            foreach (var dir in Directory.EnumerateDirectories(
                         DataPath,
                         $"FX_{symbol}_*_bin_data",
                         SearchOption.TopDirectoryOnly))
            {
                results.AddRange(Directory.EnumerateFiles(dir, "*.bin", SearchOption.TopDirectoryOnly));
            }

            // Patrón B: Data/historical_data/FX_{SYMBOL}_data/**/_bin_data/*.bin
            var symbolRoot = Path.Combine(DataPath, $"FX_{symbol}_data");
            if (Directory.Exists(symbolRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(symbolRoot, "*_bin_data", SearchOption.AllDirectories))
                {
                    // Guardia extra: solo carpetas que contengan el símbolo en el nombre (según tu lógica actual).
                    if (!Path.GetFileName(dir).Contains(symbol, StringComparison.OrdinalIgnoreCase))
                        continue;

                    results.AddRange(Directory.EnumerateFiles(dir, "*.bin", SearchOption.TopDirectoryOnly));
                }
            }

            return results
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Obtiene metadatos deducidos para todos los archivos binarios del símbolo indicado.
        /// </summary>
        /// <param name="symbol">Símbolo (ej. "EURUSD").</param>
        /// <returns>
        /// Lista de <see cref="BinMeta"/> ordenada por <see cref="BinMeta.FromUtc"/>.
        /// Archivos que no cumplan el formato esperado del filename son ignorados.
        /// </returns>
        public static List<BinMeta> GetAllMetasForSymbol(string symbol)
        {
            var files = GetAllBinaryFilesForSymbol(symbol);
            var metas = new List<BinMeta>(files.Count);

            foreach (var f in files)
            {
                if (TryParseMeta(f, symbol, out var m))
                    metas.Add(m);
            }

            return metas.OrderBy(m => m.FromUtc).ToList();
        }

        /// <summary>
        /// Resuelve qué archivos binarios deben leerse para cubrir una ventana temporal <c>[startUtc, endUtc)</c>.
        /// </summary>
        /// <param name="symbol">Símbolo (ej. "EURUSD").</param>
        /// <param name="startUtc">Inicio (inclusive) de la ventana solicitada.</param>
        /// <param name="endUtc">Fin (exclusive) de la ventana solicitada.</param>
        /// <returns>
        /// Lista de <see cref="BinMeta"/> cuyos rangos <c>[FromUtc,ToUtc)</c> intersectan con <c>[startUtc,endUtc)</c>.
        /// </returns>
        /// <remarks>
        /// La condición de intersección entre intervalos semiabiertos es:
        /// <c>meta.FromUtc &lt; endUtc &amp;&amp; startUtc &lt; meta.ToUtc</c>.
        ///
        /// <para>
        /// Este método NO ajusta la ventana del último archivo a <c>lastTimestamp + Period</c>,
        /// porque hacerlo requiere leer el archivo (responsabilidad del reader/loader).
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var metas = BinaryFileResolver.ResolveFilesFor(
        ///     symbol: "EURUSD",
        ///     startUtc: new DateTime(2009, 01, 10, 0, 0, 0, DateTimeKind.Utc),
        ///     endUtc:   new DateTime(2009, 01, 12, 0, 0, 0, DateTimeKind.Utc)
        /// );
        ///
        /// // metas contendrá todos los archivos cuyo rango se cruce con esa ventana.
        /// </code>
        /// </example>
        public static List<BinMeta> ResolveFilesFor(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var metas = GetAllMetasForSymbol(symbol);

            // Intersección [from,to) ∩ [start,end)
            return metas
                .Where(m => m.FromUtc < endUtc && startUtc < m.ToUtc)
                .ToList();
        }

        #endregion
    }
}
