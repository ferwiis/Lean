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
    /// Logical QuoteBar reader over resolved QuoteBarRow physical files.
    /// </summary>
    public sealed class BinaryQuoteBarSubscriptionReader : IEnumerator<BaseData>
    {
        /// <summary>
        /// Native QuoteBarRow size, including the explicit Reserved field at bytes 60 through 63.
        /// </summary>
        public const int RecordSize = BinaryPhysicalSchema.QuoteBarRecordSize;

        private readonly BinaryDataSubscriptionReader _reader;

        /// <summary>
        /// Creates a forward-only logical QuoteBar stream over resolved physical files.
        /// </summary>
        public BinaryQuoteBarSubscriptionReader(
            Symbol symbol,
            IReadOnlyList<BinaryFileResolver.BinMeta> files,
            DateTime startUtc,
            DateTime endUtc)
        {
            _reader = new BinaryDataSubscriptionReader(symbol, typeof(QuoteBar), files, startUtc, endUtc);
        }

        /// <summary>Gets the current logical QuoteBar.</summary>
        public BaseData Current => _reader.Current;

        object IEnumerator.Current => Current;

        /// <summary>Advances to the next logical QuoteBar.</summary>
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
