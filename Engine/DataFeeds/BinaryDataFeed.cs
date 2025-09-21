using System;
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Configuration;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Data;
using QuantConnect.Data.Fundamental;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Util;

/**
 * 📦 BinaryDataFeed.cs
 * DataFeed que lee .bin (BinaryDataLoader) respetando "bar-type" y "timeframe" del config.json.
 * Mantiene la tubería LEAN: warmup, fill-forward, filtros y schedules de universos.
 */
namespace QuantConnect.Lean.Engine.DataFeeds
{
    public class BinaryDataFeed : IDataFeed
    {
        #pragma warning disable CA1305
        #pragma warning disable CA1716
        #pragma warning disable CA2000

        private IAlgorithm _algorithm;
        private ITimeProvider _timeProvider;
        private IResultHandler _resultHandler;
        private IMapFileProvider _mapFileProvider;
        private IFactorFileProvider _factorFileProvider;
        private IDataProvider _dataProvider;
        private IDataCacheProvider _cacheProvider;
        private SubscriptionCollection _subscriptions;
        private MarketHoursDatabase _marketHoursDatabase;
        private SubscriptionDataReaderSubscriptionEnumeratorFactory _subscriptionFactory;

        public bool IsActive { get; private set; }

        public virtual void Initialize(IAlgorithm algorithm,
            AlgorithmNodePacket job,
            IResultHandler resultHandler,
            IMapFileProvider mapFileProvider,
            IFactorFileProvider factorFileProvider,
            IDataProvider dataProvider,
            IDataFeedSubscriptionManager subscriptionManager,
            IDataFeedTimeProvider dataFeedTimeProvider,
            IDataChannelProvider dataChannelProvider)
        {
            _algorithm = algorithm;
            _resultHandler = resultHandler;
            _mapFileProvider = mapFileProvider;
            _factorFileProvider = factorFileProvider;
            _dataProvider = dataProvider;
            _timeProvider = dataFeedTimeProvider.FrontierTimeProvider;
            _subscriptions = subscriptionManager.DataFeedSubscriptions;
            _cacheProvider = new ZipDataCacheProvider(dataProvider, isDataEphemeral: false);
            _subscriptionFactory = new SubscriptionDataReaderSubscriptionEnumeratorFactory(
                _resultHandler,
                _mapFileProvider,
                _factorFileProvider,
                _cacheProvider,
                algorithm,
                enablePriceScaling: false);

            IsActive = true;
            _marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
        }

        protected IEnumerator<BaseData> CreateEnumerator(SubscriptionRequest request, Resolution? fillForwardResolution = null)
        {
            return request.IsUniverseSubscription
                ? CreateUniverseEnumerator(request, CreateDataEnumerator, fillForwardResolution)
                : CreateDataEnumerator(request, fillForwardResolution);
        }

        private IEnumerator<BaseData> CreateDataEnumerator(SubscriptionRequest request, Resolution? fillForwardResolution)
        {
            var barType = Enum.Parse<BarDataMode>(Config.Get("bar-type"));
            var period  = TimeSpan.Parse(Config.Get("timeframe"));
            var symbol  = request.Configuration.Symbol;
            var start   = request.StartTimeUtc;
            var end     = request.EndTimeUtc;

            IEnumerator<BaseData> adapter = barType switch
            {
                BarDataMode.TradeBar => new TradeBarEnumeratorAdapter(
                    BinaryDataLoader.LoadAsTradeBars(symbol.Value, period, start, end)?.GetEnumerator()
                    ?? Enumerable.Empty<TradeBar>().GetEnumerator()),

                BarDataMode.QuoteBar => new QuoteBarEnumeratorAdapter(
                    BinaryDataLoader.LoadAsQuoteBars(symbol.Value, period, start, end)?.GetEnumerator()
                    ?? Enumerable.Empty<QuoteBar>().GetEnumerator()),

                _ => throw new NotSupportedException($"BarDataMode '{barType}' no soportado.")
            };

            return ConfigureEnumerator(request, false, adapter, fillForwardResolution);
        }

