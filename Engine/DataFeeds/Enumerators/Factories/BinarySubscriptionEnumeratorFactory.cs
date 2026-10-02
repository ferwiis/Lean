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

using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Interfaces;

namespace QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories
{
    /// <summary>
    /// Creates the canonical forward-only source used by binary feeds and binary history.
    /// </summary>
    public class BinarySubscriptionEnumeratorFactory : ISubscriptionEnumeratorFactory
    {
        private readonly string _dataPath;

        /// <summary>
        /// Creates a factory using the default binary data root.
        /// </summary>
        public BinarySubscriptionEnumeratorFactory()
        {
        }

        /// <summary>
        /// Creates a factory using a specific binary data root. Intended for isolated testing and tooling.
        /// </summary>
        public BinarySubscriptionEnumeratorFactory(string dataPath)
        {
            _dataPath = dataPath;
        }

        /// <summary>
        /// Creates a binary source from a data-feed request.
        /// </summary>
        public IEnumerator<BaseData> CreateEnumerator(SubscriptionRequest request, IDataProvider dataProvider)
        {
            return CreateEnumerator(
                request.Configuration.Symbol,
                request.Configuration.Type,
                request.Configuration.Increment,
                request.StartTimeUtc,
                request.EndTimeUtc,
                request.Configuration.ExchangeTimeZone);
        }

        /// <summary>
        /// Creates a binary source from a history request.
        /// </summary>
        public IEnumerator<BaseData> CreateEnumerator(HistoryRequest request)
        {
            return CreateEnumerator(
                request.Symbol,
                request.DataType,
                request.Resolution.ToTimeSpan(),
                request.StartTimeUtc,
                request.EndTimeUtc,
                request.ExchangeHours.TimeZone);
        }

        /// <summary>
        /// Creates a binary stream, converts UTC record times to exchange time, and consolidates upward when needed.
        /// </summary>
        public IEnumerator<BaseData> CreateEnumerator(
            Symbol symbol,
            Type dataType,
            TimeSpan requestedPeriod,
            DateTime startUtc,
            DateTime endUtc,
            DateTimeZone exchangeTimeZone)
        {
            if (symbol == null)
            {
                throw new ArgumentNullException(nameof(symbol));
            }
            if (exchangeTimeZone == null)
            {
                throw new ArgumentNullException(nameof(exchangeTimeZone));
            }
            if (symbol.SecurityType != SecurityType.Forex)
            {
                throw new NotSupportedException(
                    $"Auroboros binary historical data currently supports Forex symbols only. Received {symbol.SecurityType} '{symbol}'.");
            }
            if (dataType != typeof(TradeBar) && dataType != typeof(QuoteBar))
            {
                throw new NotSupportedException(
                    $"Auroboros binary historical data supports {nameof(TradeBar)} and {nameof(QuoteBar)} only. Received '{dataType}'.");
            }

            var plan = BinaryFileResolver.ResolveRequest(
                symbol.Value,
                dataType,
                requestedPeriod,
                startUtc,
                endUtc,
                _dataPath);
            if (plan.Files.Count == 0)
            {
                return Enumerable.Empty<BaseData>().GetEnumerator();
            }

            var nativePeriod = plan.NativePeriod.Value;

            IEnumerator<BaseData> enumerator = dataType == typeof(TradeBar)
                ? new BinaryTradeBarSubscriptionReader(symbol, plan.Files, startUtc, endUtc)
                : new BinaryQuoteBarSubscriptionReader(symbol, plan.Files, startUtc, endUtc);

            // Native TradeBar/QuoteBar readers convert the bar start into exchange time and
            // retain Period; EndTime remains Time + Period. Do the same instead of converting
            // the UTC end instant separately, which would distort Period at DST boundaries.
            enumerator = new BinaryDataTimeZoneEnumerator(enumerator, exchangeTimeZone);

            if (requestedPeriod > nativePeriod)
            {
                enumerator = new BinaryDataConsolidatingEnumerator(enumerator, requestedPeriod);
            }

            return enumerator;
        }

        private sealed class BinaryDataTimeZoneEnumerator : IEnumerator<BaseData>
        {
            private readonly IEnumerator<BaseData> _underlying;
            private readonly DateTimeZone _exchangeTimeZone;

            public BinaryDataTimeZoneEnumerator(IEnumerator<BaseData> underlying, DateTimeZone exchangeTimeZone)
            {
                _underlying = underlying;
                _exchangeTimeZone = exchangeTimeZone;
            }

            public BaseData Current { get; private set; }

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (!_underlying.MoveNext())
                {
                    Current = null;
                    return false;
                }

                Current = _underlying.Current;
                if (Current != null)
                {
                    var utcTime = Current.Time;
                    Current.Time = utcTime.ConvertFromUtc(_exchangeTimeZone);
                }
                return true;
            }

            public void Reset()
            {
                _underlying.Reset();
            }

            public void Dispose()
            {
                _underlying.Dispose();
            }
        }

