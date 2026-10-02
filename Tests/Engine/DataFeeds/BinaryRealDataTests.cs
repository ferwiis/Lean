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
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;

namespace QuantConnect.Tests.Engine.DataFeeds
{
    [TestFixture]
    public class BinaryRealDataTests
    {
        [Test]
        [Category("Integration")]
        public void StreamsAvailableEurnzdQuoteDataAcrossPhysicalFiles()
        {
            var dataPath = Path.GetFullPath(Path.Combine(Globals.DataFolder, "historical_data"));
            var files = BinaryFileResolver.GetAllMetasForSymbol("EURNZD", dataPath)
                .Where(file => file.Period == TimeSpan.FromMinutes(5))
                .ToList();
            if (files.Count < 2)
            {
                Assert.Ignore($"At least two local EURNZD 5-minute binary files are required under '{dataPath}'.");
            }

            var startUtc = files.Min(file => file.FromUtc);
            var endUtc = files.Max(file => file.ToUtc);
            var symbol = Symbol.Create("EURNZD", SecurityType.Forex, Market.Oanda);
            var factory = new BinarySubscriptionEnumeratorFactory(dataPath);
            using var enumerator = factory.CreateEnumerator(
                symbol,
                typeof(QuoteBar),
                TimeSpan.FromMinutes(5),
                startUtc,
                endUtc,
                TimeZones.Utc);

            var stopwatch = Stopwatch.StartNew();
            var managedBefore = GC.GetTotalMemory(false);
            var allocatedBefore = GC.GetTotalAllocatedBytes(false);
            var gen0Before = GC.CollectionCount(0);
            var gen1Before = GC.CollectionCount(1);
            var gen2Before = GC.CollectionCount(2);
            var maxObservedManaged = managedBefore;
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var workingSetBefore = process.WorkingSet64;
            var maxObservedWorkingSet = workingSetBefore;
            var count = 0L;
            DateTime? first = null;
            DateTime? previous = null;
            while (enumerator.MoveNext())
            {
                var bar = (QuoteBar)enumerator.Current;
                first ??= bar.Time;
                if (previous.HasValue)
                {
                    Assert.Greater(bar.Time, previous.Value);
                }
                Assert.AreSame(symbol, bar.Symbol);
                Assert.IsNotNull(bar.Bid);
                Assert.IsNotNull(bar.Ask);
                previous = bar.Time;
                count++;

                if (count % 25000 == 0)
                {
                    maxObservedManaged = Math.Max(maxObservedManaged, GC.GetTotalMemory(false));
                    process.Refresh();
                    maxObservedWorkingSet = Math.Max(maxObservedWorkingSet, process.WorkingSet64);
                }
            }
            stopwatch.Stop();
            maxObservedManaged = Math.Max(maxObservedManaged, GC.GetTotalMemory(false));
            process.Refresh();
            maxObservedWorkingSet = Math.Max(maxObservedWorkingSet, process.WorkingSet64);

            Assert.Greater(count, 0);
            Assert.IsNotNull(first);
            Assert.IsNotNull(previous);
            TestContext.Progress.WriteLine(
                $"Real EURNZD binary stream: files={files.Count}, bars={count}, elapsed={stopwatch.Elapsed}, " +
                $"max-observed-managed-delta={maxObservedManaged - managedBefore:N0} bytes, " +
                $"max-observed-working-set-delta={maxObservedWorkingSet - workingSetBefore:N0} bytes, " +
                $"allocated={GC.GetTotalAllocatedBytes(false) - allocatedBefore:N0} bytes, " +
                $"collections={GC.CollectionCount(0) - gen0Before}/{GC.CollectionCount(1) - gen1Before}/{GC.CollectionCount(2) - gen2Before}, " +
                $"first={first:o}, last={previous:o}");
        }
    }
}
