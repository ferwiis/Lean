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
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;

namespace QuantConnect.Lean.Engine.HistoricalData
{
    /// <summary>
    /// Native subscription-reader history pipeline backed by the streaming Auroboros binary source.
    /// </summary>
    public class BinaryHistoryProvider : SubscriptionDataReaderHistoryProvider
    {
        private readonly BinarySubscriptionEnumeratorFactory _binaryFactory;

        /// <summary>
        /// Creates a provider using the default binary data root.
        /// </summary>
        public BinaryHistoryProvider()
            : this(new BinarySubscriptionEnumeratorFactory())
        {
        }

        /// <summary>
        /// Creates a provider using the supplied binary source factory.
        /// </summary>
        public BinaryHistoryProvider(BinarySubscriptionEnumeratorFactory binaryFactory)
        {
            _binaryFactory = binaryFactory ?? throw new ArgumentNullException(nameof(binaryFactory));
        }

        /// <summary>
        /// Replaces only the raw reader. The base provider retains current LEAN strict daily end times,
        /// corporate events, fill-forward, subscription filtering, parallel scheduling, and slice synchronization.
        /// </summary>
        protected override IEnumerator<BaseData> CreateDataReader(
            SubscriptionDataConfig config,
            HistoryRequest request,
            out SubscriptionDataReader subscriptionDataReader)
        {
            subscriptionDataReader = null;
            return _binaryFactory.CreateEnumerator(request);
        }
    }
}
