using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Util;
using HistoryRequest = QuantConnect.Data.HistoryRequest;

namespace QuantConnect.Lean.Engine.HistoricalData
{
    /// <summary>
    /// HistoryProvider que usa binarios locales (BinaryDataLoader) y respeta la tubería de LEAN:
    /// - Strict daily end times
    /// - Corporate events (inofensivo para FX/CFD/Crypto)
    /// - Fill-forward
    /// - Filtros de subscripción
    /// Importante: convierte las barras de UTC -> zona del exchange.
    /// </summary>
    public class BinaryHistoryProvider : SynchronizingHistoryProvider
    {
        private IMapFileProvider _mapFileProvider;
        private IFactorFileProvider _factorFileProvider;
        private IDataCacheProvider _dataCacheProvider;
        private IObjectStore _objectStore;
        private bool _parallel;
        private bool _initialized;

        protected IDataPermissionManager DataPermissionManager { get; set; }

        public override void Initialize(HistoryProviderInitializeParameters p)
        {
            if (_initialized) return;
            _initialized = true;

            _mapFileProvider      = p.MapFileProvider;
            _factorFileProvider   = p.FactorFileProvider;
            _dataCacheProvider    = p.DataCacheProvider;
            _objectStore          = p.ObjectStore;
            AlgorithmSettings     = p.AlgorithmSettings;
            DataPermissionManager = p.DataPermissionManager;
            _parallel             = p.ParallelHistoryRequestsEnabled;

            Log.Trace("BinaryHistoryProvider.Initialize(): ready");
        }

        public override IEnumerable<Slice> GetHistory(IEnumerable<HistoryRequest> requests, DateTimeZone sliceTimeZone)
        {
            var subs = new List<Subscription>(capacity: 16);

            foreach (var req in requests)
            {
                // Si aplica, puedes validar permisos por símbolo/fuente:
                // DataPermissionManager?.AssertConfiguration(req.ToSubscriptionDataConfig(), "binary");

                subs.Add(CreateBinarySubscription(req));
            }

            return CreateSliceEnumerableFromSubscriptions(subs, sliceTimeZone);
        }

