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
using QuantConnect.Lean.Engine.DataFeeds;

namespace QuantConnect.Tests.Engine.DataFeeds
{
    [TestFixture]
    public class BinaryFileResolverTests
    {
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
        public void ParsesFilenameMetadataAndRejectsInvalidNames()
        {
            var path = Path.Combine(_dataPath, "3-FX_EURUSD_5M_20240101-20240103.bin");

            Assert.IsTrue(BinaryFileResolver.TryParseMeta(path, "eurusd", out var metadata));
            Assert.AreEqual("EURUSD", metadata.Symbol);
            Assert.AreEqual(TimeSpan.FromMinutes(5), metadata.Period);
            Assert.AreEqual(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), metadata.FromUtc);
            Assert.AreEqual(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), metadata.ToUtc);

            Assert.IsFalse(BinaryFileResolver.TryParseMeta(path, "GBPUSD", out _));
            Assert.IsFalse(BinaryFileResolver.TryParseMeta("bad.bin", "EURUSD", out _));
            Assert.IsFalse(BinaryFileResolver.TryParseMeta("bad-FX_EURUSD_5M_20240101-20240103.bin", "EURUSD", out _));
            Assert.IsFalse(BinaryFileResolver.TryParseMeta("1-FX_EURUSD_0M_20240101-20240103.bin", "EURUSD", out _));
            Assert.IsFalse(BinaryFileResolver.TryParseMeta("1-FX_EURUSD_5M_20240103-20240101.bin", "EURUSD", out _));
        }

        [Test]
        public void SelectsNativePeriodDeterministically()
        {
            var periods = new[] { TimeSpan.FromHours(1), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5) };

            Assert.AreEqual(TimeSpan.FromMinutes(15),
                BinaryFileResolver.SelectNativePeriod(periods, TimeSpan.FromMinutes(15)));
            Assert.AreEqual(TimeSpan.FromMinutes(5),
                BinaryFileResolver.SelectNativePeriod(periods, TimeSpan.FromMinutes(30)));
            Assert.AreEqual(TimeSpan.FromMinutes(5),
                BinaryFileResolver.SelectNativePeriod(periods, TimeSpan.FromMinutes(1)));
            Assert.IsNull(BinaryFileResolver.SelectNativePeriod(Array.Empty<TimeSpan>(), TimeSpan.FromMinutes(5)));
            Assert.Throws<InvalidOperationException>(() =>
                BinaryFileResolver.SelectNativePeriod(new[] { TimeSpan.FromMinutes(5) }, TimeSpan.FromMinutes(7)));
        }

        [Test]
        public void ResolvesOneTimeframeUsingHalfOpenIntersection()
        {
            CreateEmptyFile(1, "EURUSD", "5M", "20240101", "20240103");
            var expected = CreateEmptyFile(2, "EURUSD", "15M", "20240101", "20240103");
            CreateInvalidFile("not-metadata.bin");

            var resolved = BinaryFileResolver.ResolveFilesFor(
                "EURUSD",
                TimeSpan.FromMinutes(15),
                Utc(2024, 1, 1),
                Utc(2024, 1, 3),
                _dataPath);

            Assert.AreEqual(1, resolved.Count);
            Assert.AreEqual(expected, resolved[0].FullPath);
            Assert.AreEqual(TimeSpan.FromMinutes(15), resolved[0].Period);

            var touchesExclusiveEnd = BinaryFileResolver.ResolveFilesFor(
                "EURUSD",
                TimeSpan.FromMinutes(15),
                Utc(2024, 1, 3),
                Utc(2024, 1, 4),
                _dataPath);
            Assert.IsEmpty(touchesExclusiveEnd);

            var noSymbolMatches = BinaryFileResolver.ResolveFilesFor(
                "GBPUSD",
                TimeSpan.FromMinutes(15),
                Utc(2024, 1, 1),
                Utc(2024, 1, 3),
                _dataPath);
            Assert.IsEmpty(noSymbolMatches);
        }

        [Test]
        public void OrdersOverlappingFilesDeterministically()
        {
            var laterPath = CreateEmptyFile(2, "EURUSD", "5M", "20240101", "20240104");
            var firstPath = CreateEmptyFile(1, "EURUSD", "5M", "20240101", "20240103");
            var latestStart = CreateEmptyFile(3, "EURUSD", "5M", "20240102", "20240105");

            var resolved = BinaryFileResolver.ResolveFilesFor(
                "EURUSD",
                TimeSpan.FromMinutes(5),
                Utc(2024, 1, 1),
                Utc(2024, 1, 5),
                _dataPath);

            CollectionAssert.AreEqual(new[] { firstPath, laterPath, latestStart }, resolved.Select(metadata => metadata.FullPath));
        }

        private string CreateEmptyFile(int index, string symbol, string period, string from, string to)
        {
            var directory = Path.Combine(_dataPath, $"FX_{symbol}_test_bin_data");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{index}-FX_{symbol}_{period}_{from}-{to}.bin");
            using (File.Create(path))
            {
            }
            return Path.GetFullPath(path);
        }

        private void CreateInvalidFile(string name)
        {
            var directory = Path.Combine(_dataPath, "FX_EURUSD_test_bin_data");
            Directory.CreateDirectory(directory);
            using (File.Create(Path.Combine(directory, name)))
            {
            }
        }

        private static DateTime Utc(int year, int month, int day)
        {
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }
    }
}
