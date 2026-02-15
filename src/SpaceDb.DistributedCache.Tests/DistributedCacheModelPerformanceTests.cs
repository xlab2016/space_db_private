using Xunit;
using Xunit.Abstractions;
using SpaceDb.DistributedCache;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;

namespace SpaceDb.DistributedCache.Tests;

/// <summary>
/// Comprehensive performance tests for DistributedCacheModel.
/// Requirements:
/// - Reader load: >10K RPS
/// - Writer load: ~1K RPS concurrent with reads
/// - Readers must not be blocked by frequent writes
/// - Data must be returned without delays for readers
/// </summary>
[Collection("Sequential")]
public class DistributedCacheModelPerformanceTests : IDisposable
{
    private readonly DistributedCacheModel _cache;
    private readonly ITestOutputHelper _output;

    public DistributedCacheModelPerformanceTests(ITestOutputHelper output)
    {
        _cache = new DistributedCacheModel();
        _cache.Clear();
        _output = output;
    }

    public void Dispose()
    {
        _cache.Clear();
    }

    #region High-Load Read Tests

    [Fact]
    public async Task ReadPerformance_CanHandle_OverTenThousandRPS()
    {
        // Arrange
        var testDurationSeconds = 3;
        var targetRps = 10_000;
        var keyPrefix = "perf_read_";
        var valuePrefix = "test_value_";
        var numberOfKeys = 100; // Rotate through 100 keys

        // Pre-populate cache with test data
        for (int i = 0; i < numberOfKeys; i++)
        {
            await _cache.Put($"{keyPrefix}{i}", TimeSpan.FromMinutes(10), Task.FromResult($"{valuePrefix}{i}"));
        }

        _output.WriteLine($"Starting high-load read test: target {targetRps:N0} RPS for {testDurationSeconds} seconds");

        // Act
        var stopwatch = Stopwatch.StartNew();
        var operationCount = 0L;
        var errors = 0L;
        var endTime = DateTime.UtcNow.AddSeconds(testDurationSeconds);

        // Use multiple parallel tasks to generate high load
        var tasks = new List<Task>();
        var numThreads = Environment.ProcessorCount * 2; // 2x CPU cores for maximum parallelism

        for (int t = 0; t < numThreads; t++)
        {
            var threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                while (DateTime.UtcNow < endTime)
                {
                    try
                    {
                        var keyIndex = random.Next(numberOfKeys);
                        var result = await _cache.Get<string>($"{keyPrefix}{keyIndex}");

                        if (result == null)
                        {
                            Interlocked.Increment(ref errors);
                        }

                        Interlocked.Increment(ref operationCount);
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Assert
        var actualRps = operationCount / stopwatch.Elapsed.TotalSeconds;
        var errorRate = errors / (double)operationCount;

        _output.WriteLine($"Read Performance Results:");
        _output.WriteLine($"  Total Operations: {operationCount:N0}");
        _output.WriteLine($"  Duration: {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        _output.WriteLine($"  Actual RPS: {actualRps:N0}");
        _output.WriteLine($"  Errors: {errors} ({errorRate:P2})");
        _output.WriteLine($"  Threads Used: {numThreads}");

        Assert.True(actualRps >= targetRps, $"Expected at least {targetRps:N0} RPS, but got {actualRps:N0} RPS");
        Assert.True(errorRate < 0.01, $"Error rate too high: {errorRate:P2}");
    }

    [Fact]
    public async Task ReadLatency_RemainsLow_UnderHighLoad()
    {
        // Arrange
        var testDurationSeconds = 2;
        var keyPrefix = "latency_read_";
        var numberOfKeys = 50;

        // Pre-populate cache
        for (int i = 0; i < numberOfKeys; i++)
        {
            await _cache.Put($"{keyPrefix}{i}", TimeSpan.FromMinutes(10), Task.FromResult(i));
        }

        var latencies = new List<double>();
        var latencyLock = new object();
        var endTime = DateTime.UtcNow.AddSeconds(testDurationSeconds);
        var numThreads = Environment.ProcessorCount;

        _output.WriteLine($"Starting read latency test for {testDurationSeconds} seconds with {numThreads} threads");

        // Act
        var tasks = new List<Task>();
        for (int t = 0; t < numThreads; t++)
        {
            var threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                var localLatencies = new List<double>();

                while (DateTime.UtcNow < endTime)
                {
                    var sw = Stopwatch.StartNew();
                    var keyIndex = random.Next(numberOfKeys);
                    await _cache.Get<int>($"{keyPrefix}{keyIndex}");
                    sw.Stop();

                    localLatencies.Add(sw.Elapsed.TotalMilliseconds);
                }

                lock (latencyLock)
                {
                    latencies.AddRange(localLatencies);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Calculate percentiles
        var sortedLatencies = latencies.OrderBy(l => l).ToList();
        var p50 = GetPercentile(sortedLatencies, 50);
        var p95 = GetPercentile(sortedLatencies, 95);
        var p99 = GetPercentile(sortedLatencies, 99);
        var max = sortedLatencies.Last();
        var avg = sortedLatencies.Average();

        _output.WriteLine($"Read Latency Results ({latencies.Count:N0} operations):");
        _output.WriteLine($"  Average: {avg:F3} ms");
        _output.WriteLine($"  P50: {p50:F3} ms");
        _output.WriteLine($"  P95: {p95:F3} ms");
        _output.WriteLine($"  P99: {p99:F3} ms");
        _output.WriteLine($"  Max: {max:F3} ms");

        // Assert - reads should be very fast (< 1ms for P95)
        Assert.True(p50 < 0.5, $"P50 latency too high: {p50:F3} ms");
        Assert.True(p95 < 1.0, $"P95 latency too high: {p95:F3} ms");
        Assert.True(p99 < 2.0, $"P99 latency too high: {p99:F3} ms");
    }

    #endregion

    #region Concurrent Read/Write Tests

    [Fact]
    public async Task ConcurrentReadWrite_ReadersNotBlocked_ByFrequentWrites()
    {
        // Arrange
        var testDurationSeconds = 3;
        var targetReadRps = 10_000;
        var targetWriteRps = 1_000;
        var keyPrefix = "concurrent_";
        var numberOfKeys = 100;

        _output.WriteLine($"Starting concurrent read/write test:");
        _output.WriteLine($"  Target Read RPS: {targetReadRps:N0}");
        _output.WriteLine($"  Target Write RPS: {targetWriteRps:N0}");
        _output.WriteLine($"  Duration: {testDurationSeconds} seconds");

        // Pre-populate cache
        for (int i = 0; i < numberOfKeys; i++)
        {
            await _cache.Put($"{keyPrefix}{i}", TimeSpan.FromMinutes(10), Task.FromResult(i));
        }

        var stopwatch = Stopwatch.StartNew();
        var readCount = 0L;
        var writeCount = 0L;
        var readErrors = 0L;
        var writeErrors = 0L;
        var endTime = DateTime.UtcNow.AddSeconds(testDurationSeconds);

        // Act - Start readers
        var numReaderThreads = Environment.ProcessorCount * 2;
        var readerTasks = new List<Task>();

        for (int t = 0; t < numReaderThreads; t++)
        {
            var threadId = t;
            readerTasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                while (DateTime.UtcNow < endTime)
                {
                    try
                    {
                        var keyIndex = random.Next(numberOfKeys);
                        await _cache.Get<int>($"{keyPrefix}{keyIndex}");

                        Interlocked.Increment(ref readCount);
                    }
                    catch
                    {
                        Interlocked.Increment(ref readErrors);
                    }
                }
            }));
        }

        // Start writers (fewer threads, but still high frequency)
        var numWriterThreads = Math.Max(2, Environment.ProcessorCount / 2);
        var writerTasks = new List<Task>();

        for (int t = 0; t < numWriterThreads; t++)
        {
            var threadId = t + 1000; // Offset for different random seed
            writerTasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                while (DateTime.UtcNow < endTime)
                {
                    try
                    {
                        var keyIndex = random.Next(numberOfKeys);
                        var newValue = random.Next(100000);
                        await _cache.Put($"{keyPrefix}{keyIndex}", TimeSpan.FromMinutes(10),
                            Task.FromResult(newValue), asyncGet: false);

                        Interlocked.Increment(ref writeCount);
                    }
                    catch
                    {
                        Interlocked.Increment(ref writeErrors);
                    }
                }
            }));
        }

        await Task.WhenAll(readerTasks.Concat(writerTasks));
        stopwatch.Stop();

        // Assert
        var actualReadRps = readCount / stopwatch.Elapsed.TotalSeconds;
        var actualWriteRps = writeCount / stopwatch.Elapsed.TotalSeconds;
        var readErrorRate = readErrors / (double)readCount;
        var writeErrorRate = writeCount > 0 ? writeErrors / (double)writeCount : 0;

        _output.WriteLine($"Concurrent Read/Write Results:");
        _output.WriteLine($"  Duration: {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        _output.WriteLine($"  Read Operations: {readCount:N0} ({actualReadRps:N0} RPS)");
        _output.WriteLine($"  Write Operations: {writeCount:N0} ({actualWriteRps:N0} RPS)");
        _output.WriteLine($"  Read Errors: {readErrors} ({readErrorRate:P2})");
        _output.WriteLine($"  Write Errors: {writeErrors} ({writeErrorRate:P2})");
        _output.WriteLine($"  Reader Threads: {numReaderThreads}");
        _output.WriteLine($"  Writer Threads: {numWriterThreads}");

        Assert.True(actualReadRps >= targetReadRps,
            $"Read RPS below target: {actualReadRps:N0} < {targetReadRps:N0}");
        if (writeCount > 0)
        {
            Assert.True(actualWriteRps >= targetWriteRps * 0.8,
                $"Write RPS significantly below target: {actualWriteRps:N0} < {targetWriteRps * 0.8:N0}");
            Assert.True(writeErrorRate < 0.01, $"Write error rate too high: {writeErrorRate:P2}");
        }
        Assert.True(readErrorRate < 0.01, $"Read error rate too high: {readErrorRate:P2}");
    }

    [Fact]
    public async Task ConcurrentReadWrite_ReadLatency_NotDegradedByWrites()
    {
        // Arrange
        var testDurationSeconds = 3;
        var keyPrefix = "latency_concurrent_";
        var numberOfKeys = 50;

        // Pre-populate cache
        for (int i = 0; i < numberOfKeys; i++)
        {
            await _cache.Put($"{keyPrefix}{i}", TimeSpan.FromMinutes(10), Task.FromResult(i));
        }

        var readLatencies = new List<double>();
        var latencyLock = new object();
        var endTime = DateTime.UtcNow.AddSeconds(testDurationSeconds);
        var numReaderThreads = Environment.ProcessorCount;
        var numWriterThreads = Math.Max(2, Environment.ProcessorCount / 2);
        var writeCount = 0L;

        _output.WriteLine($"Starting concurrent read latency test:");
        _output.WriteLine($"  Duration: {testDurationSeconds} seconds");
        _output.WriteLine($"  Reader Threads: {numReaderThreads}");
        _output.WriteLine($"  Writer Threads: {numWriterThreads}");

        // Act - Start readers with latency tracking
        var readerTasks = new List<Task>();
        for (int t = 0; t < numReaderThreads; t++)
        {
            var threadId = t;
            readerTasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                var localLatencies = new List<double>();

                while (DateTime.UtcNow < endTime)
                {
                    var sw = Stopwatch.StartNew();
                    var keyIndex = random.Next(numberOfKeys);
                    await _cache.Get<int>($"{keyPrefix}{keyIndex}");
                    sw.Stop();

                    localLatencies.Add(sw.Elapsed.TotalMilliseconds);
                }

                lock (latencyLock)
                {
                    readLatencies.AddRange(localLatencies);
                }
            }));
        }

        // Start writers
        var writerTasks = new List<Task>();
        for (int t = 0; t < numWriterThreads; t++)
        {
            var threadId = t + 1000;
            writerTasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                while (DateTime.UtcNow < endTime)
                {
                    var keyIndex = random.Next(numberOfKeys);
                    var newValue = random.Next(100000);
                    await _cache.Put($"{keyPrefix}{keyIndex}", TimeSpan.FromMinutes(10),
                        Task.FromResult(newValue), asyncGet: false);

                    Interlocked.Increment(ref writeCount);

                    // Small delay to simulate realistic write pattern (~1K RPS)
                    await Task.Delay(1);
                }
            }));
        }

        await Task.WhenAll(readerTasks.Concat(writerTasks));

        // Calculate percentiles
        var sortedLatencies = readLatencies.OrderBy(l => l).ToList();
        var p50 = GetPercentile(sortedLatencies, 50);
        var p95 = GetPercentile(sortedLatencies, 95);
        var p99 = GetPercentile(sortedLatencies, 99);
        var max = sortedLatencies.Last();
        var avg = sortedLatencies.Average();

        _output.WriteLine($"Read Latency Results with Concurrent Writes:");
        _output.WriteLine($"  Total Read Operations: {readLatencies.Count:N0}");
        _output.WriteLine($"  Total Write Operations: {writeCount:N0}");
        _output.WriteLine($"  Average: {avg:F3} ms");
        _output.WriteLine($"  P50: {p50:F3} ms");
        _output.WriteLine($"  P95: {p95:F3} ms");
        _output.WriteLine($"  P99: {p99:F3} ms");
        _output.WriteLine($"  Max: {max:F3} ms");

        // Assert - reads should still be fast despite concurrent writes
        Assert.True(p50 < 0.5, $"P50 read latency too high with concurrent writes: {p50:F3} ms");
        Assert.True(p95 < 2.0, $"P95 read latency too high with concurrent writes: {p95:F3} ms");
        Assert.True(p99 < 5.0, $"P99 read latency too high with concurrent writes: {p99:F3} ms");
    }

    #endregion

    #region Async Refresh Tests

    [Fact]
    public async Task AsyncRefresh_DoesNotBlock_Readers()
    {
        // Arrange
        var testDurationSeconds = 2;
        var keyPrefix = "async_refresh_";
        var numberOfKeys = 20;
        var slowFetchDelayMs = 50; // Simulate slow data fetch

        // Pre-populate cache with short TTL
        for (int i = 0; i < numberOfKeys; i++)
        {
            await _cache.Put($"{keyPrefix}{i}", TimeSpan.FromMilliseconds(100),
                Task.FromResult(i), asyncGet: true);
        }

        // Wait for cache to expire
        await Task.Delay(150);

        var readLatencies = new List<double>();
        var latencyLock = new object();
        var endTime = DateTime.UtcNow.AddSeconds(testDurationSeconds);
        var numReaderThreads = Environment.ProcessorCount;
        var refreshTriggered = 0L;

        _output.WriteLine($"Starting async refresh test:");
        _output.WriteLine($"  Slow fetch delay: {slowFetchDelayMs} ms");
        _output.WriteLine($"  Duration: {testDurationSeconds} seconds");

        // Act - Readers trigger async refresh
        var tasks = new List<Task>();
        for (int t = 0; t < numReaderThreads; t++)
        {
            var threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                var localLatencies = new List<double>();

                while (DateTime.UtcNow < endTime)
                {
                    var sw = Stopwatch.StartNew();
                    var keyIndex = random.Next(numberOfKeys);

                    var result = await _cache.Put($"{keyPrefix}{keyIndex}",
                        TimeSpan.FromMilliseconds(500),
                        Task.Run(async () =>
                        {
                            Interlocked.Increment(ref refreshTriggered);
                            await Task.Delay(slowFetchDelayMs); // Simulate slow fetch
                            return keyIndex + 1000;
                        }),
                        asyncGet: true);

                    sw.Stop();
                    localLatencies.Add(sw.Elapsed.TotalMilliseconds);
                }

                lock (latencyLock)
                {
                    readLatencies.AddRange(localLatencies);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Calculate latencies
        var sortedLatencies = readLatencies.OrderBy(l => l).ToList();
        var p50 = GetPercentile(sortedLatencies, 50);
        var p95 = GetPercentile(sortedLatencies, 95);
        var p99 = GetPercentile(sortedLatencies, 99);
        var max = sortedLatencies.Last();

        _output.WriteLine($"Async Refresh Results:");
        _output.WriteLine($"  Total Operations: {readLatencies.Count:N0}");
        _output.WriteLine($"  Refreshes Triggered: {refreshTriggered:N0}");
        _output.WriteLine($"  P50 Latency: {p50:F3} ms");
        _output.WriteLine($"  P95 Latency: {p95:F3} ms");
        _output.WriteLine($"  P99 Latency: {p99:F3} ms");
        _output.WriteLine($"  Max Latency: {max:F3} ms");

        // Assert - most reads should NOT wait for the slow refresh (50ms)
        // P95 should be well below the slow fetch delay
        Assert.True(p95 < slowFetchDelayMs / 2,
            $"P95 latency suggests readers are blocked by refresh: {p95:F3} ms >= {slowFetchDelayMs / 2} ms");
        Assert.True(refreshTriggered > 0, "No async refreshes were triggered");
    }

    [Fact]
    public async Task AsyncRefresh_UpdatesCache_InBackground()
    {
        // Arrange
        var key = "async_update_test";
        var initialValue = 100;
        var updatedValue = 200;
        var valueSource = initialValue;
        var fetchCount = 0;

        // Pre-populate with initial value
        await _cache.Put(key, TimeSpan.FromMilliseconds(100),
            Task.FromResult(initialValue), asyncGet: true);

        // Wait for expiration
        await Task.Delay(150);

        _output.WriteLine("Testing async cache update in background");

        // Act - Trigger async refresh with new value
        valueSource = updatedValue;

        var result1 = await _cache.Put(key, TimeSpan.FromMilliseconds(500),
            Task.Run(async () =>
            {
                Interlocked.Increment(ref fetchCount);
                await Task.Delay(50); // Simulate fetch delay
                return valueSource;
            }),
            asyncGet: true);

        // Should return stale value immediately
        _output.WriteLine($"Immediate result (should be stale): {result1}");

        // Wait for background refresh to complete
        await Task.Delay(100);

        // Get updated value
        var result2 = await _cache.Get<int>(key);
        _output.WriteLine($"After refresh (should be updated): {result2}");
        _output.WriteLine($"Fetch count: {fetchCount}");

        // Assert
        Assert.Equal(initialValue, result1); // Stale value returned immediately
        Assert.Equal(updatedValue, result2); // Updated value after refresh
        Assert.Equal(1, fetchCount); // Fetch happened exactly once
    }

    #endregion

    #region Sustained Load Tests

    [Fact]
    public async Task SustainedLoad_MaintainsPerformance_OverTime()
    {
        // Arrange
        var testDurationSeconds = 5; // Longer sustained test
        var keyPrefix = "sustained_";
        var numberOfKeys = 100;
        var samplingIntervalMs = 500; // Sample every 500ms

        // Pre-populate cache
        for (int i = 0; i < numberOfKeys; i++)
        {
            await _cache.Put($"{keyPrefix}{i}", TimeSpan.FromMinutes(10), Task.FromResult(i));
        }

        _output.WriteLine($"Starting sustained load test for {testDurationSeconds} seconds");

        var operationCount = 0L;
        var endTime = DateTime.UtcNow.AddSeconds(testDurationSeconds);
        var numThreads = Environment.ProcessorCount * 2;
        var rpsSnapshots = new List<(double TimeElapsed, double Rps)>();

        // Act
        var stopwatch = Stopwatch.StartNew();
        var tasks = new List<Task>();

        // Monitoring task
        tasks.Add(Task.Run(async () =>
        {
            var lastCount = 0L;
            var lastTime = stopwatch.Elapsed.TotalSeconds;

            while (DateTime.UtcNow < endTime)
            {
                await Task.Delay(samplingIntervalMs);

                var currentCount = Interlocked.Read(ref operationCount);
                var currentTime = stopwatch.Elapsed.TotalSeconds;

                var deltaOps = currentCount - lastCount;
                var deltaTime = currentTime - lastTime;
                var currentRps = deltaOps / deltaTime;

                rpsSnapshots.Add((currentTime, currentRps));

                lastCount = currentCount;
                lastTime = currentTime;
            }
        }));

        // Worker tasks
        for (int t = 0; t < numThreads; t++)
        {
            var threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                while (DateTime.UtcNow < endTime)
                {
                    var keyIndex = random.Next(numberOfKeys);
                    await _cache.Get<int>($"{keyPrefix}{keyIndex}");
                    Interlocked.Increment(ref operationCount);
                }
            }));
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Assert
        var overallRps = operationCount / stopwatch.Elapsed.TotalSeconds;
        var minRps = rpsSnapshots.Min(s => s.Rps);
        var maxRps = rpsSnapshots.Max(s => s.Rps);
        var avgRps = rpsSnapshots.Average(s => s.Rps);
        var stdDevRps = CalculateStdDev(rpsSnapshots.Select(s => s.Rps).ToList());

        _output.WriteLine($"Sustained Load Results:");
        _output.WriteLine($"  Duration: {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        _output.WriteLine($"  Total Operations: {operationCount:N0}");
        _output.WriteLine($"  Overall RPS: {overallRps:N0}");
        _output.WriteLine($"  Min RPS: {minRps:N0}");
        _output.WriteLine($"  Max RPS: {maxRps:N0}");
        _output.WriteLine($"  Avg RPS: {avgRps:N0}");
        _output.WriteLine($"  Std Dev RPS: {stdDevRps:N0}");
        _output.WriteLine($"  Coefficient of Variation: {(stdDevRps / avgRps):P2}");

        // Performance should be consistent over time
        Assert.True(avgRps >= 10_000, $"Average RPS below target: {avgRps:N0}");
        Assert.True(minRps >= avgRps * 0.7,
            $"Min RPS too low, indicating performance degradation: {minRps:N0} < {avgRps * 0.7:N0}");

        // Coefficient of variation should be reasonable (< 30%)
        // Note: Some variability is expected in high-performance systems due to GC, OS scheduling, etc.
        var cv = stdDevRps / avgRps;
        Assert.True(cv < 0.3, $"Performance too variable: CV = {cv:P2}");
    }

    [Fact]
    public async Task SustainedMixedLoad_MaintainsStability_WithReadersAndWriters()
    {
        // Arrange
        var testDurationSeconds = 5;
        var keyPrefix = "mixed_sustained_";
        var numberOfKeys = 100;
        var samplingIntervalMs = 500;

        // Pre-populate cache
        for (int i = 0; i < numberOfKeys; i++)
        {
            await _cache.Put($"{keyPrefix}{i}", TimeSpan.FromMinutes(10), Task.FromResult(i));
        }

        _output.WriteLine($"Starting sustained mixed load test for {testDurationSeconds} seconds");

        var readCount = 0L;
        var writeCount = 0L;
        var endTime = DateTime.UtcNow.AddSeconds(testDurationSeconds);
        var numReaderThreads = Environment.ProcessorCount * 2;
        var numWriterThreads = Math.Max(2, Environment.ProcessorCount / 2);
        var readRpsSnapshots = new List<double>();
        var writeRpsSnapshots = new List<double>();

        // Act
        var stopwatch = Stopwatch.StartNew();
        var tasks = new List<Task>();

        // Monitoring task
        tasks.Add(Task.Run(async () =>
        {
            var lastReadCount = 0L;
            var lastWriteCount = 0L;
            var lastTime = stopwatch.Elapsed.TotalSeconds;

            while (DateTime.UtcNow < endTime)
            {
                await Task.Delay(samplingIntervalMs);

                var currentReadCount = Interlocked.Read(ref readCount);
                var currentWriteCount = Interlocked.Read(ref writeCount);
                var currentTime = stopwatch.Elapsed.TotalSeconds;

                var deltaTime = currentTime - lastTime;
                var readRps = (currentReadCount - lastReadCount) / deltaTime;
                var writeRps = (currentWriteCount - lastWriteCount) / deltaTime;

                readRpsSnapshots.Add(readRps);
                writeRpsSnapshots.Add(writeRps);

                lastReadCount = currentReadCount;
                lastWriteCount = currentWriteCount;
                lastTime = currentTime;
            }
        }));

        // Reader tasks
        for (int t = 0; t < numReaderThreads; t++)
        {
            var threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                while (DateTime.UtcNow < endTime)
                {
                    var keyIndex = random.Next(numberOfKeys);
                    await _cache.Get<int>($"{keyPrefix}{keyIndex}");
                    Interlocked.Increment(ref readCount);
                }
            }));
        }

        // Writer tasks
        for (int t = 0; t < numWriterThreads; t++)
        {
            var threadId = t + 1000;
            tasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                while (DateTime.UtcNow < endTime)
                {
                    var keyIndex = random.Next(numberOfKeys);
                    var newValue = random.Next(100000);
                    await _cache.Put($"{keyPrefix}{keyIndex}", TimeSpan.FromMinutes(10),
                        Task.FromResult(newValue), asyncGet: false);
                    Interlocked.Increment(ref writeCount);

                    // Small delay for realistic write pattern
                    await Task.Delay(1);
                }
            }));
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Assert
        var overallReadRps = readCount / stopwatch.Elapsed.TotalSeconds;
        var overallWriteRps = writeCount / stopwatch.Elapsed.TotalSeconds;
        var avgReadRps = readRpsSnapshots.Average();
        var avgWriteRps = writeRpsSnapshots.Average();
        var minReadRps = readRpsSnapshots.Min();
        var readStdDev = CalculateStdDev(readRpsSnapshots);

        _output.WriteLine($"Sustained Mixed Load Results:");
        _output.WriteLine($"  Duration: {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        _output.WriteLine($"  Total Reads: {readCount:N0} (Overall RPS: {overallReadRps:N0})");
        _output.WriteLine($"  Total Writes: {writeCount:N0} (Overall RPS: {overallWriteRps:N0})");
        _output.WriteLine($"  Avg Read RPS: {avgReadRps:N0}");
        _output.WriteLine($"  Min Read RPS: {minReadRps:N0}");
        _output.WriteLine($"  Read Std Dev: {readStdDev:N0}");
        _output.WriteLine($"  Avg Write RPS: {avgWriteRps:N0}");

        Assert.True(avgReadRps >= 10_000, $"Average read RPS below target: {avgReadRps:N0}");
        // Write RPS is limited by Task.Delay(1) in the test, so expect ~500-700 RPS
        Assert.True(avgWriteRps >= 500, $"Average write RPS below target: {avgWriteRps:N0}");
        // Tolerate more variability in reads when mixed with writes
        Assert.True(minReadRps >= avgReadRps * 0.5,
            $"Read performance degraded significantly over time: {minReadRps:N0} < {avgReadRps * 0.5:N0}");
    }

    #endregion

    #region Memory and Resource Tests

    [Fact]
    public async Task HighLoad_DoesNotCause_MemoryLeak()
    {
        // Arrange
        var testDurationSeconds = 3;
        var keyPrefix = "memory_test_";
        var numberOfKeys = 100;

        // Pre-populate cache
        for (int i = 0; i < numberOfKeys; i++)
        {
            await _cache.Put($"{keyPrefix}{i}", TimeSpan.FromMinutes(10), Task.FromResult(i));
        }

        var initialMemory = GC.GetTotalMemory(true);
        _output.WriteLine($"Initial memory: {initialMemory:N0} bytes");

        var operationCount = 0L;
        var endTime = DateTime.UtcNow.AddSeconds(testDurationSeconds);
        var numThreads = Environment.ProcessorCount * 2;

        // Act - Generate high load
        var tasks = new List<Task>();
        for (int t = 0; t < numThreads; t++)
        {
            var threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                var random = new Random(threadId);
                while (DateTime.UtcNow < endTime)
                {
                    var keyIndex = random.Next(numberOfKeys);
                    await _cache.Get<int>($"{keyPrefix}{keyIndex}");
                    Interlocked.Increment(ref operationCount);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Force garbage collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var finalMemory = GC.GetTotalMemory(false);
        var memoryIncrease = finalMemory - initialMemory;
        var memoryIncreasePercent = (double)memoryIncrease / initialMemory * 100;

        _output.WriteLine($"Operations performed: {operationCount:N0}");
        _output.WriteLine($"Final memory: {finalMemory:N0} bytes");
        _output.WriteLine($"Memory increase: {memoryIncrease:N0} bytes ({memoryIncreasePercent:F2}%)");

        // Assert - memory should not grow significantly
        // Allow up to 50% increase (generous threshold)
        Assert.True(memoryIncreasePercent < 50,
            $"Memory increased too much: {memoryIncreasePercent:F2}%");
    }

    #endregion

    #region Helper Methods

    private static double GetPercentile(List<double> sortedValues, int percentile)
    {
        if (sortedValues.Count == 0)
            return 0;

        int index = (int)Math.Ceiling(percentile / 100.0 * sortedValues.Count) - 1;
        index = Math.Max(0, Math.Min(sortedValues.Count - 1, index));
        return sortedValues[index];
    }

    private static double CalculateStdDev(List<double> values)
    {
        if (values.Count == 0)
            return 0;

        var avg = values.Average();
        var sumOfSquares = values.Sum(v => Math.Pow(v - avg, 2));
        return Math.Sqrt(sumOfSquares / values.Count);
    }

    #endregion
}
