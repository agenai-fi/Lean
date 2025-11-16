/*
 * ALPHASEEK CUSTOM DATA READER
 * Reads Binance Parquet files for cryptocurrency backtesting
 *
 * Requires Parquet.Net NuGet package
 * Add to Common/QuantConnect.csproj:
 * <PackageReference Include="Parquet.Net" Version="4.20.0" />
 *
 * Data path: /app/data/market/binance/crypto/binance/{resolution}/{symbol}_YYYYMMDD_YYYYMMDD.parquet
 * Columns: timestamp (index), open, high, low, close, volume (all float64)
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Parquet;
using Parquet.Data;
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

        // Instance-level cache for storing all bars from the file
        private List<ParquetMarketData> _bars = null;
        private int _currentIndex = 0;
        private string _cachedFilePath = null;

        /// <summary>
        /// Constructor for debugging
        /// </summary>
        public ParquetMarketData()
        {
            Console.WriteLine("[PARQUET] ParquetMarketData() CONSTRUCTOR CALLED");
        }

        /// <summary>
        /// Return the URL source for the subscription
        /// </summary>
        /// <param name="config">Subscription configuration</param>
        /// <param name="date">Date for the data</param>
        /// <param name="isLiveMode">Live trading mode flag</param>
        /// <returns>Subscription data source with file path</returns>
        public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
        {
            Console.WriteLine($"[PARQUET] GetSource() - Symbol: {config.Symbol}, Date: {date}");

            // Convert resolution to directory name
            string resolution = GetResolutionDirectory(config.Resolution);

            // Build directory path
            string directoryPath = Path.Combine(DATA_PATH, resolution);
            Console.WriteLine($"[PARQUET] Directory path: {directoryPath}");

            // Find Parquet file matching symbol pattern
            // Files are named: {SYMBOL}_YYYYMMDD_YYYYMMDD.parquet
            string pattern = $"{config.Symbol.Value}_*.parquet";
            Console.WriteLine($"[PARQUET] Pattern: {pattern}");

            if (Directory.Exists(directoryPath))
            {
                Console.WriteLine($"[PARQUET] Directory exists");
                var files = Directory.GetFiles(directoryPath, pattern);
                Console.WriteLine($"[PARQUET] Found {files.Length} files matching pattern");
                if (files.Length > 0)
                {
                    _cachedFilePath = files[0];  // Cache for later use in Reader
                    Console.WriteLine($"[PARQUET] Using file: {_cachedFilePath}");
                    Console.WriteLine($"[PARQUET] File exists: {File.Exists(_cachedFilePath)}");
                    Console.WriteLine($"[PARQUET] File size: {new FileInfo(_cachedFilePath).Length} bytes");
                    // Use the first matching file (should only be one)
                    // Use FileFormat.Csv - TextSubscriptionDataSourceReader will detect StreamReader implementation
                    return new SubscriptionDataSource(_cachedFilePath, SubscriptionTransportMedium.LocalFile);
                }
            }
            else
            {
                Console.WriteLine($"[PARQUET] Directory does NOT exist");
            }

            // Fallback to direct path construction
            string filePath = Path.Combine(directoryPath, $"{config.Symbol.Value}.parquet");
            Console.WriteLine($"[PARQUET] Fallback path: {filePath}");
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
                Console.WriteLine($"[PARQUET] Reader() ENTRY - Symbol: {config.Symbol}, Date: {date}, _bars==null: {_bars == null}");

                // Load entire file into instance cache on first call
                if (_bars == null)
                {
                    Console.WriteLine($"[PARQUET] Loading file for {config.Symbol}");
                    _bars = LoadParquetFile(stream.BaseStream, config);
                    _currentIndex = 0;
                    Console.WriteLine($"[PARQUET] Loaded {_bars.Count} bars for {config.Symbol}");
                }

                // Return next bar from instance cache
                if (_currentIndex < _bars.Count)
                {
                    var bar = _bars[_currentIndex];
                    _currentIndex++;
                    Console.WriteLine($"[PARQUET] Returning bar {_currentIndex}/{_bars.Count} - {bar.Time}");
                    return bar;
                }

                // No more data
                Console.WriteLine($"[PARQUET] No more data for {config.Symbol}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PARQUET] ERROR in Reader(): {ex.Message}");
                throw new InvalidOperationException(
                    $"Failed to read Parquet file for {config.Symbol}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get the file path for the Parquet file (uses cached path from GetSource)
        /// </summary>
        private string GetFilePath(SubscriptionDataConfig config)
        {
            if (_cachedFilePath != null)
            {
                return _cachedFilePath;
            }

            // Fallback: reconstruct the path (should not reach here in normal operation)
            string resolution = GetResolutionDirectory(config.Resolution);
            string directoryPath = Path.Combine(DATA_PATH, resolution);
            string pattern = $"{config.Symbol.Value}_*.parquet";

            var files = Directory.GetFiles(directoryPath, pattern);
            if (files.Length > 0)
            {
                return files[0];
            }

            throw new FileNotFoundException($"Parquet file not found for {config.Symbol}");
        }

        /// <summary>
        /// Load all bars from Parquet file
        /// </summary>
        private List<ParquetMarketData> LoadParquetFile(Stream stream, SubscriptionDataConfig config)
        {
            Console.WriteLine($"[PARQUET] LoadParquetFile() ENTRY - Symbol: {config.Symbol}");
            var bars = new List<ParquetMarketData>();

            // Get the raw file stream directly (not from StreamReader)
            Console.WriteLine($"[PARQUET] Getting file stream directly from path...");
            string filePath = GetFilePath(config);
            Console.WriteLine($"[PARQUET] Opening file: {filePath}");

            using (var fileStream = File.OpenRead(filePath))
            {
                Console.WriteLine($"[PARQUET] File stream length: {fileStream.Length} bytes");

                Console.WriteLine($"[PARQUET] Opening Parquet file...");
                using (var parquetReader = ParquetReader.CreateAsync(fileStream).Result)
                {
                    Console.WriteLine($"[PARQUET] Parquet file opened - Row groups: {parquetReader.RowGroupCount}");

                    // Read row groups (Parquet.Net 4.x API)
                    for (int rg = 0; rg < parquetReader.RowGroupCount; rg++)
                    {
                        Console.WriteLine($"[PARQUET] Reading row group {rg + 1}/{parquetReader.RowGroupCount}");
                        using (var rowGroupReader = parquetReader.OpenRowGroupReader(rg))
                        {
                            // Get data fields (use var for type inference)
                            var dataFields = parquetReader.Schema.GetDataFields();
                            Console.WriteLine($"[PARQUET] Found {dataFields.Length} data fields");

                            // Find field indices by name (use var for type inference)
                            var timestampField = dataFields.First(f => f.Name == "timestamp");
                            var openField = dataFields.First(f => f.Name == "open");
                            var highField = dataFields.First(f => f.Name == "high");
                            var lowField = dataFields.First(f => f.Name == "low");
                            var closeField = dataFields.First(f => f.Name == "close");
                            var volumeField = dataFields.First(f => f.Name == "volume");

                            // Read columns
                            DataColumn timestampCol = rowGroupReader.ReadColumnAsync(timestampField).Result;
                            DataColumn openCol = rowGroupReader.ReadColumnAsync(openField).Result;
                            DataColumn highCol = rowGroupReader.ReadColumnAsync(highField).Result;
                            DataColumn lowCol = rowGroupReader.ReadColumnAsync(lowField).Result;
                            DataColumn closeCol = rowGroupReader.ReadColumnAsync(closeField).Result;
                            DataColumn volumeCol = rowGroupReader.ReadColumnAsync(volumeField).Result;

                            int rowCount = timestampCol.Data.Length;
                            Console.WriteLine($"[PARQUET] Row group has {rowCount} rows");

                            // Process each row
                            for (int i = 0; i < rowCount; i++)
                            {
                                if (i % 1000 == 0 && i > 0)
                                {
                                    Console.WriteLine($"[PARQUET] Processing row {i}/{rowCount}");
                                }

                                // Parse timestamp
                                DateTime timestamp = ((DateTimeOffset)timestampCol.Data.GetValue(i)).UtcDateTime;

                                // Create bar
                                var bar = new ParquetMarketData
                                {
                                    Symbol = config.Symbol,
                                    Time = timestamp,
                                    Open = Convert.ToDecimal(openCol.Data.GetValue(i)),
                                    High = Convert.ToDecimal(highCol.Data.GetValue(i)),
                                    Low = Convert.ToDecimal(lowCol.Data.GetValue(i)),
                                    Close = Convert.ToDecimal(closeCol.Data.GetValue(i)),
                                    Volume = Convert.ToDecimal(volumeCol.Data.GetValue(i)),
                                    Period = config.Resolution.ToTimeSpan()
                                };

                                bars.Add(bar);
                            }
                            Console.WriteLine($"[PARQUET] Row group processed - Total bars so far: {bars.Count}");
                        }
                    }
                    Console.WriteLine($"[PARQUET] All row groups processed - Total bars: {bars.Count}");
                }
            }

            // Sort by timestamp
            Console.WriteLine($"[PARQUET] Sorting {bars.Count} bars by timestamp...");
            bars = bars.OrderBy(b => b.Time).ToList();
            Console.WriteLine($"[PARQUET] LoadParquetFile() COMPLETE - Returning {bars.Count} bars");

            return bars;
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
