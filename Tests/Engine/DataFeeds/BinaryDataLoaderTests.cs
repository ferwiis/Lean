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
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds;

namespace QuantConnect.Tests.Engine.DataFeeds
{
    public abstract class BinaryBatchTestBase
    {
        protected static readonly DateTime First = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        protected static readonly Symbol TestSymbol = Symbol.Create("EURUSD", SecurityType.Forex, Market.FXCM);
        private static string _root;
        protected static string DataPath => Path.Combine(_root, "Data", "historical_data");

        [SetUp]
        public void CreateBatchDataDirectory()
        {
            if (_root == null)
            {
                _root = Path.Combine(Path.GetTempPath(), "lean-binary-batch-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(DataPath);
                var previousDirectory = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = _root;
                    RuntimeHelpers.RunClassConstructor(typeof(BinaryDataLoader).TypeHandle);
                }
                finally
                {
                    Environment.CurrentDirectory = previousDirectory;
                }
            }
            Directory.CreateDirectory(DataPath);
            foreach (var file in Directory.EnumerateFiles(DataPath, "*.bin", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
        }

        [OneTimeTearDown]
        public void DeleteBatchDataDirectory()
        {
            if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        protected static string WriteFile(bool quote, int index, string period, params (DateTime Time, float Open)[] records)
        {
            var directory = Path.Combine(DataPath, "FX_EURUSD_test_bin_data");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"{index}-FX_EURUSD_{period}_20240701-20240702.bin");
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
            }
            Assert.AreEqual(records.Length * (quote ? 64 : 28), writer.BaseStream.Length);
            return file;
        }

        protected static List<BaseData> Load(bool quote, TimeSpan period, DateTime start, DateTime end)
        {
            return quote
                ? BinaryDataLoader.LoadAsQuoteBars(TestSymbol.Value, period, start, end).Cast<BaseData>().ToList()
                : BinaryDataLoader.LoadAsTradeBars(TestSymbol.Value, period, start, end).Cast<BaseData>().ToList();
        }
    }

    [TestFixture, NonParallelizable]
    public class BinaryDataLoaderTests : BinaryBatchTestBase
    {
        [TestCase(false)]
        [TestCase(true)]
        public void LoadsRequestedWindowAndDeduplicatesAcrossFiles(bool quote)
        {
            WriteFile(quote, 1, "1M", (First.AddMinutes(-1), 9), (First, 10), (First.AddMinutes(1), 11));
            WriteFile(quote, 2, "1M", (First.AddMinutes(1), 11), (First.AddMinutes(2), 12), (First.AddMinutes(3), 13));
            File.WriteAllBytes(Path.Combine(DataPath, "FX_EURUSD_test_bin_data", "malformed.bin"), new byte[] { 1 });
            var bars = Load(quote, TimeSpan.FromMinutes(1), First, First.AddMinutes(3));
            CollectionAssert.AreEqual(new[] { First, First.AddMinutes(1), First.AddMinutes(2) }, bars.Select(bar => bar.Time));
            CollectionAssert.AreEqual(new[] { 11m, 12m, 13m }, bars.Select(bar => bar.Value));
            Assert.IsTrue(bars.All(bar => bar.Symbol.Value == TestSymbol.Value));
            if (quote)
            {
                Assert.AreEqual(10m, ((QuoteBar)bars[0]).Bid.Close);
                Assert.AreEqual(12m, ((QuoteBar)bars[0]).Ask.Close);
            }
            else
            {
                Assert.AreEqual(10m, ((TradeBar)bars[0]).Open);
                Assert.AreEqual(1m, ((TradeBar)bars[0]).Volume);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConsolidatesCoarserRequestsWithoutInventingFinerBars(bool quote)
        {
            WriteFile(quote, 1, "5M", (First, 10), (First.AddMinutes(5), 11), (First.AddMinutes(10), 12));
            var coarse = Load(quote, TimeSpan.FromMinutes(15), First, First.AddMinutes(15));
            Assert.AreEqual(1, coarse.Count);
            Assert.AreEqual(First, coarse[0].Time);
            Assert.AreEqual(First.AddMinutes(15), coarse[0].EndTime);
            // Batch consolidation uses the last ask close for QuoteBar.Value.
            Assert.AreEqual(quote ? 14m : 13m, coarse[0].Value);
            var native = Load(quote, TimeSpan.FromMinutes(1), First, First.AddMinutes(15));
            Assert.AreEqual(3, native.Count);
            Assert.IsTrue(native.All(bar => bar.EndTime - bar.Time == TimeSpan.FromMinutes(5)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReturnsNoBarsOutsideAvailableFileRanges(bool quote)
        {
            WriteFile(quote, 1, "1M", (First, 10));
            Assert.IsEmpty(Load(quote, TimeSpan.FromMinutes(1), First.AddDays(2), First.AddDays(3)));
        }
    }
}
