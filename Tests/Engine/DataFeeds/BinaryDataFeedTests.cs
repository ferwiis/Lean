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
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Tests.Common.Securities;
using QuantConnect.Lean.Engine;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Packets;

namespace QuantConnect.Tests.Engine.DataFeeds
{
    [TestFixture, NonParallelizable]
    public class BinaryDataFeedTests
    {
        private string _dataPath;
        private static readonly DateTime First = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        private static readonly Symbol TestSymbol = Symbol.Create("EURUSD", SecurityType.Forex, Market.FXCM);

        [SetUp]
        public void SetUp()
        {
            _dataPath = Path.Combine(Path.GetTempPath(), "lean-binary-feed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, true);
        }

        private void WriteSelectedBarFile(bool quote, params (DateTime Time, float Open)[] records)
        {
            var directory = Path.Combine(_dataPath, "FX_EURUSD_test_bin_data");
            Directory.CreateDirectory(directory);
            var suffix = "";
            var file = Path.Combine(directory, $"1-FX_EURUSD_1M_20240701-20240702{suffix}.bin");
            using var writer = new BinaryWriter(File.Create(file));
            foreach (var record in records)
            {
                writer.Write((double)new DateTimeOffset(record.Time).ToUnixTimeSeconds());
                writer.Write(record.Open);
                writer.Write(record.Open + 2);
                writer.Write(record.Open - 1);
                writer.Write(record.Open + 1);
                writer.Write(1f);
                if (quote)
                {
                    for (var side = 0; side < 2; side++)
                    {
                        var offset = side == 0 ? -1 : 1;
                        writer.Write(record.Open + offset);
                        writer.Write(record.Open + 2 + offset);
                        writer.Write(record.Open - 1 + offset);
                        writer.Write(record.Open + 1 + offset);
                    }
                    writer.Write(2f);
                }
                else writer.Write(0);
            }
            Assert.AreEqual(records.Length * (quote ? 64 : 32), writer.BaseStream.Length);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DataFeedReadsSelectedBarType(bool quote)
        {
            WriteSelectedBarFile(quote, (First, 10), (First.AddMinutes(1), 11), (First.AddMinutes(2), 12));
            var previousBarType = Config.Get("bar-type");
            var previousTimeframe = Config.Get("timeframe");
            var feed = new TestBinaryDataFeed(new BinarySubscriptionEnumeratorFactory(_dataPath));
            var algorithm = new AlgorithmStub(feed);
            using var synchronizer = new Synchronizer();
            synchronizer.Initialize(algorithm, algorithm.DataManager, new());
            Config.Set("bar-type", quote ? "QuoteBar" : "TradeBar");
            Config.Set("timeframe", "00:01:00");
            try
            {
                var permission = new DataPermissionManager();
                feed.Initialize(algorithm, new BacktestNodePacket(), new BacktestingResultHandler(),
                    TestGlobals.MapFileProvider, TestGlobals.FactorFileProvider, TestGlobals.DataProvider,
                    algorithm.DataManager, synchronizer, permission.DataChannelProvider);
                var security = algorithm.AddForex(TestSymbol.Value, Resolution.Minute, Market.FXCM, fillForward: false);
                var config = new SubscriptionDataConfig(quote ? typeof(QuoteBar) : typeof(TradeBar),
                    security.Symbol, Resolution.Minute, TimeZones.Utc, TimeZones.Utc, false, false, false,
                    isFilteredSubscription: false, tickType: quote ? TickType.Quote : TickType.Trade, dataNormalizationMode: DataNormalizationMode.Raw);
                var request = new SubscriptionRequest(false, null, security, config, First, First.AddMinutes(3));
                using var enumerator = feed.CreateBinarySource(request);
                var bars = new List<BaseData>();
                while (enumerator.MoveNext()) bars.Add(enumerator.Current);
                Assert.AreEqual(3, bars.Count);
                Assert.IsInstanceOf(quote ? typeof(QuoteBar) : typeof(TradeBar), bars[0]);
                Assert.AreEqual(First, bars[0].Time);
                Assert.AreEqual(13m, bars[2].Value);
            }
            finally
            {
                feed.Exit();
                algorithm.DataManager.RemoveAllSubscriptions();
                Config.Set("bar-type", previousBarType);
                Config.Set("timeframe", previousTimeframe);
            }
        }

        private sealed class TestBinaryDataFeed : BinaryDataFeed
        {
            public TestBinaryDataFeed(BinarySubscriptionEnumeratorFactory factory) : base(factory) { }
            public IEnumerator<BaseData> CreateBinarySource(SubscriptionRequest request) => CreateUnderlyingDataEnumerator(request);
        }
    }
}