        /// <summary>
        /// Uses a single mutable aggregation bucket, so memory is independent of request length.
        /// A final partial bucket is emitted with its aligned requested end time, matching the legacy binary contract.
        /// </summary>
        private sealed class BinaryDataConsolidatingEnumerator : IEnumerator<BaseData>
        {
            private readonly IEnumerator<BaseData> _underlying;
            private readonly TimeSpan _period;
            private BaseData _working;
            private DateTime _bucketEnd;
            private bool _sourceExhausted;

            public BinaryDataConsolidatingEnumerator(IEnumerator<BaseData> underlying, TimeSpan period)
            {
                _underlying = underlying;
                _period = period;
            }

            public BaseData Current { get; private set; }

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                Current = null;
                if (_sourceExhausted)
                {
                    return EmitFinalBucket();
                }

                while (_underlying.MoveNext())
                {
                    var data = _underlying.Current;
                    if (data == null)
                    {
                        continue;
                    }

                    if (_working == null)
                    {
                        StartBucket(data);
                        continue;
                    }

                    if (data.EndTime <= _bucketEnd)
                    {
                        Aggregate(data);
                        continue;
                    }

                    Current = CompleteBucket();
                    StartBucket(data);
                    return true;
                }

                _sourceExhausted = true;
                return EmitFinalBucket();
            }

            public void Reset()
            {
                throw new NotSupportedException($"{nameof(BinaryDataConsolidatingEnumerator)} is forward-only.");
            }

            public void Dispose()
            {
                _working = null;
                Current = null;
                _underlying.Dispose();
            }

            private void StartBucket(BaseData data)
            {
                _bucketEnd = AlignUp(data.EndTime, _period);
                if (data is TradeBar tradeBar)
                {
                    _working = new TradeBar(
                        tradeBar.Time,
                        tradeBar.Symbol,
                        tradeBar.Open,
                        tradeBar.High,
                        tradeBar.Low,
                        tradeBar.Close,
                        tradeBar.Volume,
                        _bucketEnd - tradeBar.Time);
                }
                else if (data is QuoteBar quoteBar)
                {
                    _working = new QuoteBar(
                        quoteBar.Time,
                        quoteBar.Symbol,
                        quoteBar.Bid,
                        quoteBar.LastBidSize,
                        quoteBar.Ask,
                        quoteBar.LastAskSize,
                        _bucketEnd - quoteBar.Time)
                    {
                        EndTime = _bucketEnd
                    };
                }
                else
                {
                    throw new NotSupportedException($"Cannot consolidate binary data type '{data.GetType()}'.");
                }
            }

            private void Aggregate(BaseData data)
            {
                if (_working is TradeBar aggregateTrade && data is TradeBar tradeBar)
                {
                    aggregateTrade.High = Math.Max(aggregateTrade.High, tradeBar.High);
                    aggregateTrade.Low = Math.Min(aggregateTrade.Low, tradeBar.Low);
                    aggregateTrade.Close = tradeBar.Close;
                    aggregateTrade.Volume += tradeBar.Volume;
                    return;
                }

                if (_working is QuoteBar aggregateQuote && data is QuoteBar quoteBar)
                {
                    aggregateQuote.Bid = AggregateBar(aggregateQuote.Bid, quoteBar.Bid);
                    aggregateQuote.Ask = AggregateBar(aggregateQuote.Ask, quoteBar.Ask);
                    aggregateQuote.LastBidSize = quoteBar.LastBidSize;
                    aggregateQuote.LastAskSize = quoteBar.LastAskSize;
                    aggregateQuote.Value = aggregateQuote.Close;
                    return;
                }

                throw new InvalidOperationException("A binary stream changed data type inside one consolidation bucket.");
            }

            private BaseData CompleteBucket()
            {
                _working.EndTime = _bucketEnd;
                if (_working is TradeBar tradeBar)
                {
                    tradeBar.Period = _bucketEnd - tradeBar.Time;
                }
                else if (_working is QuoteBar quoteBar)
                {
                    quoteBar.Period = _bucketEnd - quoteBar.Time;
                    quoteBar.Value = quoteBar.Close;
                }
                return _working;
            }

            private bool EmitFinalBucket()
            {
                if (_working == null)
                {
                    return false;
                }

                Current = CompleteBucket();
                _working = null;
                return true;
            }

            private static DateTime AlignUp(DateTime time, TimeSpan period)
            {
                var remainder = time.Ticks % period.Ticks;
                return remainder == 0 ? time : time.AddTicks(period.Ticks - remainder);
            }

            private static Bar CopyBar(IBar bar)
            {
                return bar == null ? null : new Bar(bar.Open, bar.High, bar.Low, bar.Close);
            }

            private static Bar AggregateBar(Bar aggregate, IBar current)
            {
                if (current == null)
                {
                    return aggregate;
                }
                if (aggregate == null)
                {
                    return CopyBar(current);
                }

                aggregate.High = Math.Max(aggregate.High, current.High);
                aggregate.Low = Math.Min(aggregate.Low, current.Low);
                aggregate.Close = current.Close;
                return aggregate;
            }
        }
    }
}
