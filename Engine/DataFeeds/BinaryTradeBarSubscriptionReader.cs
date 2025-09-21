using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

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
    /// Enumerador streaming (forward-only) de <see cref="TradeBar"/> respaldado por
    /// <see cref="MemoryMappedFile"/> (file-backed) sobre archivos <c>.bin</c>.
    ///
    /// Este componente es el equivalente binario del lector que LEAN usa para CSV (conceptualmente similar a
    /// <c>SubscriptionDataReader</c>), pero diseñado para:
    /// - NO materializar toda la data en RAM (sin <c>List&lt;TradeBar&gt;</c>).
    /// - Emitir 1 barra por llamada a <see cref="MoveNext"/>.
    /// - Iterar múltiples archivos que intersectan el rango temporal solicitado.
    ///
    /// El objetivo principal es estabilidad de memoria (sin explosión de GC) y permitir que
    /// el kernel comparta páginas (page cache) cuando varios procesos mapean el mismo archivo binario.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Uso esperado</b>: Este reader se usa como "source enumerator" dentro de un <c>IDataFeed</c> custom
    /// (por ejemplo <c>BinaryDataFeed</c>). Luego, el enumerador es envuelto por el pipeline normal de LEAN
    /// (fill-forward, filtros, strict end times, etc.) tal como hace el feed nativo.
    /// </para>
    ///
    /// <para>
    /// <b>Memoria y multiproceso</b>:
    /// Al ser file-backed, el OS puede compartir físicamente las páginas del archivo entre procesos
    /// (read-only). Sin embargo, cada proceso seguirá creando objetos <see cref="TradeBar"/> a demanda
    /// (inevitable para el modelo de LEAN), pero sin retener millones de objetos simultáneamente.
    /// </para>
    ///
    /// <para>
    /// <b>Responsabilidades</b>:
    /// - Selección de archivos por ventana temporal: se delega a <see cref="BinaryFileResolver"/>.
    /// - Lectura de records: se hace aquí (streaming record a record).
    /// - Construcción de objetos LEAN (<see cref="TradeBar"/>): se hace aquí.
    ///
    /// <b>NO hace</b>:
    /// - Dedupe global por <c>Time</c> (esto era batch en <c>BinaryDataLoader.ProcessBars</c>).
    /// - Consolidación hacia arriba (eso lo puedes dejar en History/batch o hacerla como wrapper posterior).
    /// </para>
    /// </remarks>
    public sealed class BinaryTradeBarSubscriptionReader : IEnumerator<BaseData>
    {
        #region Nested types

        /// <summary>
        /// Tamaño en bytes de un record TradeBar en el archivo <c>.bin</c>.
        /// Debe coincidir EXACTAMENTE con el layout generado por tu exportador.
        ///
        /// Layout asumido:
        /// - double Timestamp (8 bytes) Unix seconds UTC
        /// - float Open (4)
        /// - float High (4)
        /// - float Low  (4)
        /// - float Close (4)
        /// - float Volume (4)
        /// Total: 28 bytes
        /// </summary>
        private const int TradeBarRecordSize = 28; // 8 + 5*4

        /// <summary>
        /// Record binario de TradeBar. Debe estar alineado con <see cref="TradeBarRecordSize"/>.
        /// </summary>
        /// <remarks>
        /// Se usa <see cref="StructLayoutAttribute"/> con <c>Pack=1</c> para evitar padding.
        /// Si tu exportador usa otra alineación, ajusta este struct (y el tamaño).
        /// </remarks>
        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = TradeBarRecordSize)]
        private readonly struct TradeBarRecord
        {
            /// <summary>Timestamp en Unix seconds (UTC).</summary>
            public readonly double Timestamp;

            public readonly float Open;
            public readonly float High;
            public readonly float Low;
            public readonly float Close;
            public readonly float Volume;
        }

        #endregion

        #region Fields

        /// <summary>Símbolo textual usado en el layout de carpetas (ej. "EURUSD").</summary>
        private readonly string _symbol;

        /// <summary>
        /// Símbolo LEAN. Nota: aquí se crea como Forex/Oanda por consistencia con tu loader actual.
        /// Si en tu sistema el Market difiere, parametrízalo o resuélvelo desde el request.
        /// </summary>
        private readonly Symbol _leanSymbol;

        /// <summary>Ventana solicitada [startUtc, endUtc).</summary>
        private readonly DateTime _startUtc;

        /// <summary>Ventana solicitada [startUtc, endUtc).</summary>
        private readonly DateTime _endUtc;

        /// <summary>Archivos binarios candidatos (metas) que intersectan la ventana solicitada.</summary>
        private readonly List<BinaryFileResolver.BinMeta> _metas;

        /// <summary>
        /// Índice del archivo actual dentro de <see cref="_metas"/>.
        /// Inicialmente -1 y se incrementa en <see cref="MoveToNextFile"/>.
        /// </summary>
        private int _metaIndex;

        /// <summary>MMF activo para el archivo actual.</summary>
        private MemoryMappedFile _mmf;

        /// <summary>Accessor activo para el archivo actual.</summary>
        private MemoryMappedViewAccessor _accessor;

        /// <summary>Puntero base al buffer mapeado del archivo actual.</summary>
        private unsafe byte* _basePtr;

        /// <summary>Tamaño total del archivo mapeado en bytes.</summary>
        private int _bytes;

        /// <summary>Índice del record actual dentro del archivo.</summary>
        private int _recordIndex;

        /// <summary>Cantidad total de records disponibles en el archivo actual.</summary>
        private int _recordCount;

        #endregion

        #region Constructor

        /// <summary>
        /// Crea un reader streaming para TradeBars en una ventana temporal.
        /// </summary>
        /// <param name="symbol">
        /// Símbolo textual usado en el dataset binario (ej. "EURUSD"). Debe coincidir con el naming de carpetas.
        /// </param>
        /// <param name="startUtc">Inicio (inclusive) UTC del rango solicitado.</param>
        /// <param name="endUtc">Fin (exclusive) UTC del rango solicitado.</param>
        /// <remarks>
        /// Este constructor no abre archivos de inmediato.
        /// Los archivos se abren "lazy" cuando <see cref="MoveNext"/> necesita avanzar por primera vez.
        /// </remarks>
        public BinaryTradeBarSubscriptionReader(string symbol, DateTime startUtc, DateTime endUtc)
        {
            _symbol = symbol;
            _leanSymbol = Symbol.Create(symbol, SecurityType.Forex, Market.Oanda);

            _startUtc = startUtc;
            _endUtc = endUtc;

            // Resuelve los archivos que intersectan [start,end)
            _metas = BinaryFileResolver.ResolveFilesFor(symbol, startUtc, endUtc);
            _metaIndex = -1;

            _mmf = null;
            _accessor = null;
            unsafe { _basePtr = null; }

            _bytes = 0;
            _recordIndex = 0;
            _recordCount = 0;

            Current = null;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Elemento actual (la barra actual) producido por el enumerador.
        /// </summary>
        /// <remarks>
        /// Se expone como <see cref="BaseData"/> para integrarse con el pipeline de LEAN.
        /// Internamente siempre será un <see cref="TradeBar"/> cuando <see cref="MoveNext"/> devuelve true.
        /// </remarks>
        public BaseData Current { get; private set; }

        /// <summary>
        /// Elemento actual en la interfaz no genérica.
        /// </summary>
        object System.Collections.IEnumerator.Current => Current;

        /// <summary>
        /// Avanza al siguiente record y actualiza <see cref="Current"/>.
        /// </summary>
        /// <returns>
        /// True si se pudo producir una barra válida dentro del rango; de lo contrario false (fin del stream).
        /// </returns>
        public bool MoveNext()
        {
            return MoveNextRecord();
        }

        /// <summary>
        /// Libera recursos (MMF/accessor) utilizados por el reader.
        /// </summary>
        public void Dispose()
        {
            CloseCurrentFile();
        }

        /// <summary>
        /// Reset no se soporta porque el reader es forward-only y está pensado para streaming.
        /// </summary>
        /// <exception cref="NotSupportedException">Siempre.</exception>
        public void Reset()
        {
            throw new NotSupportedException($"{nameof(BinaryTradeBarSubscriptionReader)} es forward-only (no soporta Reset).");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Cierra el archivo actual (si existe) y abre el siguiente archivo de <see cref="_metas"/>.
        /// </summary>
        /// <returns>True si se abrió un archivo con records; false si no hay más archivos.</returns>
        private bool MoveToNextFile()
        {
            CloseCurrentFile();

            _metaIndex++;
            if (_metaIndex >= _metas.Count)
                return false;

            var meta = _metas[_metaIndex];

            // Abrir MMF y view accessor (solo lectura)
            _mmf = MemoryMappedFile.CreateFromFile(meta.FullPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            unsafe
            {
                _basePtr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);
            }

            _bytes = checked((int)_accessor.Capacity);
            _recordCount = _bytes / TradeBarRecordSize;
            _recordIndex = 0;

            // Si el archivo está vacío, lo tratamos como "sin data".
            // Nota: el caller (core) puede continuar al siguiente archivo.
            return _recordCount > 0;
        }

        /// <summary>
        /// Lee un record específico del archivo actual sin materializar buffers intermedios.
        /// </summary>
        /// <param name="index">Índice del record (0..recordCount-1).</param>
        /// <returns>El record binario interpretado como <see cref="TradeBarRecord"/>.</returns>
        private unsafe TradeBarRecord ReadRecord(int index)
        {
            var offset = index * TradeBarRecordSize;
            var span = new ReadOnlySpan<byte>(_basePtr + offset, TradeBarRecordSize);
            return MemoryMarshal.Cast<byte, TradeBarRecord>(span)[0];
        }

        /// <summary>
        /// Cierra y limpia el archivo actual, liberando punteros, accessor y MMF.
        /// </summary>
        private void CloseCurrentFile()
        {
            if (_accessor != null)
            {
                unsafe
                {
                    try
                    {
                        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    }
                    catch
                    {
                        // Best effort: evitamos romper el backtest por un dispose secundario.
                    }

                    _basePtr = null;
                }

                _accessor.Dispose();
                _accessor = null;
            }

            _mmf?.Dispose();
            _mmf = null;

            _bytes = 0;
            _recordIndex = 0;
            _recordCount = 0;
        }

        #endregion

        #region Core

        /// <summary>
        /// Implementación core de <see cref="MoveNext"/> (hot-path).
        /// </summary>
        /// <remarks>
        /// Reglas:
        /// - Si no hay archivo abierto, abre el siguiente.
        /// - Lee record por record.
        /// - Filtra por ventana [startUtc, endUtc).
        /// - Si se acaba el archivo, cierra y pasa al siguiente.
        ///
        /// Nota: En Paso 4 se puede implementar un "seek" inicial por timestamp para no escanear desde 0.
        /// </remarks>
        private bool MoveNextRecord()
        {
            while (true)
            {
                // 1) Asegurar que tenemos un archivo abierto
                if (_accessor == null)
                {
                    if (!MoveToNextFile())
                        return false; // no hay más archivos
                }

                // 2) Iterar records del archivo actual
                while (_recordIndex < _recordCount)
                {
                    var record = ReadRecord(_recordIndex++);
                    var time = DateTimeOffset.FromUnixTimeSeconds((long)record.Timestamp).UtcDateTime;

                    // Filtro por ventana [start,end)
                    if (time < _startUtc) continue;

                    if (time >= _endUtc)
                    {
                        // Este archivo ya no aportará data para el rango solicitado.
                        CloseCurrentFile();
                        break;
                    }

                    var period = _metas[_metaIndex].Period;

                    Current = new TradeBar
                    {
                        Time = time,
                        EndTime = time + period,
                        Period = period,
                        Symbol = _leanSymbol,
                        Open = (decimal)record.Open,
                        High = (decimal)record.High,
                        Low = (decimal)record.Low,
                        Close = (decimal)record.Close,
                        Volume = (decimal)record.Volume
                    };

                    return true;
                }

                // 3) Si agotamos el archivo actual, cerramos y seguimos el loop
                if (_recordIndex >= _recordCount)
                {
                    CloseCurrentFile();
                    // loop continúa y abrirá el siguiente archivo si existe
                }
            }
        }

        #endregion
    }
}