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
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;

namespace QuantConnect.Tests.Engine.DataFeeds
{
    [TestFixture]
    public class BinarySubscriptionEnumeratorFactoryTests
    {
        private const string SymbolValue = "EURUSD";
        private string _dataPath;
        private BinarySubscriptionEnumeratorFactory _factory;
        private Symbol _symbol;

        [SetUp]
        public void SetUp()
        {
            _dataPath = Path.Combine(Path.GetTempPath(), "lean-binary-reader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataPath);
            _factory = new BinarySubscriptionEnumeratorFactory(_dataPath);
            _symbol = Symbol.Create(SymbolValue, SecurityType.Forex, Market.FXCM);
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
        public void StreamsTradeBarsAcrossFilesWithExactBoundariesEmptyFileAndDedupe()
        {
            CreateTradeFile(1,
                Trade(Minute(0), 10, 12, 9, 11, 1),
                Trade(Minute(5), 11, 14, 10, 13, 2));
            CreateEmptyFile(2, "5M");
            CreateTradeFile(3,
                Trade(Minute(5), 101, 102, 100, 101, 100),
                Trade(Minute(10), 13, 15, 8, 9, 3),
                Trade(Minute(15), 9, 10, 7, 8, 4));

            var bars = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(5), Minute(15));

            Assert.AreEqual(2, bars.Count);
            Assert.AreEqual(new[] { Minute(5), Minute(10) }, bars.Select(bar => AsUtc(bar.Time)));
            Assert.AreEqual(11m, bars[0].Open);
            Assert.AreEqual(13m, bars[0].Close);
            Assert.AreEqual(13m, bars[1].Open);
            Assert.AreEqual(9m, bars[1].Close);
            Assert.AreEqual(TimeSpan.FromMinutes(5), bars[0].Period);
            Assert.AreSame(_symbol, bars[0].Symbol);
        }

        [Test]
        public void StreamsQuoteBarsUsingVerifiedRecordContract()
        {
            CreateEmptyFile(1, "5M");
            CreateQuoteFile(2,
                Quote(Minute(0), 100, 104, 99, 103, 7, 99, 103, 98, 102, 101, 105, 100, 104, 2),
                Quote(Minute(5), 103, 106, 101, 105, 8, 102, 105, 100, 104, 104, 107, 102, 106, 2));
            CreateQuoteFile(3,
                Quote(Minute(5), 999, 999, 999, 999, 999, 999, 999, 999, 999, 999, 999, 999, 999, 0),
                Quote(Minute(10), 105, 108, 104, 107, 9, 104, 107, 103, 106, 106, 109, 105, 108, 2));

            var bars = Read<QuoteBar>(typeof(QuoteBar), TimeSpan.FromMinutes(5), Minute(0), Minute(15));

            Assert.AreEqual(3, bars.Count);
            Assert.AreEqual(105m, bars[1].Value, "QuoteBar.Value must use the producer's absolute close field.");
            Assert.AreEqual(102m, bars[1].Bid.Open);
            Assert.AreEqual(105m, bars[1].Bid.High);
            Assert.AreEqual(100m, bars[1].Bid.Low);
            Assert.AreEqual(104m, bars[1].Bid.Close);
            Assert.AreEqual(104m, bars[1].Ask.Open);
            Assert.AreEqual(107m, bars[1].Ask.High);
            Assert.AreEqual(102m, bars[1].Ask.Low);
            Assert.AreEqual(106m, bars[1].Ask.Close);
            Assert.AreSame(_symbol, bars[1].Symbol);
        }

        [Test]
        public void ConvertsUtcTimestampsToNonUtcExchangeTimeBeforeEmission()
        {
            var utc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            CreateTradeFile(1, Trade(utc, 10, 12, 9, 11, 1), "20240701", "20240702");

            using var enumerator = _factory.CreateEnumerator(
                _symbol,
                typeof(TradeBar),
                TimeSpan.FromMinutes(5),
                utc,
                utc.AddMinutes(10),
                TimeZones.NewYork);

            Assert.IsTrue(enumerator.MoveNext());
            Assert.AreEqual(new DateTime(2024, 7, 1, 8, 0, 0), enumerator.Current.Time);
            Assert.AreEqual(new DateTime(2024, 7, 1, 8, 5, 0), enumerator.Current.EndTime);
        }

        [Test]
        public void ConsolidatesTradeBarsAcrossPhysicalFileBoundaryWithPartialFinalBucket()
        {
            CreateTradeFile(1,
                Trade(Minute(0), 10, 12, 9, 11, 1),
                Trade(Minute(5), 11, 14, 10, 13, 2));
            CreateTradeFile(2,
                Trade(Minute(5), 100, 100, 100, 100, 100),
                Trade(Minute(10), 13, 15, 8, 9, 3),
                Trade(Minute(15), 9, 10, 7, 8, 4));

            var bars = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(15), Minute(0), Minute(20));

            Assert.AreEqual(2, bars.Count);
            Assert.AreEqual(Minute(0), AsUtc(bars[0].Time));
            Assert.AreEqual(Minute(15), AsUtc(bars[0].EndTime));
            Assert.AreEqual(10m, bars[0].Open);
            Assert.AreEqual(15m, bars[0].High);
            Assert.AreEqual(8m, bars[0].Low);
            Assert.AreEqual(9m, bars[0].Close);
            Assert.AreEqual(6m, bars[0].Volume);
            Assert.AreEqual(TimeSpan.FromMinutes(15), bars[0].Period);

            Assert.AreEqual(Minute(15), AsUtc(bars[1].Time));
            Assert.AreEqual(Minute(30), AsUtc(bars[1].EndTime));
            Assert.AreEqual(4m, bars[1].Volume);
            Assert.AreEqual(TimeSpan.FromMinutes(15), bars[1].Period);
        }

