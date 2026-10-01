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
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Packets;
using QuantConnect.Tests.Common.Securities;

namespace QuantConnect.Tests.Engine.DataFeeds
{
    [TestFixture]
    public class BinaryDataFeedTests
    {
        private string _dataPath;

        [SetUp]
        public void SetUp()
        {
            _dataPath = Path.Combine(Path.GetTempPath(), "lean-binary-feed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dataPath))
            {
                Directory.Delete(_dataPath, true);
            }
        }

        [Test]
        public void PreservesNativeWarmupToNormalLifecycleWithChangedResolution()
        {
            var records = new List<QuoteRecord>();
            var firstUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var index = 0; index < 108; index++)
            {
                var time = firstUtc.AddHours(index);
                // Deliberately leave a gap at the normal-feed boundary so native fill-forward
                // and LastPointTracker behavior is exercised across the warmup pivot.
                if (time == new DateTime(2024, 1, 3, 5, 0, 0, DateTimeKind.Utc)
                    || time == new DateTime(2024, 1, 3, 6, 0, 0, DateTimeKind.Utc))
                {
                    continue;
                }

                var mid = 1.10f + index / 10000f;
                records.Add(new QuoteRecord(time, mid - 0.0001f, mid + 0.0001f));
            }
            WriteQuoteFile(records);

            var feed = new BinaryDataFeed(new BinarySubscriptionEnumeratorFactory(_dataPath));
            var algorithm = new AlgorithmStub(feed);
            algorithm.Transactions.SetOrderProcessor(new FakeOrderProcessor());
            algorithm.SetStartDate(new DateTime(2024, 1, 3));
            algorithm.SetEndDate(new DateTime(2024, 1, 4));

            var dataPermissionManager = new DataPermissionManager();
            using var synchronizer = new Synchronizer();
            synchronizer.Initialize(algorithm, algorithm.DataManager, new());

            feed.Initialize(
                algorithm,
                new BacktestNodePacket(),
                new BacktestingResultHandler(),
                TestGlobals.MapFileProvider,
                TestGlobals.FactorFileProvider,
                TestGlobals.DataProvider,
                algorithm.DataManager,
                synchronizer,
                dataPermissionManager.DataChannelProvider);

            var security = algorithm.AddForex("EURUSD", Resolution.Hour, Market.FXCM, fillForward: true);
            algorithm.SetWarmup(1, Resolution.Daily);
            algorithm.PostInitialize();

            var warmupBars = new List<QuoteBar>();
            var normalBars = new List<QuoteBar>();
            var sliceTimes = new List<DateTime>();
            try
            {
                using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                foreach (var timeSlice in synchronizer.StreamData(cancellationTokenSource.Token))
                {
                    if (timeSlice.IsTimePulse || !timeSlice.Slice.QuoteBars.TryGetValue(security.Symbol, out var quoteBar))
                    {
                        continue;
                    }

                    sliceTimes.Add(timeSlice.Time);
                    Assert.AreSame(security.Symbol, quoteBar.Symbol);
                    Assert.AreEqual(quoteBar.Close, quoteBar.Value);
                    if (timeSlice.Slice.Time <= algorithm.StartDate)
                    {
                        warmupBars.Add(quoteBar);
                    }
                    else
                    {
                        normalBars.Add(quoteBar);
                    }
                }
            }
            finally
            {
                feed.Exit();
                algorithm.DataManager.RemoveAllSubscriptions();
            }

            Assert.IsNotEmpty(warmupBars);
            Assert.IsNotEmpty(normalBars);
            Assert.IsTrue(
                warmupBars.Any(bar => bar.Period == Time.OneDay),
                $"Warmup periods: {string.Join(", ", warmupBars.Select(bar => bar.Period))}");
            Assert.IsTrue(normalBars.Any(bar => bar.Period == Time.OneHour));
            Assert.IsTrue(normalBars.Any(bar => bar.IsFillForward));
            Assert.AreEqual(algorithm.StartDate, normalBars[0].Time, "The first normal logical point was missing.");
            Assert.IsTrue(sliceTimes.Zip(sliceTimes.Skip(1), (left, right) => left < right).All(inOrder => inOrder));
            Assert.AreEqual(
                warmupBars.Concat(normalBars).Select(bar => bar.EndTime).Distinct().Count(),
                warmupBars.Count + normalBars.Count,
                "The warmup/normal pivot emitted duplicate logical bars.");
        }

