# Gelbooru Archiver

A high-performance, asynchronous command-line tool built in C# for archiving images, videos, and metadata from Gelbooru.

## Overview

This tool uses an ID-based cursor strategy to paginate through the Gelbooru API without hitting duplicate or skipped results.

- **Cursor Pagination:** Rather than using page numbers (which can drift as new posts are added), the archiver tracks the lowest post ID seen in each batch and appends `id:<minId` to the next query. This ensures no posts are skipped or double-fetched across runs.
- **Parallel Downloads:** Uses `Parallel.ForEachAsync` with a configurable concurrency limit to download files and write metadata (image + JSON sidecar) to disk simultaneously.
- **Thread Safety:** Uses `Interlocked` counters to track downloaded file sizes and counts across threads without lock contention.
- **Async I/O:** Fully asynchronous network and disk operations keep threads from sitting idle.
- **Download Tracker:** A `download_tracker.json` file is maintained in your save directory. On subsequent runs, any post ID already in the tracker is skipped automatically — safe to stop and resume at any time.
- **Failure Logging:** Any post that exhausts all retry attempts is recorded in `failed_downloads.log` in your save directory for later review.

## ⚠️ Concurrency & NotFound Warnings

**Do not set the concurrency limit (`-c`) too high.**

If you open too many concurrent connections at once, your IP may get temporarily rate-limited or banned. The default concurrency is **4**, which is a safe conservative baseline.

If you suddenly see a flood of log lines like:

```
[WARNING] Post 123456 attempt 1 failed: NotFound
```

this is typically the server being overwhelmed by too many simultaneous requests, not the posts actually being missing. To address this:

- Reduce concurrency with `-c` (e.g., `-c 2`)
- Or simply re-run the script — the download tracker will skip already-completed posts and retry the ones that failed.

## Command Line Usage

The tool is designed to be run from the Windows Command Prompt (CMD) or PowerShell.

### Syntax

```
GelbooruArchiver.exe -t "TAGS" -k API_KEY -u USER_ID -d "DRIVE:\\PATH" -s MAX_SIZE_GB -c CONCURRENCY -to TIMEOUT_SECONDS -m MEDIA_MODE -r MAX_RETRIES -dm DELAY_MS
```

### Arguments

| Flag | Long Flag | Description | Default |
| --- | --- | --- | --- |
| `-t` | `--tags` | The search tags to archive (space-separated inside quotes). | `1girl` |
| `-k` | `--apikey` | Your Gelbooru API Key. | *None* |
| `-u` | `--userid` | Your Gelbooru User ID. | *None* |
| `-d` | `--dir` | The target directory to save files and JSON metadata. | `G:\\GelBooru` |
| `-s` | `--size` | Maximum total download size in Gigabytes (GB) before stopping. | `100` |
| `-c` | `--concurrency` | Number of simultaneous downloads. | `4` |
| `-to` | `--timeout` | Seconds before a request times out. | `100` |
| `-m` | `--media` | Media filter mode: `0` = images only, `1` = all media, `2` = video/GIF only. | `0` |
| `-r` | `--retries` | Maximum retry attempts per file before logging as failed. | `3` |
| `-dm` | `--delay` | Milliseconds to wait between retry attempts. | `500` |

### Media Mode Details

| Value | Behavior |
| --- | --- |
| `0` | Downloads images only — skips `.mp4`, `.webm`, and `.gif` |
| `1` | Downloads everything |
| `2` | Downloads `.mp4`, `.webm`, and `.gif` only — skips standard images |

## Linux Usage

The Linux binary has no file extension (named `GelbooruArchiver_linux-x64`). Before running it for the first time, grant it execution permissions.

1. Open your terminal and navigate to the folder containing the downloaded file.
2. Make it executable:
```bash
chmod +x GelbooruArchiver_linux-x64
```
3. Run it using `./` with **forward slashes** for paths:
```bash
./GelbooruArchiver_linux-x64 -t "1girl" -d "/home/username/GelBooru" -s 100
```

## macOS Usage

Like Linux, the macOS binary has no file extension and runs via Terminal.

1. Navigate to the folder containing the binary.
2. Grant execution permissions:
```bash
chmod +x GelbooruArchiver_osx-arm64
```
3. **Bypass Gatekeeper:** Because this binary isn't signed with an Apple Developer certificate, macOS will likely block it. Strip the quarantine flag with:
```bash
xattr -d com.apple.quarantine GelbooruArchiver_osx-arm64
```
4. Run it with **forward slashes** for paths:
```bash
./GelbooruArchiver_osx-arm64 -t "1girl" -d "/Users/username/GelBooru" -s 100
```
