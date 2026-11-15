# Translator (.NET 5 Windows Forms + LibreTranslate)

A Windows desktop translation app built with standard WinForms controls. It detects the language
of each text automatically and translates it into one of 24 languages using a self-hosted
**LibreTranslate** service: free, open source, no usage limits, and the text never leaves your
network. A **load balancer** runs several LibreTranslate workers in parallel for large data sets.

```
Translator app ──> load balancer :5000 ──┬──> LibreTranslate worker :5001
 (batches in parallel)                   ├──> LibreTranslate worker :5002
                                         └──> other servers (optional)
```

## Features

- Table of words, sentences and paragraphs. Hover over a row to see the original text and
  "From → To" languages.
- **File**: import large data sets (TXT: one text per line; CSV: column `text` or the first column),
  export results to CSV, clear items, clear the translation cache, reconnect to the server.
- **Translate** (opens on hover): 24 target languages; languages not loaded on the server are greyed out.
- **Settings**: server address, API key, parallel requests (client settings only).
- **Check Connection**: server, load balancer health, a self-test of every worker, and a test translation.
- **Show**: switch the table between translated and original text.
- Progress bar with **Cancel**; partial results are kept.

## Quick start

1. Install Python 3.12 if needed: `winget install -e --id Python.Python.3.12 --scope user`
2. Run `LibreTranslateServer\setup.cmd` once (installs LibreTranslate into `LibreTranslateServer\.venv`).
3. Start the server: double-click `LibreTranslateServer\start.cmd` (settings in
   `LibreTranslateServer\config.json`; the first start downloads the language models, a few minutes).
4. Run the app:
   ```
   cd Translator
   dotnet run
   ```

The server and the app are **separate**: the app only sends requests to the server address in
its Settings. It never starts, stops or configures the server, and closing the app has no effect
on it. Stop the server with `LibreTranslateServer\stop.cmd`. If the server is not running when the
app starts, the status bar says so; start it and use **File > Reconnect to Server**.

The project targets `net5.0-windows`. `RollForward=Major` is set so it also runs on a newer
Windows Desktop runtime if the .NET 5 runtime is not installed.

See [LibreTranslateServer/README.md](LibreTranslateServer/README.md) for its settings,
the test routes, more languages, sharing it on the network, and adding more machines.

## Scaling for large data sets

| Layer | What it does |
|---|---|
| **Batching** | 25 texts / 2,000 characters per request |
| **Parallel batches** | The app asks the load balancer how many workers are healthy (`/lb/status`) and sends 2 requests per worker at the same time |
| **Load balancer** | Sends each request to the healthy worker with the fewest active requests; health checks, automatic failover to another worker (also on HTTP 5xx), restarts crashed workers |
| **Workers** | Separate LibreTranslate processes, each with an equal share of the CPU cores. Set `workers` in `LibreTranslateServer\config.json` (about 1 GB RAM each) |
| **More machines** | Run LibreTranslate on other servers and list them in `backends` in `config.json` |
| **Cache** | `%LocalAppData%\Translator\translation-cache-libretranslate.json`: a text is never translated twice for the same language |
| **Retries** | Busy or failing servers (HTTP 429/5xx) are retried with increasing waits |

Rule of thumb for workers: logical CPUs ÷ 4 (e.g. 16 logical CPUs → 4 workers). More workers than
CPU cores does not help on one machine; add machines instead. A GPU server is much faster still.

## Language detection

Each text is detected separately (`/detect`), then texts are translated in batches grouped by
language. (LibreTranslate's own `auto` mode uses one language for a whole batch.)
Short words that are detected as English with low confidence ("Ciao") are checked with a round
trip (text → English → back) per likely language; the tooltip shows when a language was corrected.

## Testing

| Tool | How |
|---|---|
| Browser | `http://127.0.0.1:5000/lb/selftest`: tests every worker, `200` = all passed |
| Command line | `LibreTranslateServer\.venv\Scripts\python LibreTranslateServer\check_server.py`: every route, PASS/FAIL, exit code 0/1 |
| Postman | Import `postman/Translator.postman_collection.json` (+ `translation-data.csv` for the data-driven folder) |
| App | **Check Connection** |

## Licence note

LibreTranslate is licensed AGPL-3.0. Internal use is fine; if you modify it and offer it to
users outside the company, the changes must be published. Check with your legal/IT team.
