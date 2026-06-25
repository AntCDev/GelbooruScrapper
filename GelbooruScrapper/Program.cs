using System;
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
        private static DateTime _startTime;

        // Thread lock for safely writing to the error log from multiple parallel tasks
        private static readonly object _logLock = new object();

        static async Task Main(string[] args)
        {
            string tags = "1girl";
            string apiKey = "";
            string userId = "";
            string saveDir = @"G:\GelBooru";
            double maxSizeGb = 100;
            int maxConcurrency = 16;
            int timeoutSeconds = 100; // Default timeout

            // Parse args
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
                }
            }

            long maxSizeBytes = (long)(maxSizeGb * 1024 * 1024 * 1024);
            Directory.CreateDirectory(saveDir);

            // Apply the custom timeout to the HttpClient
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://gelbooru.com/");

            _startTime = DateTime.Now;

            Console.WriteLine($"Target directory: {saveDir}");
            Console.WriteLine($"Size limit: {maxSizeGb} GB");
            Console.WriteLine($"Tags: {tags}");
            Console.WriteLine($"Concurrency: {maxConcurrency} simultaneous downloads");
            Console.WriteLine($"Timeout: {timeoutSeconds} seconds");
            Console.WriteLine($"Started at: {_startTime:yyyy-MM-dd HH:mm:ss}\n");

            using var cts = new CancellationTokenSource();

            try
            {
                await RunLockstepArchiverAsync(tags, apiKey, userId, saveDir, maxSizeBytes, maxConcurrency, cts);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n[STOPPED] Target size reached or operation cancelled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[FATAL ERROR] {ex.Message}");
            }

            double totalGb = (double)_totalDownloadedBytes / (1024 * 1024 * 1024);
            double elapsedMin = (DateTime.Now - _startTime).TotalMinutes;
            double avgRate = elapsedMin > 0 ? _filesProcessed / elapsedMin : 0;

            Console.WriteLine($"\nFinished! Processed {_filesProcessed} files. Total size: {totalGb:F4} GB");
            Console.WriteLine($"Average rate: {avgRate:F1} img/min over {elapsedMin:F1} minutes");
        }

        static async Task RunLockstepArchiverAsync(string tags, string apiKey, string userId, string saveDir, long maxSizeBytes, int maxConcurrency, CancellationTokenSource cts)
        {
            string formattedOriginalTags = tags.Trim().Replace(" ", "+");
            string currentSearchTags = formattedOriginalTags;
            int pid = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cts.Token
            };

            while (!cts.IsCancellationRequested)
            {
                string url = $"https://gelbooru.com/index.php?page=dapi&s=post&q=index&json=1&limit=100&pid={pid}&tags={currentSearchTags}";
                if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(userId))
                {
                    url += $"&api_key={apiKey}&user_id={userId}";
                }

                List<JsonElement> currentBatch = await FetchPageWithRetryAsync(url, pid, cts.Token);

                if (currentBatch == null || currentBatch.Count == 0)
                {
                    Console.WriteLine("\n[INFO] No more posts in current ID range. Finished.");
                    break;
                }

                await Parallel.ForEachAsync(currentBatch, parallelOptions, async (post, token) =>
                {
                    await ProcessAndDownloadPostAsync(post, saveDir, maxSizeBytes, cts);
                });

                if (currentBatch.Count < 100)
                {
                    Console.WriteLine("\n[INFO] Reached the end of the results.");
                    break;
                }

                if (pid >= 199)
                {
                    long minIdInBatch = GetMinIdFromBatch(currentBatch);

                    if (minIdInBatch != long.MaxValue)
                    {
                        currentSearchTags = $"{formattedOriginalTags}+id:<{minIdInBatch}";
                        pid = 0;
                        Console.WriteLine($"\n[INFO] Pagination limit reached. Switched to next ID range → id:<{minIdInBatch}");
                        Console.WriteLine($"       New search tags: {currentSearchTags}\n");
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    pid++;
                }
            }
        }

        static async Task<List<JsonElement>> FetchPageWithRetryAsync(string url, int pid, CancellationToken token)
        {
            int retries = 3;
            while (retries > 0 && !token.IsCancellationRequested)
            {
                try
                {
                    HttpResponseMessage response = await _httpClient.GetAsync(url, token);
                    string responseContent = await response.Content.ReadAsStringAsync(token);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[HTTP ERROR] pid={pid} | Status: {(int)response.StatusCode}");
                        retries--;
                        await Task.Delay(3000, token);
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
                    Console.WriteLine($"[FETCH ERROR] pid={pid}: {ex.Message}");
                    retries--;
                    await Task.Delay(3000, token);
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

        static async Task ProcessAndDownloadPostAsync(JsonElement post, string saveDir, long maxSizeBytes, CancellationTokenSource cts)
        {
            if (cts.IsCancellationRequested) return;

            if (!post.TryGetProperty("file_url", out JsonElement urlElement)) return;
            string imageUrl = urlElement.GetString();
            if (string.IsNullOrEmpty(imageUrl)) return;

            string filename = Path.GetFileName(new Uri(imageUrl).LocalPath);
            string baseName = Path.GetFileNameWithoutExtension(filename);

            string jsonFilepath = Path.Combine(saveDir, $"{baseName}.json");
            string finalFilepath = Path.Combine(saveDir, filename);

            // Skip logic: if both files exist, skip the download completely.
            if (File.Exists(finalFilepath) && File.Exists(jsonFilepath)) return;

            try
            {
                using HttpResponseMessage imgResponse = await _httpClient.GetAsync(imageUrl, cts.Token);
                if (!imgResponse.IsSuccessStatusCode) return;

                if (imgResponse.Content.Headers.ContentType?.MediaType?.Contains("text/html") == true) return;

                byte[] imageBytes = await imgResponse.Content.ReadAsByteArrayAsync(cts.Token);
                string jsonString = JsonSerializer.Serialize(post, new JsonSerializerOptions { WriteIndented = true });

                await File.WriteAllBytesAsync(finalFilepath, imageBytes, cts.Token);
                await File.WriteAllTextAsync(jsonFilepath, jsonString, cts.Token);

                long newSize = Interlocked.Add(ref _totalDownloadedBytes, imageBytes.Length + jsonString.Length);
                int currentCount = Interlocked.Increment(ref _filesProcessed);

                if (currentCount % 100 == 0)
                {
                    double elapsedMinutes = (DateTime.Now - _startTime).TotalMinutes;
                    double rate = elapsedMinutes > 0 ? currentCount / elapsedMinutes : 0;

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{currentCount} files] Downloaded: {(double)newSize / (1024 * 1024 * 1024):F4} GB | Rate: {rate:F1} img/min");
                }

                if (newSize >= maxSizeBytes)
                {
                    cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                string postId = post.TryGetProperty("id", out JsonElement idEl) ? idEl.ToString() : "UNKNOWN_ID";
                Console.WriteLine($"[DOWNLOAD ERROR] Post ID: {postId} | File: {filename}: {ex.Message}");
                LogFailedDownload(post, filename, saveDir, ex.Message);
            }
        }

        static void LogFailedDownload(JsonElement post, string filename, string saveDir, string errorMessage)
        {
            string postId = post.TryGetProperty("id", out JsonElement idEl) ? idEl.ToString() : "UNKNOWN_ID";
            string logFilePath = Path.Combine(saveDir, "failed_downloads.log");

            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Post ID: {postId} | File: {filename} | Error: {errorMessage}{Environment.NewLine}";

            // Lock ensures multiple parallel threads don't collide when writing to the text file
            lock (_logLock)
            {
                File.AppendAllText(logFilePath, logEntry);
            }
        }
    }
}