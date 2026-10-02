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
            WriteQuoteFile(1, "1M", "20240701", "20240702",
                Quote(startUtc.AddMinutes(-1), 900, 99, 101),
                Quote(startUtc, 901, 100, 102),
                Quote(startUtc.AddMinutes(1), 902, 101, 103),
                Quote(startUtc.AddMinutes(2), 903, 102, 104));
            var provider = CreateProvider(parallel);
            var request = CreateRequest(
                startUtc,
                startUtc.AddMinutes(2),
                typeof(QuoteBar),
                Resolution.Minute,
                null,
                TickType.Quote);

            var bars = provider.GetHistory(new[] { request }, TimeZones.Utc)
                .SelectMany(slice => slice.QuoteBars.Values)
                .ToList();

            Assert.AreEqual(2, bars.Count);
            Assert.AreEqual(new DateTime(2024, 7, 1, 8, 0, 0), bars[0].Time);
            Assert.AreEqual(new DateTime(2024, 7, 1, 8, 1, 0), bars[0].EndTime);
            Assert.AreEqual(101m, bars[0].Value);
            Assert.AreEqual(bars[0].Close, bars[0].Value);
            Assert.AreNotEqual(901m, bars[0].Value);
            Assert.AreSame(_symbol, bars[0].Symbol);
        }

        [Test]
        public void StreamsMixedPhysicalTradeBarHistoryAfterLogicalProjection()
        {
            var startUtc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            WriteTradeFile(1, "1M", "20240701", "20240702",
                Trade(startUtc, 10, 12, 9, 11, 1),
                Trade(startUtc.AddMinutes(2), 12, 14, 11, 13, 3));
            WriteQuoteFile(2, "1M", "20240701", "20240702",
                Quote(startUtc.AddMinutes(1), 12, 100, 102, globalOpen: 11, globalHigh: 13, globalLow: 10, volume: 2),
                Quote(startUtc.AddMinutes(2), 13, 200, 202, globalOpen: 12, globalHigh: 14, globalLow: 11, volume: 3));
            var provider = CreateProvider(false);
            var request = CreateRequest(
                startUtc,
                startUtc.AddMinutes(3),
                typeof(TradeBar),
                Resolution.Minute,
                null,
                TickType.Trade);

            var bars = provider.GetHistory(new[] { request }, TimeZones.Utc)
                .SelectMany(slice => slice.Bars.Values)
                .ToList();

            Assert.AreEqual(3, bars.Count);
            CollectionAssert.AreEqual(new[] { 11m, 12m, 13m }, bars.Select(bar => bar.Close));
        }

        [Test]
        public void AppliesNativeHistoryFillForwardWrappers()
        {
            var startUtc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            WriteQuoteFile(1, "1M", "20240701", "20240702",
                Quote(startUtc, 900, 99, 101),
                Quote(startUtc.AddMinutes(10), 910, 109, 111));
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
            Assert.IsTrue(bars.All(bar => bar.Value == bar.Close));
        }

        [Test]
        public void ReturnsStreamingConsolidatedHistory()
        {
            var startUtc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            WriteTradeFile(1, "5M", "20240701", "20240702",
                Enumerable.Range(0, 12)
                    .Select(index => Trade(startUtc.AddMinutes(index * 5), 100 + index, 111 + index, 99, 101 + index, 1))
                    .ToArray());
            var provider = CreateProvider(false);
            var request = CreateRequest(
                startUtc,
                startUtc.AddHours(1),
                typeof(TradeBar),
                Resolution.Hour,
                null,
                TickType.Trade);

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

        [Test]
        public void HistoryRejectsFinerThanNativeRequest()
        {
            var startUtc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            WriteTradeFile(1, "5M", "20240701", "20240702", Trade(startUtc, 10, 12, 9, 11, 1));
            var provider = CreateProvider(false);
            var request = CreateRequest(
                startUtc,
                startUtc.AddMinutes(5),
                typeof(TradeBar),
                Resolution.Minute,
                null,
                TickType.Trade);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                provider.GetHistory(new[] { request }, TimeZones.Utc).ToList());
            StringAssert.Contains("finer", exception.Message);
        }

        [Test]
        public void EarlyHistoryTerminationDisposesMappedBinaryFile()
        {
            var startUtc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            var path = WriteQuoteFile(1, "1M", "20240701", "20240702",
                Quote(startUtc, 900, 99, 101),
                Quote(startUtc.AddMinutes(1), 901, 100, 102));
            var provider = CreateProvider(false);
            var request = CreateRequest(
                startUtc,
                startUtc.AddMinutes(2),
                typeof(QuoteBar),
                Resolution.Minute,
                null,
                TickType.Quote);

            using (var enumerator = provider.GetHistory(new[] { request }, TimeZones.Utc).GetEnumerator())
            {
                Assert.IsTrue(enumerator.MoveNext());
            }

            var moved = path + ".moved";
            File.Move(path, moved);
            Assert.IsTrue(File.Exists(moved));
        }

        [Test]
        public void HistoryPreservesBothNewYorkDstTransitions()
        {
            var forwardFirst = new DateTime(2024, 3, 10, 6, 59, 0, DateTimeKind.Utc);
            WriteQuoteFile(1, "1M", "20240310", "20240311",
                Quote(forwardFirst, 900, 99, 101),
                Quote(forwardFirst.AddMinutes(1), 901, 100, 102));
            var provider = CreateProvider(false);
            var forwardRequest = CreateRequest(
                forwardFirst,
                forwardFirst.AddMinutes(2),
                typeof(QuoteBar),
                Resolution.Minute,
                null,
                TickType.Quote);
            var forward = provider.GetHistory(new[] { forwardRequest }, TimeZones.Utc)
                .SelectMany(slice => slice.QuoteBars.Values)
                .ToList();
            CollectionAssert.AreEqual(
                new[] { new DateTime(2024, 3, 10, 1, 59, 0), new DateTime(2024, 3, 10, 3, 0, 0) },
                forward.Select(bar => bar.Time));

            DeleteBinaryFiles();
            // The native history lifecycle filters in exchange-local time, so span the
            // transition without asking it to order two intrinsically ambiguous 01:xx values.
            // Exact repeated-hour UTC ordering is covered at the binary factory seam.
            var backwardFirst = new DateTime(2024, 11, 3, 4, 59, 0, DateTimeKind.Utc);
            WriteQuoteFile(1, "1M", "20241103", "20241104",
                Quote(backwardFirst, 900, 99, 101),
                Quote(new DateTime(2024, 11, 3, 7, 0, 0, DateTimeKind.Utc), 901, 100, 102));
            provider = CreateProvider(false);
            var backwardRequest = CreateRequest(
                new DateTime(2024, 11, 3, 4, 30, 0, DateTimeKind.Utc),
                new DateTime(2024, 11, 3, 7, 30, 0, DateTimeKind.Utc),
                typeof(QuoteBar),
                Resolution.Minute,
                null,
                TickType.Quote);
            var backward = provider.GetHistory(new[] { backwardRequest }, TimeZones.Utc)
                .SelectMany(slice => slice.QuoteBars.Values)
                .ToList();
            Assert.AreEqual(2, backward.Count);
            Assert.AreEqual(new DateTime(2024, 11, 3, 0, 59, 0), backward[0].Time);
            Assert.AreEqual(new DateTime(2024, 11, 3, 2, 0, 0), backward[1].Time);
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

        private string WriteTradeFile(
            int index,
            string period,
            string from,
            string to,
            params TradeRecord[] records)
        {
            var path = CreatePath(index, period, from, to, "TB");
            using var writer = new BinaryWriter(File.Create(path));
            foreach (var record in records)
            {
                writer.Write((double)new DateTimeOffset(record.Time).ToUnixTimeSeconds());
                writer.Write(record.Open);
                writer.Write(record.High);
                writer.Write(record.Low);
                writer.Write(record.Close);
                writer.Write(record.Volume);
                writer.Write(0);
            }
            return path;
        }

        private string WriteQuoteFile(
            int index,
            string period,
            string from,
            string to,
            params QuoteRecord[] records)
        {
            var path = CreatePath(index, period, from, to, "QB");
            using var writer = new BinaryWriter(File.Create(path));
            foreach (var record in records)
            {
                writer.Write((double)new DateTimeOffset(record.Time).ToUnixTimeSeconds());
                writer.Write(record.GlobalOpen);
                writer.Write(record.GlobalHigh);
                writer.Write(record.GlobalLow);
                writer.Write(record.GlobalClose);
                writer.Write(record.Volume);
                writer.Write(record.BidClose - 1);
                writer.Write(record.BidClose + 1);
                writer.Write(record.BidClose - 2);
                writer.Write(record.BidClose);
                writer.Write(record.AskClose - 1);
                writer.Write(record.AskClose + 1);
                writer.Write(record.AskClose - 2);
                writer.Write(record.AskClose);
                writer.Write(123u);
            }
            return path;
        }

        private string CreatePath(int index, string period, string from, string to, string suffix)
        {
            var directory = Path.Combine(_dataPath, "FX_EURUSD_test_bin_data");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, $"{index}-FX_EURUSD_{period}_{from}-{to}_{suffix}.bin");
        }

        private void DeleteBinaryFiles()
        {
            foreach (var file in Directory.EnumerateFiles(_dataPath, "*.bin", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
        }

        private static TradeRecord Trade(DateTime time, float open, float high, float low, float close, float volume)
        {
            return new TradeRecord(time, open, high, low, close, volume);
        }

        private static QuoteRecord Quote(
            DateTime time,
            float globalClose,
            float bidClose,
            float askClose,
            float? globalOpen = null,
            float? globalHigh = null,
            float? globalLow = null,
            float volume = 1)
        {
            return new QuoteRecord(
                time,
                globalOpen ?? globalClose,
                globalHigh ?? globalClose + 1,
                globalLow ?? globalClose - 1,
                globalClose,
                volume,
                bidClose,
                askClose);
        }

        private sealed record TradeRecord(DateTime Time, float Open, float High, float Low, float Close, float Volume);

        private sealed record QuoteRecord(
            DateTime Time,
            float GlobalOpen,
            float GlobalHigh,
            float GlobalLow,
            float GlobalClose,
            float Volume,
            float BidClose,
            float AskClose);
    }
}
