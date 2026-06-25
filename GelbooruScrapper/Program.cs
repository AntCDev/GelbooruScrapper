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

        static async Task Main(string[] args)
        {
            string tags = "1girl";
            string apiKey = "";
            string userId = "";
            string saveDir = @"G:\GelBooru";
            double maxSizeGb = 100;
            int maxConcurrency = 16;

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
                }
            }

            long maxSizeBytes = (long)(maxSizeGb * 1024 * 1024 * 1024);
            Directory.CreateDirectory(saveDir);

            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://gelbooru.com/");

            _startTime = DateTime.Now;

            Console.WriteLine($"Target directory: {saveDir}");
            Console.WriteLine($"Size limit: {maxSizeGb} GB");
            Console.WriteLine($"Tags: {tags}");
            Console.WriteLine($"Concurrency: {maxConcurrency} simultaneous downloads");
            Console.WriteLine($"Started at: {_startTime:yyyy-MM-dd HH:mm:ss}\n");

            using var cts = new CancellationTokenSource();

            try
            {
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrency,
                    CancellationToken = cts.Token
                };

                await Parallel.ForEachAsync(FetchPostsAsync(tags, apiKey, userId, cts.Token), options, async (post, token) =>
                {
                    await ProcessAndDownloadPostAsync(post, saveDir, maxSizeBytes, cts);
                });
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

        static async IAsyncEnumerable<JsonElement> FetchPostsAsync(string tags, string apiKey, string userId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            int pid = 0;
            string formattedTags = tags.Trim().Replace(" ", "+");
            string baseUrl = "https://gelbooru.com/index.php";

            while (!token.IsCancellationRequested)
            {
                string url = $"{baseUrl}?page=dapi&s=post&q=index&json=1&limit=100&pid={pid}&tags={formattedTags}";
                if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(userId))
                {
                    url += $"&api_key={apiKey}&user_id={userId}";
                }

                List<JsonElement> currentBatch = new List<JsonElement>();
                bool endOfPosts = false;
                string responseContent = null;

                try
                {
                    HttpResponseMessage response = await _httpClient.GetAsync(url, token);
                    responseContent = await response.Content.ReadAsStringAsync(token);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[HTTP ERROR] Page {pid}: {(int)response.StatusCode} ({response.StatusCode})");
                        string preview = responseContent?.Length > 0
                            ? responseContent.Substring(0, Math.Min(500, responseContent.Length)).Replace("\r\n", " ").Replace("\n", " ").Trim()
                            : "(empty response)";
                        Console.WriteLine($"   Preview: {preview}");
                        await Task.Delay(5000, token);
                        continue;
                    }

                    using JsonDocument doc = JsonDocument.Parse(responseContent);

                    if (!doc.RootElement.TryGetProperty("post", out JsonElement postsArray) || postsArray.GetArrayLength() == 0)
                    {
                        Console.WriteLine("\nReached the end of available posts.");
                        endOfPosts = true;
                    }
                    else
                    {
                        foreach (JsonElement post in postsArray.EnumerateArray())
                        {
                            currentBatch.Add(post.Clone());
                        }
                    }
                }
                catch (JsonException jex)
                {
                    Console.WriteLine($"[JSON PARSE ERROR] Page {pid}: {jex.Message}");
                    if (responseContent != null)
                    {
                        string preview = responseContent.Substring(0, Math.Min(300, responseContent.Length));
                        Console.WriteLine($"   Response started with: '{preview}'");
                    }
                    await Task.Delay(3000, token);
                    continue;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FETCH ERROR] Page {pid}: {ex.GetType().Name} - {ex.Message}");
                    await Task.Delay(3000, token);
                    continue;
                }

                if (endOfPosts)
                {
                    yield break;
                }

                foreach (JsonElement post in currentBatch)
                {
                    yield return post;
                }

                pid++;
            }
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

                if (currentCount % 50 == 0)
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
                Console.WriteLine($"[DOWNLOAD ERROR] {filename}: {ex.Message}");
            }
        }
    }
}