# Gelbooru Scrapper

A high-performance, asynchronous command-line tool built in C# for archiving images and metadata from Gelbooru. 

## Overview
This tool uses a producer-consumer pattern to maximize resource utilization and saturate high-speed internet connections. 
- **Producer:** Uses `IAsyncEnumerable` to safely paginate through the Gelbooru API sequentially, fetching metadata without spamming the endpoint.
- **Consumer:** Uses `Parallel.ForEachAsync` with a configurable concurrency limit to download images and write files (images + JSON metadata) to disk simultaneously. 
- **Thread Safety:** Implements `Interlocked` counters to track file sizes and counts across multiple threads without locking bottlenecks.
- **Async I/O:** Fully asynchronous network streams and disk writes prevent the CPU from sitting idle while waiting for disk operations.

## ⚠️ Concurrency Limits
**Do not set the concurrency limit (`-c`) too high**
If you open too many concurrent connections at once (e.g., 50-100+), your IP address will likely get temporarily or permanently banned. 
The default concurrency is **16**, which is generally safe enough to avoid triggering rate limits.

## Command Line Usage
The tool is designed to be run from the Windows Command Prompt (CMD) or PowerShell.

### Syntax
GelbooruScrapper.exe -t "TAGS" -k API_KEY -u USER_ID -d "DRIVE:\\PATH" -s MAX_SIZE_GB -c CONCURRENCY -to TIMEOUT_SECONDS

### Arguments

| Flag | Long Flag | Description | Default |
| --- | --- | --- | --- |
| `-t` | `--tags` | The search tags to archive (space-separated inside quotes). | `1girl` |
| `-k` | `--apikey` | Your Gelbooru API Key. | *None* |
| `-u` | `--userid` | Your Gelbooru User ID. | *None* |
| `-d` | `--dir` | The target directory to save images and JSON metadata. | `G:\\GelBooru` |
| `-s` | `--size` | Maximum total download size in Gigabytes (GB) before stopping. | `100` |
| `-c` | `--concurrency` | Number of simultaneous image downloads. | `16` |
| `-to` | `--timeout` | Seconds before a request timeouts. | `100` |
