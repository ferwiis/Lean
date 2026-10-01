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
 
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds;

namespace QuantConnect.Tests.Engine.DataFeeds
{
    [TestFixture]
    public class BinaryFileResolverTests
    {
        private const string SymbolValue = "EURUSD";
        private string _dataPath;

        [SetUp]
        public void SetUp()
        {
            _dataPath = Path.Combine(Path.GetTempPath(), "lean-binary-resolver-" + Guid.NewGuid().ToString("N"));
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
        public void ParsesLegacyAndExplicitPhysicalSchemaFilenames()
        {
            var legacy = CreatePath(1, "5M", "20240101", "20240103");
            var trade = CreatePath(2, "5M", "20240101", "20240103", "TB");
            var quote = CreatePath(3, "5M", "20240101", "20240103", "QB");

            Assert.IsTrue(BinaryFileResolver.TryParseMeta(legacy, "eurusd", out var legacyMetadata));
            Assert.IsNull(legacyMetadata.DeclaredPhysicalRecordType);
            Assert.AreEqual(TimeSpan.FromMinutes(5), legacyMetadata.Period);
            Assert.AreEqual(Utc(2024, 1, 1), legacyMetadata.FromUtc);
            Assert.AreEqual(Utc(2024, 1, 3), legacyMetadata.ToUtc);

            Assert.IsTrue(BinaryFileResolver.TryParseMeta(trade, SymbolValue, out var tradeMetadata));
            Assert.AreEqual(PhysicalRecordType.TradeBarRow, tradeMetadata.DeclaredPhysicalRecordType);

            Assert.IsTrue(BinaryFileResolver.TryParseMeta(quote, SymbolValue, out var quoteMetadata));
            Assert.AreEqual(PhysicalRecordType.QuoteBarRow, quoteMetadata.DeclaredPhysicalRecordType);

            Assert.IsFalse(BinaryFileResolver.TryParseMeta(
                CreatePath(4, "5M", "20240101", "20240103", "XX"), SymbolValue, out _));
            Assert.IsFalse(BinaryFileResolver.TryParseMeta("bad.bin", SymbolValue, out _));
            Assert.IsFalse(BinaryFileResolver.TryParseMeta(legacy, "GBPUSD", out _));
        }

        [Test]
        public void PhysicalContractsHaveExactProducerAbi()
        {
            Assert.AreEqual(32, Marshal.SizeOf<TradeBarRow>());
            Assert.AreEqual(0, Marshal.OffsetOf<TradeBarRow>(nameof(TradeBarRow.Timestamp)).ToInt32());
            Assert.AreEqual(24, Marshal.OffsetOf<TradeBarRow>(nameof(TradeBarRow.Volume)).ToInt32());
            Assert.IsNull(typeof(TradeBarRow).GetField("Reserved"),
                "The TradeBar producer declares 28 bytes of fields followed by native ABI padding.");

            Assert.AreEqual(64, Marshal.SizeOf<QuoteBarRow>());
            Assert.AreEqual(60, Marshal.OffsetOf<QuoteBarRow>(nameof(QuoteBarRow.Reserved)).ToInt32());
            Assert.IsNull(typeof(QuoteBarRow).GetField("Spread"));
        }

        [Test]
        public void SelectsExactThenLargestSmallerExactDivisor()
        {
            var periods = new[] { TimeSpan.FromHours(1), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5) };

            Assert.AreEqual(TimeSpan.FromHours(1),
                BinaryFileResolver.SelectNativePeriod(periods, TimeSpan.FromHours(1)));
            Assert.AreEqual(TimeSpan.FromMinutes(15),
                BinaryFileResolver.SelectNativePeriod(periods, TimeSpan.FromMinutes(30)));
            Assert.IsNull(BinaryFileResolver.SelectNativePeriod(Array.Empty<TimeSpan>(), TimeSpan.FromMinutes(5)));
            Assert.Throws<InvalidOperationException>(() =>
                BinaryFileResolver.SelectNativePeriod(new[] { TimeSpan.FromMinutes(5) }, TimeSpan.FromMinutes(1)));
            Assert.Throws<InvalidOperationException>(() =>
                BinaryFileResolver.SelectNativePeriod(new[] { TimeSpan.FromMinutes(5) }, TimeSpan.FromMinutes(7)));
        }