        public Subscription CreateSubscription(SubscriptionRequest request)
        {
            IEnumerator<BaseData> enumerator;

            if (_algorithm.IsWarmingUp)
            {
                var pivotTimeUtc = _algorithm.StartDate.ConvertToUtc(_algorithm.TimeZone);

                var warmupRequest = new SubscriptionRequest(request, endTimeUtc: pivotTimeUtc,
                    configuration: new SubscriptionDataConfig(request.Configuration, resolution: _algorithm.Settings.WarmupResolution));

                IEnumerator<BaseData> warmupEnumerator = null;
                if (warmupRequest.TradableDaysInDataTimeZone.Any()
                    && LeanData.IsValidConfiguration(warmupRequest.Configuration.SecurityType, warmupRequest.Configuration.Resolution, warmupRequest.Configuration.TickType))
                {
                    pivotTimeUtc = Time.GetStartTimeForTradeBars(request.Security.Exchange.Hours,
                        _algorithm.StartDate.ConvertTo(_algorithm.TimeZone, request.Security.Exchange.TimeZone),
                        Time.OneDay, 1, false, warmupRequest.Configuration.DataTimeZone,
                        LeanData.UseDailyStrictEndTimes(_algorithm.Settings, request, request.Security.Symbol, Time.OneDay))
                        .ConvertToUtc(request.Security.Exchange.TimeZone);

                    if (pivotTimeUtc < warmupRequest.StartTimeUtc)
                        pivotTimeUtc = warmupRequest.StartTimeUtc;

                    warmupEnumerator = CreateEnumerator(warmupRequest, _algorithm.Settings.WarmupResolution);
                    warmupEnumerator = new FilterEnumerator<BaseData>(warmupEnumerator, data => data == null || data.EndTime <= warmupRequest.EndTimeLocal);
                }

                var normalEnumerator = CreateEnumerator(new SubscriptionRequest(request, startTimeUtc: pivotTimeUtc), null);
                normalEnumerator = new FilterEnumerator<BaseData>(normalEnumerator, data => data == null || data.EndTime >= warmupRequest.EndTimeLocal);
                enumerator = new ConcatEnumerator(true, warmupEnumerator, normalEnumerator);
            }
            else
            {
                enumerator = CreateEnumerator(request, null);
            }

            enumerator = AddScheduleWrapper(request, enumerator);

            if (request.IsUniverseSubscription && request.Universe is UserDefinedUniverse)
            {
                return SubscriptionUtils.Create(request, enumerator, _algorithm.Settings.DailyPreciseEndTime);
            }
            return SubscriptionUtils.CreateAndScheduleWorker(request, enumerator, factorFileProvider: null, true, _algorithm.Settings.DailyPreciseEndTime);
        }

        public virtual void RemoveSubscription(Subscription subscription) { }

        protected IEnumerator<BaseData> CreateUniverseEnumerator(SubscriptionRequest request, Func<SubscriptionRequest, Resolution?, IEnumerator<BaseData>> createUnderlyingEnumerator, Resolution? fillForwardResolution = null)
        {
            ISubscriptionEnumeratorFactory factory = _subscriptionFactory;
            if (request.Universe is ITimeTriggeredUniverse)
            {
                factory = new TimeTriggeredUniverseSubscriptionEnumeratorFactory(request.Universe as ITimeTriggeredUniverse,
                    _marketHoursDatabase,
                    _timeProvider);
            }
            else if (request.Configuration.Type == typeof(FundamentalUniverse))
            {
                factory = new BaseDataCollectionSubscriptionEnumeratorFactory(_algorithm.ObjectStore);
            }

            var enumerator = factory.CreateEnumerator(request, _dataProvider);
            return enumerator;
        }

        private IEnumerator<BaseData> AddScheduleWrapper(SubscriptionRequest request, IEnumerator<BaseData> enumerator)
        {
            if (!request.IsUniverseSubscription || !request.Universe.UniverseSettings.Schedule.Initialized)
            {
                return enumerator;
            }

            var schedule = request.Universe.UniverseSettings.Schedule.Get(request.StartTimeLocal, request.EndTimeLocal);
            return schedule != null
                ? new ScheduledEnumerator(enumerator, schedule, _timeProvider, request.Configuration.ExchangeTimeZone, request.StartTimeLocal)
                : enumerator;
        }

