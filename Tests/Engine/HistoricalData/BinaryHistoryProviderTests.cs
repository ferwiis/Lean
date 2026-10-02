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
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;
using QuantConnect.Lean.Engine.HistoricalData;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Engine.HistoricalData
{
    [TestFixture]
    public class BinaryHistoryProviderTests
    {
        private string _dataPath;
        private Symbol _symbol;

        [SetUp]
        public void SetUp()
        {
            _dataPath = Path.Combine(Path.GetTempPath(), "lean-binary-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataPath);
            _symbol = Symbol.Create("EURUSD", SecurityType.Forex, Market.FXCM);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dataPath))
            {
                Directory.Delete(_dataPath, true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StreamsQuoteHistoryWithNativeValueRangeTimezoneAndParallelSemantics(bool parallel)
        {
            var startUtc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            WriteQuoteFile(
                startUtc.AddMinutes(-5),
                startUtc,
                startUtc.AddMinutes(5),
                startUtc.AddMinutes(10));
            var provider = CreateProvider(parallel);
            var request = CreateRequest(startUtc, startUtc.AddMinutes(10), typeof(QuoteBar), Resolution.Minute, null, TickType.Quote);

            var bars = provider.GetHistory(new[] { request }, TimeZones.Utc)
                .SelectMany(slice => slice.QuoteBars.Values)
                .ToList();

            Assert.AreEqual(2, bars.Count);
            Assert.AreEqual(new DateTime(2024, 7, 1, 8, 0, 0), bars[0].Time);
            Assert.AreEqual(new DateTime(2024, 7, 1, 8, 5, 0), bars[0].EndTime);
            Assert.AreEqual(new DateTime(2024, 7, 1, 8, 5, 0), bars[1].Time);
            Assert.AreEqual(104m, bars[1].Value);
            Assert.AreEqual(103m, bars[1].Bid.Close);
            Assert.AreEqual(105m, bars[1].Ask.Close);
        }

        [Test]
        public void AppliesHistoryFillForwardWrappers()
        {
            var startUtc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            WriteQuoteFile(startUtc, startUtc.AddMinutes(10));
            var provider = CreateProvider(false);
            var request = CreateRequest(
                startUtc,
                startUtc.AddMinutes(11),
                typeof(QuoteBar),
                Resolution.Minute,
                Resolution.Minute,
                TickType.Quote);

            var bars = provider.GetHistory(new[] { request }, TimeZones.Utc)
                .SelectMany(slice => slice.QuoteBars.Values)
                .ToList();

            Assert.IsTrue(bars.Any(bar => bar.IsFillForward));
            Assert.IsTrue(bars.Any(bar => !bar.IsFillForward));
            Assert.IsTrue(bars.Zip(bars.Skip(1), (left, right) => left.EndTime < right.EndTime).All(inOrder => inOrder));
        }

        [Test]
        public void ReturnsStreamingConsolidatedHistory()
        {
            var startUtc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            var records = Enumerable.Range(0, 12).Select(index => startUtc.AddMinutes(index * 5)).ToArray();
            WriteTradeFile(records);
            var provider = CreateProvider(false);
            var request = CreateRequest(startUtc, startUtc.AddHours(1), typeof(TradeBar), Resolution.Hour, null, TickType.Trade);

            var bars = provider.GetHistory(new[] { request }, TimeZones.Utc)
                .SelectMany(slice => slice.Bars.Values)
                .ToList();

            Assert.AreEqual(1, bars.Count);
            Assert.AreEqual(new DateTime(2024, 7, 1, 8, 0, 0), bars[0].Time);
            Assert.AreEqual(new DateTime(2024, 7, 1, 9, 0, 0), bars[0].EndTime);
            Assert.AreEqual(100m, bars[0].Open);
            Assert.AreEqual(122m, bars[0].High);
            Assert.AreEqual(99m, bars[0].Low);
            Assert.AreEqual(112m, bars[0].Close);
            Assert.AreEqual(12m, bars[0].Volume);
        }

        private BinaryHistoryProvider CreateProvider(bool parallel)
        {
            var provider = new BinaryHistoryProvider(new BinarySubscriptionEnumeratorFactory(_dataPath));
            provider.Initialize(new HistoryProviderInitializeParameters(
                null,
                null,
                TestGlobals.DataProvider,
                TestGlobals.DataCacheProvider,
                TestGlobals.MapFileProvider,
                TestGlobals.FactorFileProvider,
                null,
                parallel,
                new DataPermissionManager(),
                null,
                new AlgorithmSettings()));
            return provider;
        }

        private HistoryRequest CreateRequest(
            DateTime startUtc,
            DateTime endUtc,
            Type dataType,
            Resolution resolution,
            Resolution? fillForwardResolution,
            TickType tickType)
        {
            return new HistoryRequest(
                startUtc,
                endUtc,
                dataType,
                _symbol,
                resolution,
                SecurityExchangeHours.AlwaysOpen(TimeZones.NewYork),
                TimeZones.Utc,
                fillForwardResolution,
                false,
                false,
                DataNormalizationMode.Raw,
                tickType);
        }

        private void WriteQuoteFile(params DateTime[] times)
        {
            var path = CreatePath("5M");
            using var writer = new BinaryWriter(File.Create(path));
            for (var index = 0; index < times.Length; index++)
            {
                var value = 100f + index;
                writer.Write((double)new DateTimeOffset(times[index]).ToUnixTimeSeconds());
                writer.Write(value);
                writer.Write(value + 3);
                writer.Write(value - 1);
                writer.Write(value + 2);
                writer.Write(1f);
                writer.Write(value - 1);
                writer.Write(value + 2);
                writer.Write(value - 2);
                writer.Write(value + 1);
                writer.Write(value + 1);
                writer.Write(value + 4);
                writer.Write(value);
                writer.Write(value + 3);
                writer.Write(2f);
            }
        }

        private void WriteTradeFile(params DateTime[] times)
        {
            var path = CreatePath("5M");
            using var writer = new BinaryWriter(File.Create(path));
            for (var index = 0; index < times.Length; index++)
            {
                var value = 100f + index;
                writer.Write((double)new DateTimeOffset(times[index]).ToUnixTimeSeconds());
                writer.Write(value);
                writer.Write(value + 11);
                writer.Write(99f);
                writer.Write(value + 1);
                writer.Write(1f);
                writer.Write(0);
            }
        }

        private string CreatePath(string period)
        {
            var directory = Path.Combine(_dataPath, "FX_EURUSD_test_bin_data");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, $"1-FX_EURUSD_{period}_20240701-20240702.bin");
        }
    }
}