        [Test]
        public void ConsolidatesQuoteBarsAcrossPhysicalFileBoundary()
        {
            CreateQuoteFile(1,
                Quote(Minute(0), 100, 104, 99, 103, 1, 99, 103, 98, 102, 101, 105, 100, 104, 2),
                Quote(Minute(5), 103, 106, 101, 105, 1, 102, 105, 100, 104, 104, 107, 102, 106, 2));
            CreateQuoteFile(2,
                Quote(Minute(5), 999, 999, 999, 999, 1, 999, 999, 999, 999, 999, 999, 999, 999, 0),
                Quote(Minute(10), 105, 108, 97, 107, 1, 104, 107, 96, 106, 106, 109, 98, 108, 2));

            var bars = Read<QuoteBar>(typeof(QuoteBar), TimeSpan.FromMinutes(15), Minute(0), Minute(15));

            Assert.AreEqual(1, bars.Count);
            Assert.AreEqual(107m, bars[0].Value);
            Assert.AreEqual(99m, bars[0].Bid.Open);
            Assert.AreEqual(107m, bars[0].Bid.High);
            Assert.AreEqual(96m, bars[0].Bid.Low);
            Assert.AreEqual(106m, bars[0].Bid.Close);
            Assert.AreEqual(101m, bars[0].Ask.Open);
            Assert.AreEqual(109m, bars[0].Ask.High);
            Assert.AreEqual(98m, bars[0].Ask.Low);
            Assert.AreEqual(108m, bars[0].Ask.Close);
        }

        [Test]
        public void PreservesCoarserNativePeriodWhenRequestIsFiner()
        {
            CreateTradeFile(1, Trade(Minute(0), 10, 12, 9, 11, 1));

            var bars = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(1), Minute(0), Minute(10));

            Assert.AreEqual(1, bars.Count);
            Assert.AreEqual(TimeSpan.FromMinutes(5), bars[0].Period);
        }

        [Test]
        public void RejectsMalformedRecordLengthAndNonChronologicalRecords()
        {
            var malformed = CreateEmptyFile(1, "5M");
            File.WriteAllBytes(malformed, new byte[] { 1 });
            using (var enumerator = _factory.CreateEnumerator(
                _symbol, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(20), TimeZones.Utc))
            {
                Assert.Throws<InvalidDataException>(() => enumerator.MoveNext());
            }

            File.Delete(malformed);
            CreateTradeFile(2,
                Trade(Minute(5), 10, 12, 9, 11, 1),
                Trade(Minute(0), 11, 13, 10, 12, 2));
            using var unordered = _factory.CreateEnumerator(
                _symbol, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(20), TimeZones.Utc);
            Assert.IsTrue(unordered.MoveNext());
            Assert.Throws<InvalidDataException>(() => unordered.MoveNext());
        }

        [Test]
        public void QuoteReaderRejectsMalformedRecordLengthAndNonChronologicalRecords()
        {
            var malformed = CreateEmptyFile(1, "5M");
            File.WriteAllBytes(malformed, new byte[] { 1 });
            using (var enumerator = _factory.CreateEnumerator(
                _symbol, typeof(QuoteBar), TimeSpan.FromMinutes(5), Minute(0), Minute(20), TimeZones.Utc))
            {
                Assert.Throws<InvalidDataException>(() => enumerator.MoveNext());
            }

            File.Delete(malformed);
            CreateQuoteFile(2,
                Quote(Minute(5), 100, 104, 99, 103, 1, 99, 103, 98, 102, 101, 105, 100, 104, 2),
                Quote(Minute(0), 103, 106, 101, 105, 1, 102, 105, 100, 104, 104, 107, 102, 106, 2));
            using var unordered = _factory.CreateEnumerator(
                _symbol, typeof(QuoteBar), TimeSpan.FromMinutes(5), Minute(0), Minute(20), TimeZones.Utc);
            Assert.IsTrue(unordered.MoveNext());
            Assert.Throws<InvalidDataException>(() => unordered.MoveNext());
        }

        [Test]
        public void EarlyDisposeReleasesMappedFile()
        {
            var path = CreateTradeFile(1, Trade(Minute(0), 10, 12, 9, 11, 1));
            var enumerator = _factory.CreateEnumerator(
                _symbol, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(10), TimeZones.Utc);

            Assert.IsTrue(enumerator.MoveNext());
            enumerator.Dispose();

            var movedPath = path + ".moved";
            File.Move(path, movedPath);
            Assert.IsTrue(File.Exists(movedPath));
        }