        [Test]
        public void ExplicitDeclarationsResolveAmbiguousLengthsAndRejectContradictions()
        {
            var quotePath = CreatePath(1, "5M", "20240101", "20240102", "QB");
            WriteQuoteRows(quotePath, Minute(0));
            Assert.AreEqual(
                PhysicalRecordType.QuoteBarRow,
                BinaryPhysicalSchema.Resolve(quotePath, new FileInfo(quotePath).Length,
                    PhysicalRecordType.QuoteBarRow, Utc(2024, 1, 1), Utc(2024, 1, 2)));

            var contradiction = CreatePath(2, "5M", "20240101", "20240102", "QB");
            WriteTradeRows(contradiction, Minute(0));
            var exception = Assert.Throws<InvalidDataException>(() =>
                BinaryPhysicalSchema.Resolve(contradiction, new FileInfo(contradiction).Length,
                    PhysicalRecordType.QuoteBarRow, Utc(2024, 1, 1), Utc(2024, 1, 2)));
            StringAssert.Contains("declares QuoteBarRow", exception.Message);
        }

        [Test]
        public void BoundedProbeResolvesLegacyTradeAndQuoteRows()
        {
            var tradePath = CreatePath(1, "5M", "20240101", "20240102");
            WriteTradeRows(tradePath, Minute(0), Minute(5), Minute(10), Minute(15));
            Assert.AreEqual(
                PhysicalRecordType.TradeBarRow,
                BinaryPhysicalSchema.Resolve(tradePath, new FileInfo(tradePath).Length, null,
                    Utc(2024, 1, 1), Utc(2024, 1, 2)));

            var quotePath = CreatePath(2, "5M", "20240101", "20240102");
            WriteQuoteRows(quotePath, Minute(0), Minute(5));
            Assert.AreEqual(
                PhysicalRecordType.QuoteBarRow,
                BinaryPhysicalSchema.Resolve(quotePath, new FileInfo(quotePath).Length, null,
                    Utc(2024, 1, 1), Utc(2024, 1, 2)));
        }

        [Test]
        public void BoundedProbeRejectsInvalidAndStillAmbiguousPayloads()
        {
            var invalid = CreatePath(1, "5M", "20240101", "20240102");
            File.WriteAllBytes(invalid, new byte[BinaryPhysicalSchema.AmbiguityProbeSize]);
            var invalidException = Assert.Throws<InvalidDataException>(() =>
                BinaryPhysicalSchema.Resolve(invalid, new FileInfo(invalid).Length, null,
                    Utc(2024, 1, 1), Utc(2024, 1, 2)));
            StringAssert.Contains("neither schema is valid", invalidException.Message);

            var ambiguous = CreatePath(2, "5M", "20231231", "20240102");
            var timestamp = DoubleFromFloats(30f, 27.17407f);
            using (var writer = new BinaryWriter(File.Create(ambiguous)))
            {
                for (var index = 0; index < 4; index++)
                {
                    WriteTradeRow(writer, timestamp, 30f, 30f, 30f, 30f, 30f, 30f);
                }
            }
            var ambiguousException = Assert.Throws<InvalidDataException>(() =>
                BinaryPhysicalSchema.Resolve(ambiguous, new FileInfo(ambiguous).Length, null,
                    Utc(2023, 12, 31), Utc(2024, 1, 2)));
            StringAssert.Contains("both schemas remain valid", ambiguousException.Message);
        }

        [Test]
        public void ExplicitPhysicalSchemaDoesNotChangeRequestedLogicalType()
        {
            var path = CreatePath(1, "5M", "20240101", "20240102", "QB");
            WriteQuoteRows(path, Minute(0));

            var plan = BinaryFileResolver.ResolveRequest(
                SymbolValue,
                typeof(TradeBar),
                TimeSpan.FromMinutes(5),
                Minute(0),
                Minute(10),
                _dataPath);

            Assert.AreEqual(typeof(TradeBar), plan.RequestedLeanType);
            Assert.AreEqual(PhysicalRecordType.QuoteBarRow, plan.Files.Single().PhysicalRecordType);
        }