        private Subscription CreateBinarySubscription(HistoryRequest req)
        {
            var config = req.ToSubscriptionDataConfig();

            // Security "mínimo" para filtros, exchange hours y fill-forward
            var security = new Security(
                req.ExchangeHours, config,
                new Cash(Currencies.NullCurrency, 0, 1m),
                SymbolProperties.GetDefault(Currencies.NullCurrency),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCache()
            );

            // 1) Build enumerator desde BIN + consolidación si target > nativo
            var reader = BuildBinaryEnumerator(req, out int rawCount);

            // 2) Convertir UTC -> Exchange TZ (clave para que no filtre todo a 0)
            reader = ConvertToExchangeTimeZone(reader, req);

            // 3) Strict daily end times cuando aplica
            var useDailyStrictEndTimes =
                LeanData.UseDailyStrictEndTimes(AlgorithmSettings, req, config.Symbol, config.Increment);
            if (useDailyStrictEndTimes)
            {
                reader = new StrictDailyEndTimesEnumerator(reader, req.ExchangeHours, req.StartTimeLocal);
            }

            // 4) Corporate events (harmless para FX/CFD/Crypto)
            reader = CorporateEventEnumeratorFactory.CreateEnumerators(
                reader, config, _factorFileProvider, /*raw*/ null, _mapFileProvider,
                req.StartTimeLocal, req.EndTimeLocal);

            // 5) Fill-Forward si corresponde
            if (req.FillForwardResolution.HasValue && req.DataType != typeof(Tick))
            {
                if (req.DataType == typeof(QuoteBar))
                    reader = new QuoteBarFillForwardEnumerator(reader);

                var ffRef    = Ref.CreateReadOnly(() => req.FillForwardResolution.Value.ToTimeSpan());
                var exchange = GetSecurityExchange(security.Exchange, req.DataType, req.Symbol);

                reader = new FillForwardEnumerator(
                    enumerator: reader,
                    exchange: exchange,
                    fillForwardResolution: ffRef,
                    isExtendedMarketHours: req.IncludeExtendedMarketHours,
                    subscriptionStartTime: req.StartTimeLocal,
                    subscriptionEndTime: req.EndTimeLocal,
                    dataResolution: config.Increment,
                    dataTimeZone: config.DataTimeZone,
                    dailyStrictEndTimeEnabled: useDailyStrictEndTimes,
                    dataType: req.DataType,
                    lastPointTracker: null
                );
            }

            // 6) Filtros LEAN + ventana temporal
            SubscriptionFilterEnumerator sfe = null;
            FilterEnumerator<BaseData>  fe  = null;

            try
            {
                // Nota: SubscriptionFilterEnumerator espera los timestamps en la zona del exchange (ya convertidos)
                sfe   = new SubscriptionFilterEnumerator(reader, security, req.EndTimeLocal, config.ExtendedMarketHours, liveMode: false, req.ExchangeHours);
                reader = sfe;

                // Filtro adicional por ventana temporal, evita colarse algo fuera de [StartLocal,EndLocal]
                if (config.Resolution != Resolution.Tick)
                {
                    var tb = new TimeWindowFilter(req);
                    fe = new FilterEnumerator<BaseData>(reader, tb.Filter);
                    reader = fe;
                }

                var subReq = new SubscriptionRequest(false, null, security, config, req.StartTimeUtc, req.EndTimeUtc);

                var sub = _parallel
                    ? SubscriptionUtils.CreateAndScheduleWorker(subReq, reader, _factorFileProvider, /*useMapping*/ false, AlgorithmSettings.DailyPreciseEndTime)
                    : SubscriptionUtils.Create(subReq, reader, AlgorithmSettings.DailyPreciseEndTime);

                // Si necesitas diagnosticar:
                // Log.Trace($"BinaryHistoryProvider: raw={rawCount}");

                sfe = null;
                fe  = null;
                return sub;
            }
            finally
            {
                fe?.Dispose();
                sfe?.Dispose();
            }
        }

        /// <summary>
        /// Lectura de binarios + consolidación (si target &gt; base). Devuelve enumerator en UTC.
        /// </summary>
        private static IEnumerator<BaseData> BuildBinaryEnumerator(HistoryRequest req, out int rawCount)
        {
            rawCount = 0;

            var symbolStr = req.Symbol.Value;
            var startUtc  = req.StartTimeUtc;
            var endUtc    = req.EndTimeUtc;
            var target    = req.Resolution.ToTimeSpan();

            if (req.DataType == typeof(QuoteBar))
            {
                var baseBars = BinaryDataLoader.LoadAsQuoteBars(symbolStr, startUtc, endUtc);
                rawCount = baseBars.Count;
                if (rawCount == 0) return Enumerable.Empty<BaseData>().GetEnumerator();

                var basePeriod = baseBars.First().Period;
                IEnumerable<BaseData> outBars = (target > basePeriod)
                    ? ConsolidateQuoteBars(baseBars, target, req.Symbol)
                    : baseBars.Cast<BaseData>();

                return outBars.GetEnumerator();
            }

            if (req.DataType == typeof(TradeBar))
            {
                var baseBars = BinaryDataLoader.LoadAsTradeBars(symbolStr, startUtc, endUtc);
                rawCount = baseBars.Count;
                if (rawCount == 0) return Enumerable.Empty<BaseData>().GetEnumerator();

                var basePeriod = baseBars.First().Period;
                IEnumerable<BaseData> outBars = (target > basePeriod)
                    ? ConsolidateTradeBars(baseBars, target, req.Symbol)
                    : baseBars.Cast<BaseData>();

                return outBars.GetEnumerator();
            }

            return Enumerable.Empty<BaseData>().GetEnumerator();
        }

