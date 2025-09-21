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
using System.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.HistoricalData;
using QuantConnect.Securities;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Engine.HistoricalData
{
    [TestFixture, NonParallelizable]
    public class BinaryHistoryProviderTests : BinaryBatchTestBase
    {
        [TestCase(false)]
        [TestCase(true)]
        public void StreamsQuoteHistoryWithNativeValueRangeTimezoneAndParallelSemantics(bool parallel)
        {
            WriteFile(true, 1, "1M", (First.AddMinutes(-1), 9), (First, 10), (First.AddMinutes(1), 11), (First.AddMinutes(2), 12));
            var provider = new BinaryHistoryProvider();
            provider.Initialize(new HistoryProviderInitializeParameters(null, null, TestGlobals.DataProvider,
                TestGlobals.DataCacheProvider, TestGlobals.MapFileProvider, TestGlobals.FactorFileProvider,
                null, parallel, new DataPermissionManager(), null, new AlgorithmSettings()));
            var request = new HistoryRequest(First, First.AddMinutes(2), typeof(QuoteBar), TestSymbol,
                Resolution.Minute, SecurityExchangeHours.AlwaysOpen(TimeZones.NewYork), TimeZones.Utc,
                null, false, false, DataNormalizationMode.Raw, TickType.Quote);
            var bars = provider.GetHistory(new[] { request }, TimeZones.Utc)
                .SelectMany(slice => slice.QuoteBars.Values).ToList();
            Assert.AreEqual(2, bars.Count);
            Assert.AreEqual(new DateTime(2024, 7, 1, 8, 0, 0), bars[0].Time);
            Assert.AreEqual(new DateTime(2024, 7, 1, 8, 1, 0), bars[0].EndTime);
            CollectionAssert.AreEqual(new[] { 11m, 12m }, bars.Select(bar => bar.Value));
            Assert.AreEqual(TestSymbol.Value, bars[0].Symbol.Value);
        }
    }
}
