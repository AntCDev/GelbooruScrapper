using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GelbooruArchiver
{
    class Program
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        private static long _totalDownloadedBytes = 0;
        private static int _filesProcessed = 0;
        private static int _postsSeen = 0;
        private static DateTime _startTime;

        private static readonly object _logLock = new object();
        private static readonly object _trackerLock = new object();

        private static ConcurrentDictionary<long, string> _downloadTracker = new ConcurrentDictionary<long, string>();
        private static string _trackerFilePath;

        static async Task Main(string[] args)
        {
            string tags = "1girl";
            string apiKey = "";
            string userId = "";
            string saveDir = @"G:\GelBooru";
            double maxSizeGb = 100;
            int maxConcurrency = 4;
            int timeoutSeconds = 100;
            int mediaMode = 0; // 0 = images only, 1 = all, 2 = video/gif only
            int maxRetries = 3;
            int delayMs = 500;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "-t": case "--tags": tags = args[++i]; break;
                    case "-k": case "--apikey": apiKey = args[++i]; break;
                    case "-u": case "--userid": userId = args[++i]; break;
                    case "-d": case "--dir": saveDir = args[++i]; break;
                    case "-s": case "--size": maxSizeGb = double.Parse(args[++i]); break;
                    case "-c": case "--concurrency": maxConcurrency = int.Parse(args[++i]); break;
                    case "-to": case "--timeout": timeoutSeconds = int.Parse(args[++i]); break;
                    case "-m": case "--media": mediaMode = int.Parse(args[++i]); break;
                    case "-r": case "--retries": maxRetries = int.Parse(args[++i]); break;
                    case "-dm": case "--delay": delayMs = int.Parse(args[++i]); break;
                }
            }

            long maxSizeBytes = (long)(maxSizeGb * 1024 * 1024 * 1024);
            Directory.CreateDirectory(saveDir);

            _trackerFilePath = Path.Combine(saveDir, "download_tracker.json");
            LoadTracker();

            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://gelbooru.com/");

            _startTime = DateTime.Now;

            string mediaString = mediaMode switch
            {
                0 => "Images only (Skipping MP4/WebM/GIF)",
                1 => "All media types",
                2 => "Videos/GIFs only (Skipping standard images)",
                _ => "Images only (Skipping MP4/WebM/GIF)"
            };

            Console.WriteLine($"Target directory: {saveDir}");
            Console.WriteLine($"Size limit: {maxSizeGb} GB");
            Console.WriteLine($"Tags: {tags}");
            Console.WriteLine($"Concurrency: {maxConcurrency} simultaneous downloads");
            Console.WriteLine($"Timeout: {timeoutSeconds} seconds");
            Console.WriteLine($"Media Mode: {mediaString}");
            Console.WriteLine($"Max Retries: {maxRetries}");
            Console.WriteLine($"Retry Delay: {delayMs} ms");
            Console.WriteLine($"Previously downloaded files tracked: {_downloadTracker.Count}");
            Console.WriteLine($"Started at: {_startTime:yyyy-MM-dd HH:mm:ss}\n");

            using var cts = new CancellationTokenSource();

            try
            {
                await RunLockstepArchiverAsync(tags, apiKey, userId, saveDir, maxSizeBytes, maxConcurrency, mediaMode, maxRetries, delayMs, cts);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n[STOPPED] Target size reached or operation cancelled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[FATAL ERROR] {ex.Message}");
            }
            finally
            {
                SaveTracker();
            }

            double totalGb = (double)_totalDownloadedBytes / (1024 * 1024 * 1024);
            double elapsedMin = (DateTime.Now - _startTime).TotalMinutes;
            double avgRate = elapsedMin > 0 ? _filesProcessed / elapsedMin : 0;

            Console.WriteLine($"\nFinished!");
            Console.WriteLine($"Posts seen from API: {_postsSeen}");
            Console.WriteLine($"Unique files successfully downloaded: {_filesProcessed}");
            Console.WriteLine($"Total size downloaded this run: {totalGb:F4} GB");
            Console.WriteLine($"Average rate: {avgRate:F1} img/min over {elapsedMin:F1} minutes");
        }

        static void LoadTracker()
        {
            if (File.Exists(_trackerFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_trackerFilePath);
                    var loadedDict = JsonSerializer.Deserialize<Dictionary<long, string>>(json);
                    if (loadedDict != null)
                    {
                        _downloadTracker = new ConcurrentDictionary<long, string>(loadedDict);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARNING] Failed to load tracker JSON: {ex.Message}. Starting fresh.");
                }
            }
        }

        static void SaveTracker()
        {
            lock (_trackerLock)
            {
                try
                {
                    var dictToSave = new Dictionary<long, string>(_downloadTracker);
                    string json = JsonSerializer.Serialize(dictToSave, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_trackerFilePath, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to save tracker JSON: {ex.Message}");
                }
            }
        }

        static async Task RunLockstepArchiverAsync(string tags, string apiKey, string userId, string saveDir, long maxSizeBytes, int maxConcurrency, int mediaMode, int maxRetries, int delayMs, CancellationTokenSource cts)
        {
            string formattedOriginalTags = tags.Trim().Replace(" ", "+");
            string currentSearchTags = formattedOriginalTags;
            int batchCounter = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cts.Token
            };

            Console.WriteLine($"[INFO] Starting search using ID-based cursor...");

            while (!cts.IsCancellationRequested)
            {
                string url = $"https://gelbooru.com/index.php?page=dapi&s=post&q=index&json=1&limit=100&pid=0&tags={currentSearchTags}";
                if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(userId))
                {
                    url += $"&api_key={apiKey}&user_id={userId}";
                }

                List<JsonElement> currentBatch = await FetchPageWithRetryAsync(url, batchCounter, maxRetries, delayMs, cts.Token);

                if (currentBatch == null || currentBatch.Count == 0)
                {
                    Console.WriteLine("\n[INFO] No more posts in current ID range. Reached the end of the results.");
                    break;
                }

                Interlocked.Add(ref _postsSeen, currentBatch.Count);
                int filesProcessedBeforeBatch = _filesProcessed;

                await Parallel.ForEachAsync(currentBatch, parallelOptions, async (post, token) =>
                {
                    await ProcessAndDownloadPostAsync(post, saveDir, maxSizeBytes, mediaMode, maxRetries, delayMs, cts);
                });

                int downloadedThisBatch = _filesProcessed - filesProcessedBeforeBatch;
                Console.WriteLine($"[INFO] Batch {batchCounter} processed | Scanned: {currentBatch.Count} | Downloaded: {downloadedThisBatch}");

                SaveTracker();

                long minIdInBatch = GetMinIdFromBatch(currentBatch);

                if (minIdInBatch == long.MaxValue)
                {
                    Console.WriteLine("\n[INFO] No valid IDs found.");
                    break;
                }

                currentSearchTags = $"{formattedOriginalTags}+id:<{minIdInBatch}";
                batchCounter++;
            }
        }

        static async Task<List<JsonElement>> FetchPageWithRetryAsync(string url, int batchCounter, int maxRetries, int delayMs, CancellationToken token)
        {
            int retries = maxRetries;
            while (retries > 0 && !token.IsCancellationRequested)
            {
                try
                {
                    HttpResponseMessage response = await _httpClient.GetAsync(url, token);
                    string responseContent = await response.Content.ReadAsStringAsync(token);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[HTTP ERROR] batch={batchCounter} | Status: {(int)response.StatusCode}");
                        retries--;
                        await Task.Delay(delayMs, token);
                        continue;
                    }

                    using JsonDocument doc = JsonDocument.Parse(responseContent);
                    List<JsonElement> batch = new List<JsonElement>();

                    if (doc.RootElement.TryGetProperty("post", out JsonElement postsArray) && postsArray.GetArrayLength() > 0)
                    {
                        foreach (JsonElement post in postsArray.EnumerateArray())
                        {
                            batch.Add(post.Clone());
                        }
                    }
                    return batch;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FETCH ERROR] batch={batchCounter}: {ex.Message}");
                    retries--;
                    await Task.Delay(delayMs, token);
                }
            }
            return null;
        }

        static long GetMinIdFromBatch(List<JsonElement> batch)
        {
            long minId = long.MaxValue;
            foreach (var post in batch)
            {
                if (post.TryGetProperty("id", out JsonElement idEl) && idEl.TryGetInt64(out long id))
                {
                    if (id < minId) minId = id;
                }
            }
            return minId;
        }

        static async Task ProcessAndDownloadPostAsync(JsonElement post, string saveDir, long maxSizeBytes, int mediaMode, int maxRetries, int delayMs, CancellationTokenSource cts)
        {
            if (cts.IsCancellationRequested) return;

            if (!post.TryGetProperty("id", out JsonElement idEl) || !idEl.TryGetInt64(out long postId)) return;

            if (_downloadTracker.ContainsKey(postId)) return;

            if (!post.TryGetProperty("file_url", out JsonElement urlElement)) return;
            string imageUrl = urlElement.GetString();
            if (string.IsNullOrEmpty(imageUrl)) return;

            string originalFilename = Path.GetFileName(new Uri(imageUrl).LocalPath);
            string extension = Path.GetExtension(originalFilename).ToLower();
            bool isVideoOrGif = extension == ".mp4" || extension == ".webm" || extension == ".gif";

            if (mediaMode == 0 && isVideoOrGif) return;
            if (mediaMode == 2 && !isVideoOrGif) return;

            string jsonFilepath = Path.Combine(saveDir, $"{postId}.json");
            string finalFilepath = Path.Combine(saveDir, $"{postId}{extension}");

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using HttpResponseMessage imgResponse = await _httpClient.GetAsync(imageUrl, cts.Token);

                    if (!imgResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[WARNING] Post {postId} attempt {attempt} failed: {imgResponse.StatusCode} | URL: {imageUrl}");
                        if (attempt < maxRetries) await Task.Delay(delayMs, cts.Token);
                        continue;
                    }

                    if (imgResponse.Content.Headers.ContentType?.MediaType?.Contains("text/html") == true)
                    {
                        Console.WriteLine($"[CLOUDFLARE] Post {postId} attempt {attempt} intercepted by HTML.");
                        if (attempt < maxRetries) await Task.Delay(delayMs, cts.Token);
                        continue;
                    }

                    byte[] imageBytes = await imgResponse.Content.ReadAsByteArrayAsync(cts.Token);
                    string jsonString;
                    using (var stream = new MemoryStream())
                    {
                        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                        {
                            post.WriteTo(writer);
                        }
                        jsonString = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                    }

                    await File.WriteAllBytesAsync(finalFilepath, imageBytes, cts.Token);
                    await File.WriteAllTextAsync(jsonFilepath, jsonString, cts.Token);

                    _downloadTracker.TryAdd(postId, originalFilename);

                    long newSize = Interlocked.Add(ref _totalDownloadedBytes, imageBytes.Length + jsonString.Length);
                    int currentCount = Interlocked.Increment(ref _filesProcessed);

                    if (currentCount % 100 == 0)
                    {
                        double elapsedMinutes = (DateTime.Now - _startTime).TotalMinutes;
                        double rate = elapsedMinutes > 0 ? currentCount / elapsedMinutes : 0;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{currentCount} files] Downloaded: {(double)newSize / (1024 * 1024 * 1024):F4} GB | Rate: {rate:F1} item/min");
                    }

                    if (newSize >= maxSizeBytes)
                    {
                        cts.Cancel();
                    }

                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Post {postId} attempt {attempt} threw an exception: {ex.Message}");
                    if (attempt < maxRetries) await Task.Delay(delayMs, cts.Token);
                }
            }

            Console.WriteLine($"[FAILED] Post {postId} completely failed after {maxRetries} attempts.");
            LogFailedDownload(postId, originalFilename, saveDir, $"Failed after {maxRetries} attempts.");
        }

        static void LogFailedDownload(long postId, string filename, string saveDir, string errorMessage)
        {
            string logFilePath = Path.Combine(saveDir, "failed_downloads.log");
            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Post ID: {postId} | File: {filename} | Error: {errorMessage}{Environment.NewLine}";

            lock (_logLock)
            {
                File.AppendAllText(logFilePath, logEntry);
            }
        }
    }
}