        private IEnumerator<BaseData> ConfigureEnumerator(SubscriptionRequest request, bool aggregate, IEnumerator<BaseData> enumerator, Resolution? fillForwardResolution)
        {
            if (aggregate)
            {
                enumerator = new BaseDataCollectionAggregatorEnumerator(enumerator, request.Configuration.Symbol);
            }

            enumerator = TryAddFillForwardEnumerator(request, enumerator, request.Configuration.FillDataForward, fillForwardResolution);

            if (request.Configuration.IsFilteredSubscription)
            {
                enumerator = SubscriptionFilterEnumerator.WrapForDataFeed(_resultHandler, enumerator, request.Security,
                    request.EndTimeLocal, request.Configuration.ExtendedMarketHours, false, request.ExchangeHours);
            }

            return enumerator;
        }

        private IEnumerator<BaseData> TryAddFillForwardEnumerator(SubscriptionRequest request, IEnumerator<BaseData> enumerator, bool fillForward, Resolution? fillForwardResolution)
        {
            if (fillForward && request.Configuration.Resolution != Resolution.Tick)
            {
                if (request.Configuration.Type == typeof(QuoteBar))
                {
                    enumerator = new QuoteBarFillForwardEnumerator(enumerator);
                }

                var fillForwardSpan = _subscriptions.UpdateAndGetFillForwardResolution(request.Configuration);
                if (fillForwardResolution != null && fillForwardResolution != Resolution.Tick)
                {
                    fillForwardSpan = Ref.Create(fillForwardResolution.Value.ToTimeSpan());
                }

                var useDailyStrictEndTimes = LeanData.UseDailyStrictEndTimes(
                    _algorithm.Settings,
                    request,
                    request.Configuration.Symbol,
                    request.Configuration.Increment,
                    request.Security.Exchange.Hours
                );

                enumerator = new FillForwardEnumerator(
                    enumerator: enumerator,
                    exchange: request.Security.Exchange,
                    fillForwardResolution: fillForwardSpan,
                    isExtendedMarketHours: request.Configuration.ExtendedMarketHours,
                    subscriptionStartTime: request.StartTimeLocal,
                    subscriptionEndTime: request.EndTimeLocal,
                    dataResolution: request.Configuration.Increment,
                    dataTimeZone: request.Configuration.DataTimeZone,
                    dailyStrictEndTimeEnabled: useDailyStrictEndTimes,
                    dataType: request.Configuration.Type,
                    lastPointTracker: null
                );
            }
            return enumerator;
        }

        public virtual void Exit()
        {
            if (IsActive)
            {
                IsActive = false;
                Log.Trace("BinaryDataFeed.Exit(): Start. Setting cancellation token...");

                _subscriptionFactory?.DisposeSafely();
                _cacheProvider.DisposeSafely();

                Log.Trace("BinaryDataFeed.Exit(): Cleared BinaryDataLoader cache.");
                Log.Trace("BinaryDataFeed.Exit(): Exit Finished.");
            }
        }

        private class TradeBarEnumeratorAdapter : IEnumerator<BaseData>
        {
            private readonly IEnumerator<TradeBar> _inner;
            public TradeBarEnumeratorAdapter(IEnumerator<TradeBar> inner) => _inner = inner;
            public BaseData Current => _inner.Current;
            object System.Collections.IEnumerator.Current => _inner.Current;
            public void Dispose() => _inner.Dispose();
            public bool MoveNext() => _inner.MoveNext();
            public void Reset() => _inner.Reset();
        }

        private class QuoteBarEnumeratorAdapter : IEnumerator<BaseData>
        {
            private readonly IEnumerator<QuoteBar> _inner;
            public QuoteBarEnumeratorAdapter(IEnumerator<QuoteBar> inner) => _inner = inner;
            public BaseData Current => _inner.Current;
            object System.Collections.IEnumerator.Current => _inner.Current;
            public void Dispose() => _inner.Dispose();
            public bool MoveNext() => _inner.MoveNext();
            public void Reset() => _inner.Reset();
        }
    }
}