        [Test]
        public void ReturnsEmptyForNoMatchingFilesAndRejectsUnsupportedRequests()
        {
            using var empty = _factory.CreateEnumerator(
                _symbol, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(10), TimeZones.Utc);
            Assert.IsFalse(empty.MoveNext());

            Assert.Throws<NotSupportedException>(() => _factory.CreateEnumerator(
                _symbol, typeof(Tick), TimeSpan.FromMinutes(5), Minute(0), Minute(10), TimeZones.Utc));

            var equity = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            Assert.Throws<NotSupportedException>(() => _factory.CreateEnumerator(
                equity, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(10), TimeZones.Utc));
        }

        private List<T> Read<T>(Type dataType, TimeSpan requestedPeriod, DateTime startUtc, DateTime endUtc)
            where T : BaseData
        {
            using var enumerator = _factory.CreateEnumerator(
                _symbol, dataType, requestedPeriod, startUtc, endUtc, TimeZones.Utc);
            var data = new List<T>();
            while (enumerator.MoveNext())
            {
                data.Add((T)enumerator.Current);
            }
            return data;
        }

        private string CreateTradeFile(int index, params TradeRecord[] records)
        {
            return CreateTradeFile(index, records, "20240101", "20240102");
        }

        private string CreateTradeFile(int index, TradeRecord record, string from, string to)
        {
            return CreateTradeFile(index, new[] { record }, from, to);
        }

        private string CreateTradeFile(int index, TradeRecord[] records, string from, string to)
        {
            var path = CreatePath(index, "5M", from, to);
            using var writer = new BinaryWriter(File.Create(path));
            foreach (var record in records)
            {
                writer.Write((double)new DateTimeOffset(record.Time).ToUnixTimeSeconds());
                writer.Write(record.Open);
                writer.Write(record.High);
                writer.Write(record.Low);
                writer.Write(record.Close);
                writer.Write(record.Volume);
                writer.Write(0); // native C trailing alignment padding
            }
            Assert.AreEqual(0, writer.BaseStream.Position % BinaryTradeBarSubscriptionReader.RecordSize);
            return path;
        }

        private string CreateQuoteFile(int index, params QuoteRecord[] records)
        {
            var path = CreatePath(index, "5M", "20240101", "20240102");
            using var writer = new BinaryWriter(File.Create(path));
            foreach (var record in records)
            {
                writer.Write((double)new DateTimeOffset(record.Time).ToUnixTimeSeconds());
                writer.Write(record.Open);
                writer.Write(record.High);
                writer.Write(record.Low);
                writer.Write(record.Close);
                writer.Write(record.Volume);
                writer.Write(record.BidOpen);
                writer.Write(record.BidHigh);
                writer.Write(record.BidLow);
                writer.Write(record.BidClose);
                writer.Write(record.AskOpen);
                writer.Write(record.AskHigh);
                writer.Write(record.AskLow);
                writer.Write(record.AskClose);
                writer.Write(record.Spread);
            }
            Assert.AreEqual(0, writer.BaseStream.Position % BinaryQuoteBarSubscriptionReader.RecordSize);
            return path;
        }

        private string CreateEmptyFile(int index, string period)
        {
            var path = CreatePath(index, period, "20240101", "20240102");
            using (File.Create(path))
            {
            }
            return path;
        }

        private string CreatePath(int index, string period, string from, string to)
        {
            var directory = Path.Combine(_dataPath, $"FX_{SymbolValue}_test_bin_data");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, $"{index}-FX_{SymbolValue}_{period}_{from}-{to}.bin");
        }

        private static TradeRecord Trade(DateTime time, float open, float high, float low, float close, float volume)
        {
            return new TradeRecord(time, open, high, low, close, volume);
        }

        private static QuoteRecord Quote(
            DateTime time,
            float open,
            float high,
            float low,
            float close,
            float volume,
            float bidOpen,
            float bidHigh,
            float bidLow,
            float bidClose,
            float askOpen,
            float askHigh,
            float askLow,
            float askClose,
            float spread)
        {
            return new QuoteRecord(time, open, high, low, close, volume,
                bidOpen, bidHigh, bidLow, bidClose, askOpen, askHigh, askLow, askClose, spread);
        }

        private static DateTime Minute(int minute)
        {
            return new DateTime(2024, 1, 1, 0, minute, 0, DateTimeKind.Utc);
        }

        private static DateTime AsUtc(DateTime time)
        {
            return DateTime.SpecifyKind(time, DateTimeKind.Utc);
        }

        private sealed record TradeRecord(DateTime Time, float Open, float High, float Low, float Close, float Volume);

        private sealed record QuoteRecord(
            DateTime Time,
            float Open,
            float High,
            float Low,
            float Close,
            float Volume,
            float BidOpen,
            float BidHigh,
            float BidLow,
            float BidClose,
            float AskOpen,
            float AskHigh,
            float AskLow,
            float AskClose,
            float Spread);
    }
}