        [Test]
        public void InvalidPhysicalSuffixIsReportedDuringDiscovery()
        {
            var path = CreatePath(1, "5M", "20240101", "20240102", "BAD");
            using (File.Create(path))
            {
            }

            var exception = Assert.Throws<InvalidDataException>(() =>
                BinaryFileResolver.GetAllMetasForSymbol(SymbolValue, _dataPath));
            StringAssert.Contains("_BAD", exception.Message);
        }

        [Test]
        public void StructuralPreflightAggregatesErrorsDeterministically()
        {
            var first = CreatePath(1, "5M", "20240101", "20240102", "QB");
            WriteTradeRows(first, Minute(0));
            var second = CreatePath(2, "5M", "20240101", "20240102", "TB");
            File.WriteAllBytes(second, new byte[] { 1, 2, 3 });

            var exception = Assert.Throws<InvalidDataException>(() => BinaryFileResolver.ResolveRequest(
                SymbolValue,
                typeof(TradeBar),
                TimeSpan.FromMinutes(5),
                Minute(0),
                Minute(5),
                _dataPath));

            StringAssert.Contains(first, exception.Message);
            StringAssert.Contains(second, exception.Message);
            Assert.Less(exception.Message.IndexOf(first, StringComparison.OrdinalIgnoreCase),
                exception.Message.IndexOf(second, StringComparison.OrdinalIgnoreCase));
        }

        private string CreatePath(int index, string period, string from, string to, string suffix = null)
        {
            var directory = Path.Combine(_dataPath, $"FX_{SymbolValue}_test_bin_data");
            Directory.CreateDirectory(directory);
            var tag = suffix == null ? string.Empty : "_" + suffix;
            return Path.Combine(directory, $"{index}-FX_{SymbolValue}_{period}_{from}-{to}{tag}.bin");
        }

        private static void WriteTradeRows(string path, params DateTime[] times)
        {
            using var writer = new BinaryWriter(File.Create(path));
            foreach (var time in times)
            {
                WriteTradeRow(writer, new DateTimeOffset(time).ToUnixTimeSeconds(), 10, 12, 9, 11, 1, 0);
            }
        }

        private static void WriteTradeRow(
            BinaryWriter writer,
            double timestamp,
            float open,
            float high,
            float low,
            float close,
            float volume,
            float padding)
        {
            writer.Write(timestamp);
            writer.Write(open);
            writer.Write(high);
            writer.Write(low);
            writer.Write(close);
            writer.Write(volume);
            writer.Write(padding);
        }

        private static void WriteQuoteRows(string path, params DateTime[] times)
        {
            using var writer = new BinaryWriter(File.Create(path));
            foreach (var time in times)
            {
                var timestamp = (double)new DateTimeOffset(time).ToUnixTimeSeconds();
                writer.Write(timestamp);
                writer.Write(10f);
                writer.Write(12f);
                writer.Write(9f);
                writer.Write(11f);
                writer.Write(1f);
                writer.Write(10f);
                writer.Write(12f);
                writer.Write(9f);
                writer.Write(11f);
                writer.Write(11f);
                writer.Write(13f);
                writer.Write(10f);
                writer.Write(12f);
                writer.Write(123u);
            }
        }

        private static double DoubleFromFloats(float lowBytes, float highBytes)
        {
            var bytes = new byte[sizeof(double)];
            Buffer.BlockCopy(BitConverter.GetBytes(lowBytes), 0, bytes, 0, sizeof(float));
            Buffer.BlockCopy(BitConverter.GetBytes(highBytes), 0, bytes, sizeof(float), sizeof(float));
            return BitConverter.ToDouble(bytes, 0);
        }

        private static DateTime Minute(int minute)
        {
            return new DateTime(2024, 1, 1, 0, minute, 0, DateTimeKind.Utc);
        }

        private static DateTime Utc(int year, int month, int day)
        {
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }
    }
}
