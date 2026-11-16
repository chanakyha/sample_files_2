# Translator (.NET 5 Windows Forms + Argos Translate)

A Windows desktop translation app built with standard WinForms controls. It detects the language
of each text automatically and translates it into one of 24 languages using a self-hosted
**Argos Translate** server: free, open source, no usage limits, works offline, and the text never
leaves your network. A **load balancer** runs several workers in parallel for large data sets.

```
Translator app ──> load balancer :5000 ──┬──> worker :5001 (Argos Translate + Lingua)
 (batches in parallel)                   ├──> worker :5002 (Argos Translate + Lingua)
                                         └──> other servers (optional)
```

## Features

- Table of words, sentences and paragraphs. Hover over a row to see the original text and
  "From → To" languages.
- **File**: import large data sets (TXT: one text per line; CSV: column `text` or the first column),
  export results to CSV, clear items, clear the translation cache, reconnect to the server.
- **Translate** (opens on hover): 24 target languages; languages without models on the server are greyed out.
- **Settings**: server address and parallel requests (client settings only).
- **Check Connection**: server, load balancer health, a self-test of every worker, and a test translation.
- **Show**: switch the table between translated and original text.
- Progress bar with **Cancel**; partial results are kept.

## Quick start

1. Install Python 3.12 if needed: `winget install -e --id Python.Python.3.12 --scope user`
2. On a corporate network: double-click `ArgosTranslateServer\check_network.cmd` first. It shows
   whether the download sites are reachable, and which proxy to put in `config.json` if needed.
3. Double-click `ArgosTranslateServer\setup.cmd` once: installs Argos Translate into
   `ArgosTranslateServer\.venv` and downloads the language models into `ArgosTranslateServer\models`.
4. Start the server: double-click `ArgosTranslateServer\start.cmd` (settings in `config.json`).
5. Run the app:
   ```
   cd Translator
   dotnet run
   ```

The server and the app are **separate**: the app only sends requests to the server address in
its Settings. It never starts, stops or configures the server, and closing the app has no effect
on it. Stop the server with `ArgosTranslateServer\stop.cmd`. If the server is not running when the
app starts, the status bar says so; start it and use **File > Reconnect to Server**.

After setup everything works offline. The `ArgosTranslateServer` folder (with `.venv` and
`models`) can be copied to a PC that cannot download.

The project targets `net5.0-windows`. `RollForward=Major` is set so it also runs on a newer
Windows Desktop runtime if the .NET 5 runtime is not installed.

See [ArgosTranslateServer/README.md](ArgosTranslateServer/README.md) for the network check,
settings, API, test routes, more languages, sharing it on the network, and adding more machines.

## Scaling for large data sets

| Layer | What it does |
|---|---|
| **Batching** | 25 texts / 2,000 characters per request |
| **Parallel batches** | The app asks the load balancer how many workers are healthy (`/lb/status`) and sends 2 requests per worker at the same time |
| **Load balancer** | Sends each request to the healthy worker with the fewest active requests; health checks, automatic failover to another worker (also on HTTP 5xx), restarts crashed workers |
| **Workers** | Separate Argos Translate processes, each with an equal share of the CPU cores. Set `workers` in `ArgosTranslateServer\config.json` (about 1.2 GB RAM each) |
| **More machines** | Run the server folder on other machines and list them in `backends` in `config.json` |
| **Cache** | `%LocalAppData%\Translator\translation-cache-argos.json`: a text is never translated twice for the same language |
| **Retries** | Busy or failing servers (HTTP 429/5xx) are retried with increasing waits |

Measured on a laptop (4 cores, 16 GB) with 2 workers: 200 sentences in 37 s (about 5 texts/s).
Rule of thumb for workers: logical CPUs ÷ 4. More workers than CPU cores does not help on one
machine; add machines instead.

## Language detection

Each text is detected separately (`/detect`, Lingua), then texts are translated in batches grouped
by language. Single words are ambiguous for any detector ("Merci", "Hello", "Car"), so short texts
(up to 3 words) with a confidence below 90% are double-checked: for each candidate language
(the detector's ranking first) the text is translated to English and back (English is checked via
French). The language that gives back the original text wins. The tooltip shows when a language
was corrected, or when it stayed uncertain.

## Testing

| Tool | How |
|---|---|
| Network | `ArgosTranslateServer\check_network.cmd`: can this network download setup's files? |
| Browser | `http://127.0.0.1:5000/lb/selftest`: tests every worker, `200` = all passed |
| Command line | `ArgosTranslateServer\.venv\Scripts\python ArgosTranslateServer\check_server.py`: every route, PASS/FAIL, exit code 0/1 |
| Postman | Import `postman/Translator.postman_collection.json` (+ `translation-data.csv` for the data-driven folder) |
| App | **Check Connection** |

## Licences

Argos Translate: MIT. Lingua: Apache-2.0. Language models: see the README in each model folder
(`ArgosTranslateServer\models\argos-translate\packages\*`). Check with your legal/IT team before
distributing.