        /// <summary>
        /// Convierte las barras de UTC → zona del exchange (req.ExchangeHours.TimeZone).
        /// </summary>
        private static IEnumerator<BaseData> ConvertToExchangeTimeZone(IEnumerator<BaseData> source, HistoryRequest req)
        {
            var exTz = req.ExchangeHours.TimeZone; // destino

            IEnumerable<BaseData> Convert()
            {
                while (source.MoveNext())
                {
                    var d = source.Current;
                    if (d == null) { yield return null; continue; }

                    // Convertimos in-place (TradeBar/QuoteBar son clases mutables)
                    var newTime    = d.Time.ConvertFromUtc(exTz);
                    var newEndTime = d.EndTime.ConvertFromUtc(exTz);

                    if (d is TradeBar tb)
                    {
                        tb.Time    = newTime;
                        tb.EndTime = newEndTime;
                        yield return tb;
                    }
                    else if (d is QuoteBar qb)
                    {
                        qb.Time    = newTime;
                        qb.EndTime = newEndTime;
                        yield return qb;
                    }
                    else
                    {
                        d.Time    = newTime;
                        d.EndTime = newEndTime;
                        yield return d;
                    }
                }
            }

            return Convert().GetEnumerator();
        }

        // ---------- Consolidadores (5m -> 15m, 5m -> 1h, etc.) ----------
        private static IEnumerable<BaseData> ConsolidateTradeBars(IEnumerable<TradeBar> src, TimeSpan target, Symbol symbol)
        {
            var bucket = new List<TradeBar>();
            DateTime? bucketEnd = null;

            foreach (var b in src.OrderBy(x => x.EndTime))
            {
                if (bucketEnd == null)
                    bucketEnd = AlignUp(b.EndTime, target);

                if (b.EndTime <= bucketEnd.Value)
                {
                    bucket.Add(b);
                    continue;
                }

                if (bucket.Count > 0)
                    yield return AggregateTrade(bucket, bucketEnd.Value, symbol);

                bucket.Clear();
                bucketEnd = AlignUp(b.EndTime, target);
                bucket.Add(b);
            }

            if (bucket.Count > 0 && bucketEnd != null)
                yield return AggregateTrade(bucket, bucketEnd.Value, symbol);
        }

        private static IEnumerable<BaseData> ConsolidateQuoteBars(IEnumerable<QuoteBar> src, TimeSpan target, Symbol symbol)
        {
            var bucket = new List<QuoteBar>();
            DateTime? bucketEnd = null;

            foreach (var b in src.OrderBy(x => x.EndTime))
            {
                if (bucketEnd == null)
                    bucketEnd = AlignUp(b.EndTime, target);

                if (b.EndTime <= bucketEnd.Value)
                {
                    bucket.Add(b);
                    continue;
                }

                if (bucket.Count > 0)
                    yield return AggregateQuote(bucket, bucketEnd.Value, symbol);

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
                open:   w.First().Open,
                high:   w.Max(x => x.High),
                low:    w.Min(x => x.Low),
                close:  w.Last().Close,
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

        /// <summary>
        /// Filtro temporal estricto para [StartLocal, EndLocal].
        /// </summary>
        private sealed class TimeWindowFilter
        {
            public DateTime EndLocal { get; }
            public DateTime StartLocal { get; }
            public Type RequestedType { get; }

            public TimeWindowFilter(HistoryRequest r)
            {
                RequestedType = r.DataType;
                EndLocal      = r.EndTimeLocal;
                StartLocal    = r.StartTimeLocal;
            }

            public bool Filter(BaseData d)
            {
                // Evitar auxiliares si no son del tipo pedido
                if (d.DataType == MarketDataType.Auxiliary && d.GetType() != RequestedType) return false;
                if (d.EndTime > EndLocal) return false;
                return d.EndTime > StartLocal;
            }
        }
    }
}