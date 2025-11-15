/*
 * ALPHASEEK CUSTOM DATA READER
 * Reads Binance Parquet files for cryptocurrency backtesting
 *
 * Requires Apache.Arrow NuGet package
 * Add to Common/QuantConnect.Common.csproj:
 * <PackageReference Include="Apache.Arrow" Version="14.0.0" />
 *
 * Data path: /app/data/market/binance/crypto/binance/{resolution}/{symbol}_YYYYMMDD_YYYYMMDD.parquet
 * Columns: timestamp (index), open, high, low, close, volume (all float64)
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using QuantConnect.Data.Market;

namespace QuantConnect.Data.Custom
{
    /// <summary>
    /// Custom data reader for Binance Parquet files
    /// Reads OHLCV data from Apache Parquet format
    /// </summary>
    public class ParquetMarketData : TradeBar
    {
        // Base data path for Parquet files
        private const string DATA_PATH = "/app/data/market/binance/crypto/binance";

        // Cache for storing all bars from the file
        private static Dictionary<string, List<ParquetMarketData>> _fileCache = new Dictionary<string, List<ParquetMarketData>>();
        private static int _currentIndex = 0;
        private static string _currentCacheKey = null;

        /// <summary>
        /// Return the URL source for the subscription
        /// </summary>
        /// <param name="config">Subscription configuration</param>
        /// <param name="date">Date for the data</param>
        /// <param name="isLiveMode">Live trading mode flag</param>
        /// <returns>Subscription data source with file path</returns>
        public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
        {
            // Convert resolution to directory name
            string resolution = GetResolutionDirectory(config.Resolution);

            // Build directory path
            string directoryPath = Path.Combine(DATA_PATH, resolution);

            // Find Parquet file matching symbol pattern
            // Files are named: {SYMBOL}_YYYYMMDD_YYYYMMDD.parquet
            string pattern = $"{config.Symbol.Value}_*.parquet";

            if (Directory.Exists(directoryPath))
            {
                var files = Directory.GetFiles(directoryPath, pattern);
                if (files.Length > 0)
                {
                    // Use the first matching file (should only be one)
                    return new SubscriptionDataSource(files[0], SubscriptionTransportMedium.LocalFile);
                }
            }

            // Fallback to direct path construction
            string filePath = Path.Combine(directoryPath, $"{config.Symbol.Value}.parquet");
            return new SubscriptionDataSource(filePath, SubscriptionTransportMedium.LocalFile);
        }

        /// <summary>
        /// Reader for parsing Parquet file data
        /// </summary>
        /// <param name="config">Subscription configuration</param>
        /// <param name="stream">Stream reader for the file</param>
        /// <param name="date">Current date being processed</param>
        /// <param name="isLiveMode">Live trading flag</param>
        /// <returns>Parsed TradeBar data point</returns>
        public override BaseData Reader(SubscriptionDataConfig config, StreamReader stream, DateTime date, bool isLiveMode)
        {
            try
            {
                string cacheKey = $"{config.Symbol}_{config.Resolution}";

                // Load entire file into cache on first call
                if (_currentCacheKey != cacheKey || !_fileCache.ContainsKey(cacheKey))
                {
                    _fileCache[cacheKey] = LoadParquetFile(stream.BaseStream, config);
                    _currentCacheKey = cacheKey;
                    _currentIndex = 0;
                }

                // Return next bar from cache
                var bars = _fileCache[cacheKey];
                if (_currentIndex < bars.Count)
                {
                    var bar = bars[_currentIndex];
                    _currentIndex++;
                    return bar;
                }

                // No more data
                return null;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to read Parquet file for {config.Symbol}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Load all bars from Parquet file
        /// </summary>
        private List<ParquetMarketData> LoadParquetFile(Stream stream, SubscriptionDataConfig config)
        {
            var bars = new List<ParquetMarketData>();

            using (var fileReader = new ArrowFileReader(stream))
            {
                // Read schema to understand column structure
                Schema schema = fileReader.Schema;

                // Read all record batches
                RecordBatch recordBatch;
                while ((recordBatch = fileReader.ReadNextRecordBatch()) != null)
                {
                    // Get column indices
                    int timestampIdx = schema.GetFieldIndex("timestamp");
                    int openIdx = schema.GetFieldIndex("open");
                    int highIdx = schema.GetFieldIndex("high");
                    int lowIdx = schema.GetFieldIndex("low");
                    int closeIdx = schema.GetFieldIndex("close");
                    int volumeIdx = schema.GetFieldIndex("volume");

                    // Get arrays
                    var timestampArray = recordBatch.Column(timestampIdx);
                    var openArray = recordBatch.Column(openIdx) as DoubleArray;
                    var highArray = recordBatch.Column(highIdx) as DoubleArray;
                    var lowArray = recordBatch.Column(lowIdx) as DoubleArray;
                    var closeArray = recordBatch.Column(closeIdx) as DoubleArray;
                    var volumeArray = recordBatch.Column(volumeIdx) as DoubleArray;

                    // Process each row
                    for (int i = 0; i < recordBatch.Length; i++)
                    {
                        // Parse timestamp (could be different types)
                        DateTime timestamp = ParseTimestamp(timestampArray, i);

                        // Create bar
                        var bar = new ParquetMarketData
                        {
                            Symbol = config.Symbol,
                            Time = timestamp,
                            Open = (decimal)openArray.GetValue(i).Value,
                            High = (decimal)highArray.GetValue(i).Value,
                            Low = (decimal)lowArray.GetValue(i).Value,
                            Close = (decimal)closeArray.GetValue(i).Value,
                            Volume = (decimal)volumeArray.GetValue(i).Value,
                            Period = config.Resolution.ToTimeSpan()
                        };

                        bars.Add(bar);
                    }
                }
            }

            // Sort by timestamp
            bars = bars.OrderBy(b => b.Time).ToList();

            return bars;
        }

        /// <summary>
        /// Parse timestamp from Arrow array (handles multiple timestamp formats)
        /// </summary>
        private DateTime ParseTimestamp(IArrowArray array, int index)
        {
            // Try TimestampArray first
            if (array is TimestampArray timestampArray)
            {
                long? value = timestampArray.GetValue(index);
                if (!value.HasValue) throw new InvalidOperationException("Timestamp is null");
                // TimestampArray stores values in milliseconds
                return DateTimeOffset.FromUnixTimeMilliseconds(value.Value).DateTime;
            }

            // Try Int64Array (Unix timestamp in milliseconds)
            if (array is Int64Array int64Array)
            {
                long? unixMs = int64Array.GetValue(index);
                if (!unixMs.HasValue) throw new InvalidOperationException("Timestamp is null");
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs.Value).DateTime;
            }

            // Try Date64Array
            if (array is Date64Array date64Array)
            {
                long? value = date64Array.GetValue(index);
                if (!value.HasValue) throw new InvalidOperationException("Timestamp is null");
                return DateTimeOffset.FromUnixTimeMilliseconds(value.Value).DateTime;
            }

            throw new InvalidOperationException(
                $"Unsupported timestamp type: {array.Data.DataType.TypeId}");
        }

        /// <summary>
        /// Convert Lean Resolution to directory name
        /// </summary>
        private string GetResolutionDirectory(Resolution resolution)
        {
            switch (resolution)
            {
                case Resolution.Daily:
                    return "daily";
                case Resolution.Hour:
                    return "hourly";
                case Resolution.Minute:
                    return "minute";
                default:
                    throw new ArgumentException($"Unsupported resolution: {resolution}");
            }
        }

        /// <summary>
        /// Clone method for creating copies of the data
        /// </summary>
        public override BaseData Clone()
        {
            return new ParquetMarketData
            {
                Symbol = Symbol,
                Time = Time,
                Open = Open,
                High = High,
                Low = Low,
                Close = Close,
                Volume = Volume,
                Period = Period
            };
        }
    }
}