        [Test]
        public void DataFeedRawSeamProjectsAndMergesQuoteRowsForTradeRequest()
        {
            var first = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            WriteQuoteFile(1, new[]
            {
                new QuoteRecord(first, 1.0f, 1.2f),
                new QuoteRecord(first.AddHours(2), 1.2f, 1.4f)
            });
            WriteQuoteFile(2, new[]
            {
                new QuoteRecord(first.AddHours(1), 1.1f, 1.3f),
                new QuoteRecord(first.AddHours(2), 1.2f, 1.4f)
            });

            var feed = new TestBinaryDataFeed(new BinarySubscriptionEnumeratorFactory(_dataPath));
            var algorithm = new AlgorithmStub(feed);
            var dataPermissionManager = new DataPermissionManager();
            using var synchronizer = new Synchronizer();
            synchronizer.Initialize(algorithm, algorithm.DataManager, new());
            feed.Initialize(
                algorithm,
                new BacktestNodePacket(),
                new BacktestingResultHandler(),
                TestGlobals.MapFileProvider,
                TestGlobals.FactorFileProvider,
                TestGlobals.DataProvider,
                algorithm.DataManager,
                synchronizer,
                dataPermissionManager.DataChannelProvider);

            var security = algorithm.AddForex("EURUSD", Resolution.Hour, Market.FXCM, fillForward: false);
            var config = new SubscriptionDataConfig(
                typeof(TradeBar),
                security.Symbol,
                Resolution.Hour,
                TimeZones.Utc,
                TimeZones.Utc,
                false,
                false,
                false,
                tickType: TickType.Trade,
                dataNormalizationMode: DataNormalizationMode.Raw);
            var request = new SubscriptionRequest(
                false,
                null,
                security,
                config,
                first,
                first.AddHours(3));

            var bars = new List<TradeBar>();
            try
            {
                using var enumerator = feed.CreateBinarySource(request);
                while (enumerator.MoveNext())
                {
                    bars.Add((TradeBar)enumerator.Current);
                }
            }
            finally
            {
                feed.Exit();
                algorithm.DataManager.RemoveAllSubscriptions();
            }

            Assert.AreEqual(3, bars.Count);
            CollectionAssert.AreEqual(
                new[] { first, first.AddHours(1), first.AddHours(2) },
                bars.Select(bar => DateTime.SpecifyKind(bar.Time, DateTimeKind.Utc)));
            Assert.IsTrue(bars.All(bar => ReferenceEquals(security.Symbol, bar.Symbol)));
        }

        private void WriteQuoteFile(IEnumerable<QuoteRecord> records)
        {
            WriteQuoteFile(1, records);
        }

        private void WriteQuoteFile(int index, IEnumerable<QuoteRecord> records)
        {
            var directory = Path.Combine(_dataPath, "FX_EURUSD_test_bin_data");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{index}-FX_EURUSD_1H_20240101-20240106_QB.bin");
            using var writer = new BinaryWriter(File.Create(path));
            foreach (var record in records)
            {
                writer.Write((double)new DateTimeOffset(record.TimeUtc).ToUnixTimeSeconds());
                var open = (record.Bid + record.Ask) / 2;
                writer.Write(open);
                writer.Write(open);
                writer.Write(open);
                writer.Write(open);
                writer.Write(0f);
                writer.Write(record.Bid);
                writer.Write(record.Bid);
                writer.Write(record.Bid);
                writer.Write(record.Bid);
                writer.Write(record.Ask);
                writer.Write(record.Ask);
                writer.Write(record.Ask);
                writer.Write(record.Ask);
                writer.Write(0u);
            }
        }

        private sealed record QuoteRecord(DateTime TimeUtc, float Bid, float Ask);

        private sealed class TestBinaryDataFeed : BinaryDataFeed
        {
            public TestBinaryDataFeed(BinarySubscriptionEnumeratorFactory factory)
                : base(factory)
            {
            }

            public IEnumerator<BaseData> CreateBinarySource(SubscriptionRequest request)
            {
                return CreateUnderlyingDataEnumerator(request);
            }
        }
    }
}
