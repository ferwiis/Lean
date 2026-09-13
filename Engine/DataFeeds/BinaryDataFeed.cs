/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Generic;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Packets;
using QuantConnect.Util;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// File-system data feed whose normal TradeBar and QuoteBar subscriptions are sourced from
    /// forward-only Auroboros binary readers. Universe subscriptions retain native LEAN behavior.
    /// </summary>
    public class BinaryDataFeed : FileSystemDataFeed
    {
        private readonly BinarySubscriptionEnumeratorFactory _binaryFactory;
        private IAlgorithm _algorithm;
        private IMapFileProvider _mapFileProvider;
        private IFactorFileProvider _factorFileProvider;

        /// <summary>
        /// Creates a feed using the default binary data root.
        /// </summary>
        public BinaryDataFeed()
            : this(new BinarySubscriptionEnumeratorFactory())
        {
        }

        /// <summary>
        /// Creates a feed using the supplied binary source factory.
        /// </summary>
        public BinaryDataFeed(BinarySubscriptionEnumeratorFactory binaryFactory)
        {
            _binaryFactory = binaryFactory ?? throw new ArgumentNullException(nameof(binaryFactory));
        }

        /// <summary>
        /// Initializes the binary feed and the standard file-system subscription infrastructure.
        /// </summary>
        public override void Initialize(
            IAlgorithm algorithm,
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
            _mapFileProvider = mapFileProvider;
            _factorFileProvider = factorFileProvider;
            base.Initialize(algorithm, job, resultHandler, mapFileProvider, factorFileProvider, dataProvider,
                subscriptionManager, dataFeedTimeProvider, dataChannelProvider);
        }

        /// <summary>
        /// Replaces only the raw normal-subscription source; the base class still owns warmup,
        /// LastPointTracker, fill-forward, filters, schedules, universe routing, and subscription lifetime.
        /// </summary>
        protected override IEnumerator<BaseData> CreateUnderlyingDataEnumerator(SubscriptionRequest request)
        {
            IEnumerator<BaseData> enumerator = _binaryFactory.CreateEnumerator(request, null);

            if (LeanData.UseDailyStrictEndTimes(
                _algorithm.Settings,
                request,
                request.Configuration.Symbol,
                request.Configuration.Increment))
            {
                enumerator = new StrictDailyEndTimesEnumerator(enumerator, request.ExchangeHours, request.StartTimeLocal);
            }

            return CorporateEventEnumeratorFactory.CreateEnumerators(
                enumerator,
                request.Configuration,
                _factorFileProvider,
                null,
                _mapFileProvider,
                request.StartTimeLocal,
                request.EndTimeLocal,
                enablePriceScaling: false);
        }
    }
}
