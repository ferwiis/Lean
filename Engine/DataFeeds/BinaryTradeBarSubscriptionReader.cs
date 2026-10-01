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

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Logical TradeBar reader over resolved TradeBarRow and QuoteBarRow physical files.
    /// </summary>
    public sealed class BinaryTradeBarSubscriptionReader : IEnumerator<BaseData>
    {
        /// <summary>
        /// Native TradeBarRow size, including four trailing alignment bytes.
        /// </summary>
        public const int RecordSize = BinaryPhysicalSchema.TradeBarRecordSize;

        private readonly BinaryDataSubscriptionReader _reader;

        /// <summary>
        /// Creates a forward-only logical TradeBar stream over resolved physical files.
        /// </summary>
        public BinaryTradeBarSubscriptionReader(
            Symbol symbol,
            IReadOnlyList<BinaryFileResolver.BinMeta> files,
            DateTime startUtc,
            DateTime endUtc)
        {
            _reader = new BinaryDataSubscriptionReader(symbol, typeof(TradeBar), files, startUtc, endUtc);
        }

        /// <summary>Gets the current logical TradeBar.</summary>
        public BaseData Current => _reader.Current;

        object IEnumerator.Current => Current;

        /// <summary>Advances to the next logical TradeBar.</summary>
        public bool MoveNext()
        {
            return _reader.MoveNext();
        }

        /// <summary>This reader is forward-only.</summary>
        public void Reset()
        {
            _reader.Reset();
        }

        /// <summary>Releases active file mappings deterministically.</summary>
        public void Dispose()
        {
            _reader.Dispose();
        }
    }
}
