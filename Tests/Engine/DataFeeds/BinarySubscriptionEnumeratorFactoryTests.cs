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
        public void TradeBarRowProjectsDirectlyToTradeBar()
        {
            CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 7));

            var bars = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5));

            Assert.AreEqual(1, bars.Count);
            Assert.AreEqual(10m, bars[0].Open);
            Assert.AreEqual(12m, bars[0].High);
            Assert.AreEqual(9m, bars[0].Low);
            Assert.AreEqual(11m, bars[0].Close);
            Assert.AreEqual(7m, bars[0].Volume);
            Assert.AreSame(_symbol, bars[0].Symbol);
        }

        [Test]
        public void QuoteBarRowProjectsToTradeBarAndNativeQuoteBarSemantics()
        {
            CreateQuoteFile(1, "5M", "20240101", "20240102",
                Quote(Minute(0), 700, 800, 600, 777, 9,
                    100, 104, 99, 103, 102, 106, 101, 105, 0xDEADBEEF));

            var trade = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5)).Single();
            Assert.AreEqual(700m, trade.Open);
            Assert.AreEqual(777m, trade.Close);
            Assert.AreEqual(9m, trade.Volume);

            var quote = Read<QuoteBar>(typeof(QuoteBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5)).Single();
            Assert.AreEqual(104m, quote.Value);
            Assert.AreEqual(quote.Close, quote.Value);
            Assert.AreNotEqual(777m, quote.Value, "Global QuoteBarRow.Close must not overwrite QuoteBar.Value.");
            Assert.AreEqual(103m, quote.Bid.Close);
            Assert.AreEqual(105m, quote.Ask.Close);
            Assert.AreSame(_symbol, quote.Symbol);
        }

        [Test]
        public void TradeBarRowCannotSatisfyQuoteBarRequest()
        {
            CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 7));

            var exception = Assert.Throws<NotSupportedException>(() =>
                _factory.CreateEnumerator(
                    _symbol, typeof(QuoteBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5), TimeZones.Utc));
            StringAssert.Contains("TradeBarRow cannot project to QuoteBar", exception.Message);
        }

        [Test]
        public void MixedNonEmptyPhysicalSetCannotSatisfyQuoteButEmptyDeclarationsAreIgnored()
        {
            CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 7));
            CreateQuoteFile(2, "5M", "20240101", "20240102",
                Quote(Minute(0), 10, 12, 9, 11, 1, 99, 103, 98, 102, 101, 105, 100, 104, 0));

            var mixed = Assert.Throws<NotSupportedException>(() =>
                _factory.CreateEnumerator(
                    _symbol, typeof(QuoteBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5), TimeZones.Utc));
            StringAssert.Contains("TradeBarRow cannot project to QuoteBar", mixed.Message);

            DeleteBinaryFiles();
            using (File.Create(CreatePath(1, "5M", "20240101", "20240102", "TB")))
            {
            }
            CreateQuoteFile(2, "5M", "20240101", "20240102",
                Quote(Minute(0), 10, 12, 9, 11, 1, 99, 103, 98, 102, 101, 105, 100, 104, 0));

            Assert.AreEqual(1,
                Read<QuoteBar>(typeof(QuoteBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5)).Count);
        }

        [Test]
        public void MixedPhysicalSchemasProduceOneChronologicalTradeBarStream()
        {
            CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 1),
                Trade(Minute(10), 12, 14, 11, 13, 3));
            CreateQuoteFile(2, "5M", "20240101", "20240102",
                Quote(Minute(5), 11, 13, 10, 12, 2, 100, 101, 99, 100, 101, 102, 100, 101, 1),
                Quote(Minute(10), 12, 14, 11, 13, 3, 200, 201, 199, 200, 201, 202, 200, 201, 2));

            var bars = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(15));

            CollectionAssert.AreEqual(new[] { Minute(0), Minute(5), Minute(10) }, bars.Select(bar => AsUtc(bar.Time)));
            CollectionAssert.AreEqual(new[] { 11m, 12m, 13m }, bars.Select(bar => bar.Close));
        }

        [Test]
        public void ExactPeriodWinsAndLargestSmallerDivisorIsSelected()
        {
            CreateTradeFile(1, "5M", "20240101", "20240102",
                Enumerable.Range(0, 6).Select(index =>
                    Trade(Minute(index * 5), 10 + index, 20 + index, 5, 11 + index, 1)).ToArray());
            CreateTradeFile(2, "15M", "20240101", "20240102",
                Trade(Minute(0), 200, 220, 190, 210, 3),
                Trade(Minute(15), 210, 230, 205, 225, 3));
            CreateTradeFile(3, "1H", "20240101", "20240102",
                Trade(Minute(0), 500, 550, 490, 540, 12));

            var thirtyMinute = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(30), Minute(0), Minute(30)).Single();
            Assert.AreEqual(200m, thirtyMinute.Open, "The 15-minute source should be selected over the 5-minute source.");
            Assert.AreEqual(225m, thirtyMinute.Close);

            var hour = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromHours(1), Minute(0), Minute(60)).Single();
            Assert.AreEqual(500m, hour.Open, "The exact one-hour source should win.");
            Assert.AreEqual(540m, hour.Close);
        }

        [Test]
        public void FinerAndNonDivisibleRequestsFailExplicitly()
        {
            CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 1));

            var finer = Assert.Throws<InvalidOperationException>(() =>
                _factory.CreateEnumerator(
                    _symbol, typeof(TradeBar), TimeSpan.FromMinutes(1), Minute(0), Minute(10), TimeZones.Utc));
            StringAssert.Contains("finer", finer.Message);
            StringAssert.Contains(SymbolValue, finer.Message);

            var nonDivisible = Assert.Throws<InvalidOperationException>(() =>
                _factory.CreateEnumerator(
                    _symbol, typeof(TradeBar), TimeSpan.FromMinutes(7), Minute(0), Minute(10), TimeZones.Utc));
            StringAssert.Contains("exactly divides", nonDivisible.Message);
        }

        [Test]
        public void CompatibleNativeTimeframesAggregateEquivalently()
        {
            CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 1),
                Trade(Minute(5), 11, 14, 10, 13, 2),
                Trade(Minute(10), 13, 15, 8, 9, 3),
                Trade(Minute(15), 9, 16, 7, 14, 4),
                Trade(Minute(20), 14, 17, 13, 16, 5),
                Trade(Minute(25), 16, 18, 12, 15, 6));
            var fromFive = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(30), Minute(0), Minute(30)).Single();

            DeleteBinaryFiles();
            CreateTradeFile(1, "15M", "20240101", "20240102",
                Trade(Minute(0), 10, 15, 8, 9, 6),
                Trade(Minute(15), 9, 18, 7, 15, 15));
            var fromFifteen = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(30), Minute(0), Minute(30)).Single();

            DeleteBinaryFiles();
            CreateTradeFile(1, "30M", "20240101", "20240102",
                Trade(Minute(0), 10, 18, 7, 15, 21));
            var native = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(30), Minute(0), Minute(30)).Single();

            AssertTradeBarEqual(native, fromFive);
            AssertTradeBarEqual(native, fromFifteen);
        }

        [Test]
        public void ComplementaryThreeFileOverlapIsMergedWithoutLoss()
        {
            CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 10, 10, 10, 1),
                Trade(Minute(15), 13, 13, 13, 13, 1));
            CreateTradeFile(2, "5M", "20240101", "20240102",
                Trade(Minute(5), 11, 11, 11, 11, 1),
                Trade(Minute(15), 13, 13, 13, 13, 1));
            CreateTradeFile(3, "5M", "20240101", "20240102",
                Trade(Minute(10), 12, 12, 12, 12, 1));

            var bars = Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(20));

            CollectionAssert.AreEqual(
                new[] { Minute(0), Minute(5), Minute(10), Minute(15) },
                bars.Select(bar => AsUtc(bar.Time)));
        }

        [Test]
        public void QuoteDuplicatesIgnoreUnusedGlobalFieldsAndReserved()
        {
            CreateQuoteFile(1, "5M", "20240101", "20240102",
                Quote(Minute(0), 10, 12, 9, 11, 1, 99, 103, 98, 102, 101, 105, 100, 104, 1));
            CreateQuoteFile(2, "5M", "20240101", "20240102",
                Quote(Minute(0), 1000, 1200, 900, 1100, 999, 99, 103, 98, 102, 101, 105, 100, 104, 999));

            var bars = Read<QuoteBar>(typeof(QuoteBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5));

            Assert.AreEqual(1, bars.Count);
            Assert.AreEqual(103m, bars[0].Value);
        }

        [Test]
        public void ConflictingDuplicatesFailWithoutFilenamePricePrecedence()
        {
            var first = CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 1));
            var second = CreateTradeFile(2, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 99, 1));

            using var enumerator = _factory.CreateEnumerator(
                _symbol, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5), TimeZones.Utc);
            var exception = Assert.Throws<InvalidDataException>(() => enumerator.MoveNext());
            StringAssert.Contains("Conflicting logical binary duplicates", exception.Message);
            StringAssert.Contains(first, exception.Message);
            StringAssert.Contains(second, exception.Message);

            AssertFilesCanMove(first, second);
        }

        [Test]
        public void EqualAndConflictingSameFileDuplicatesUseLogicalPolicy()
        {
            CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 1),
                Trade(Minute(0), 10, 12, 9, 11, 1),
                Trade(Minute(5), 11, 13, 10, 12, 1));
            Assert.AreEqual(2,
                Read<TradeBar>(typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(10)).Count);

            DeleteBinaryFiles();
            CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 1),
                Trade(Minute(0), 10, 12, 9, 12, 1));
            using var enumerator = _factory.CreateEnumerator(
                _symbol, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(10), TimeZones.Utc);
            Assert.IsTrue(enumerator.MoveNext());
            Assert.Throws<InvalidDataException>(() => enumerator.MoveNext());
        }

        [Test]
        public void EmptyFilesAndSequentialGroupsDoNotTerminateTheStream()
        {
            CreateEmptyFile(1, "5M", "20240101", "20240102");
            CreateTradeFile(2, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 1));
            CreateEmptyFile(3, "5M", "20240102", "20240103");
            CreateTradeFile(4, "5M", "20240102", "20240103",
                Trade(Utc(2024, 1, 2), 11, 13, 10, 12, 1));

            var bars = Read<TradeBar>(
                typeof(TradeBar),
                TimeSpan.FromMinutes(5),
                Utc(2024, 1, 1),
                Utc(2024, 1, 2).AddMinutes(5));

            CollectionAssert.AreEqual(
                new[] { Utc(2024, 1, 1), Utc(2024, 1, 2) },
                bars.Select(bar => AsUtc(bar.Time)));
        }

        [Test]
        public void DecreasingAndNonFiniteRecordsReportExactBinaryContext()
        {
            var decreasing = CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(5), 10, 12, 9, 11, 1),
                Trade(Minute(0), 11, 13, 10, 12, 2));
            using (var enumerator = _factory.CreateEnumerator(
                       _symbol, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(10), TimeZones.Utc))
            {
                Assert.IsTrue(enumerator.MoveNext());
                var exception = Assert.Throws<InvalidDataException>(() => enumerator.MoveNext());
                StringAssert.Contains(decreasing, exception.Message);
                StringAssert.Contains("recordIndex=1", exception.Message);
                StringAssert.Contains("byteOffset=32", exception.Message);
                StringAssert.Contains("TradeBarRow", exception.Message);
            }

            DeleteBinaryFiles();
            var nonFinite = CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), float.NaN, 12, 9, 11, 1));
            using var invalid = _factory.CreateEnumerator(
                _symbol, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5), TimeZones.Utc);
            var invalidException = Assert.Throws<InvalidDataException>(() => invalid.MoveNext());
            StringAssert.Contains(nonFinite, invalidException.Message);
            StringAssert.Contains("Open is not finite", invalidException.Message);
            StringAssert.Contains("byteOffset=0", invalidException.Message);
        }

        [Test]
        public void EarlyDisposeReleasesSequentialAndOverlapMappings()
        {
            var sequential = CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 1));
            var enumerator = _factory.CreateEnumerator(
                _symbol, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5), TimeZones.Utc);
            Assert.IsTrue(enumerator.MoveNext());
            enumerator.Dispose();
            AssertFilesCanMove(sequential);

            DeleteBinaryFiles();
            var overlapA = CreateTradeFile(1, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 1));
            var overlapB = CreateTradeFile(2, "5M", "20240101", "20240102",
                Trade(Minute(0), 10, 12, 9, 11, 1));
            var overlap = _factory.CreateEnumerator(
                _symbol, typeof(TradeBar), TimeSpan.FromMinutes(5), Minute(0), Minute(5), TimeZones.Utc);
            Assert.IsTrue(overlap.MoveNext());
            overlap.Dispose();
            AssertFilesCanMove(overlapA, overlapB);
        }

        [Test]
        public void UtcChronologySurvivesBothNewYorkDstTransitions()
        {
            var forwardFirst = new DateTime(2024, 3, 10, 6, 55, 0, DateTimeKind.Utc);
            var forwardSecond = forwardFirst.AddMinutes(5);
            CreateTradeFile(1, "5M", "20240310", "20240311",
                Trade(forwardFirst, 10, 10, 10, 10, 1),
                Trade(forwardSecond, 11, 11, 11, 11, 1));

            var forward = Read<TradeBar>(
                typeof(TradeBar), TimeSpan.FromMinutes(5), forwardFirst, forwardSecond.AddMinutes(5), TimeZones.NewYork);
            CollectionAssert.AreEqual(
                new[] { new DateTime(2024, 3, 10, 1, 55, 0), new DateTime(2024, 3, 10, 3, 0, 0) },
                forward.Select(bar => bar.Time));

            DeleteBinaryFiles();
            var backwardFirst = new DateTime(2024, 11, 3, 5, 55, 0, DateTimeKind.Utc);
            var backwardSecond = backwardFirst.AddMinutes(5);
            CreateTradeFile(1, "5M", "20241103", "20241104",
                Trade(backwardFirst, 10, 10, 10, 10, 1),
                Trade(backwardSecond, 11, 11, 11, 11, 1));

            var backward = Read<TradeBar>(
                typeof(TradeBar), TimeSpan.FromMinutes(5), backwardFirst, backwardSecond.AddMinutes(5), TimeZones.NewYork);
            Assert.AreEqual(2, backward.Count);
            Assert.AreEqual(new DateTime(2024, 11, 3, 1, 55, 0), backward[0].Time);
            Assert.AreEqual(new DateTime(2024, 11, 3, 1, 0, 0), backward[1].Time);
            CollectionAssert.AreEqual(new[] { 10m, 11m }, backward.Select(bar => bar.Close));
            Assert.IsTrue(backward.All(bar => bar.Period == TimeSpan.FromMinutes(5)));
        }

        [Test]
        public void ReturnsEmptyForNoFilesAndRejectsUnsupportedRequests()
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

        private List<T> Read<T>(
            Type dataType,
            TimeSpan requestedPeriod,
            DateTime startUtc,
            DateTime endUtc,
            NodaTime.DateTimeZone timeZone = null)
            where T : BaseData
        {
            using var enumerator = _factory.CreateEnumerator(
                _symbol, dataType, requestedPeriod, startUtc, endUtc, timeZone ?? TimeZones.Utc);
            var data = new List<T>();
            while (enumerator.MoveNext())
            {
                data.Add((T)enumerator.Current);
            }
            return data;
        }

        private string CreateTradeFile(
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
                writer.Write(0); // native C alignment padding; no semantic field
            }
            Assert.AreEqual(0, writer.BaseStream.Position % BinaryTradeBarSubscriptionReader.RecordSize);
            return path;
        }

        private string CreateQuoteFile(
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
                writer.Write(record.Reserved);
            }
            Assert.AreEqual(0, writer.BaseStream.Position % BinaryQuoteBarSubscriptionReader.RecordSize);
            return path;
        }

        private string CreateEmptyFile(int index, string period, string from, string to)
        {
            var path = CreatePath(index, period, from, to, null);
            using (File.Create(path))
            {
            }
            return path;
        }

        private string CreatePath(int index, string period, string from, string to, string suffix)
        {
            var directory = Path.Combine(_dataPath, $"FX_{SymbolValue}_test_bin_data");
            Directory.CreateDirectory(directory);
            var tag = suffix == null ? string.Empty : "_" + suffix;
            return Path.Combine(directory, $"{index}-FX_{SymbolValue}_{period}_{from}-{to}{tag}.bin");
        }

        private void DeleteBinaryFiles()
        {
            foreach (var file in Directory.EnumerateFiles(_dataPath, "*.bin", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
        }

        private static void AssertFilesCanMove(params string[] paths)
        {
            foreach (var path in paths)
            {
                var moved = path + "." + Guid.NewGuid().ToString("N") + ".moved";
                File.Move(path, moved);
                Assert.IsTrue(File.Exists(moved));
            }
        }

        private static void AssertTradeBarEqual(TradeBar expected, TradeBar actual)
        {
            Assert.AreEqual(expected.Time, actual.Time);
            Assert.AreEqual(expected.EndTime, actual.EndTime);
            Assert.AreEqual(expected.Period, actual.Period);
            Assert.AreEqual(expected.Open, actual.Open);
            Assert.AreEqual(expected.High, actual.High);
            Assert.AreEqual(expected.Low, actual.Low);
            Assert.AreEqual(expected.Close, actual.Close);
            Assert.AreEqual(expected.Volume, actual.Volume);
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
            uint reserved)
        {
            return new QuoteRecord(time, open, high, low, close, volume,
                bidOpen, bidHigh, bidLow, bidClose, askOpen, askHigh, askLow, askClose, reserved);
        }

        private static DateTime Minute(int minute)
        {
            return Utc(2024, 1, 1).AddMinutes(minute);
        }

        private static DateTime Utc(int year, int month, int day)
        {
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
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
            uint Reserved);
    }
}
