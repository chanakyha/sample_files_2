# LibreTranslate server with load balancer (Python, no Docker)

A local, self-hosted [LibreTranslate](https://github.com/LibreTranslate/LibreTranslate) service:
a **load balancer** in front of several LibreTranslate **workers**. Free, no usage limits, and
the text never leaves this PC.

```
clients ──> load_balancer.py :5000 ──┬──> worker :5001 (LibreTranslate)
(Translator app,                     ├──> worker :5002 (LibreTranslate)
 Postman, scripts)                   └──> remote servers ("backends", optional)
```

**It runs separately from the Translator app.** You start, stop and configure it here; the app
(and any other client) only sends requests to its address. Closing the app has no effect on it.

## Files

| File | Purpose |
|---|---|
| `config.json` | **Server settings**: port, workers, languages, remote backends, ... |
| `requirements.txt` | Pinned LibreTranslate version |
| `setup.ps1` / `setup.cmd` | One-time install into `.venv` in this folder |
| `start.ps1` / `start.cmd` | Starts the load balancer + workers |
| `stop.ps1` / `stop.cmd` | Stops the load balancer + workers, however they were started |
| `load_balancer.py` | The load balancer (Python standard library only) |
| `check_server.py` | Route test: every route, PASS/FAIL per test, exit code 0/1 |
| `server.log`, `logs/` | Load balancer log / per-worker logs (created at runtime) |

## 1. Install (once)

Needs **Python 3.10 - 3.13** (LibreTranslate does not support 3.14 yet). If you don't have it:

```powershell
winget install -e --id Python.Python.3.12 --scope user
```

Then double-click `setup.cmd`, or `powershell -ExecutionPolicy Bypass -File setup.ps1`.
Behind a corporate proxy: `setup.ps1 -Proxy http://proxy.company.com:8080`

## 2. Configure: `config.json`

```json
{
  "host": "127.0.0.1",
  "port": 5000,
  "workers": 3,
  "worker_base_port": 0,
  "languages": "en,fr,es,de,it,pt",
  "threads_per_worker": 0,
  "backends": [],
  "api_keys": false,
  "log_file": "server.log"
}
```

| Setting | Meaning |
|---|---|
| `host` | `127.0.0.1` = only this PC; `0.0.0.0` = other PCs too (allow the port in the firewall) |
| `port` | Port of the load balancer. Clients use `http://host:port` |
| `workers` | Local LibreTranslate processes (about 1 GB RAM each). `0` = only remote backends |
| `worker_base_port` | First worker port; `0` = `port + 1` (busy ports are skipped) |
| `languages` | Codes to load, or `"all"` (several GB). The first start downloads the models |
| `threads_per_worker` | CPU threads per worker; `0` = split the cores evenly |
| `backends` | Other LibreTranslate servers to include, e.g. `["http://server2:5000"]` |
| `api_keys` | Start workers with `--api-keys` (manage keys with `.venv\Scripts\ltmanage.exe`) |
| `log_file` | Load balancer log, relative to this folder; `""` = console only |

Unknown keys are rejected at start, so a typo does not go unnoticed. Restart the server after a
change (`stop.cmd`, then `start.cmd`).

## 3. Start and stop

Double-click `start.cmd` (settings from `config.json`), or override settings for one run:

```powershell
powershell -ExecutionPolicy Bypass -File start.ps1                          # config.json
powershell -ExecutionPolicy Bypass -File start.ps1 -Workers 4               # more workers
powershell -ExecutionPolicy Bypass -File start.ps1 -Languages all           # all languages
powershell -ExecutionPolicy Bypass -File start.ps1 -BindHost 0.0.0.0        # allow other PCs
powershell -ExecutionPolicy Bypass -File start.ps1 -Backends "http://server2:5000,http://server3:5000"
```

**Stop:** Ctrl+C in its window, or `stop.cmd` from anywhere (`stop.ps1 -Port 5001` for another
port). The workers always stop together with the load balancer, even when it is killed.

The **first start downloads the language models** (a few minutes). Later starts load them from
disk (seconds to half a minute) and work offline.

## 4. Test routes

The load balancer answers these routes itself (they are not forwarded to a worker):

| Route | Answer | Use |
|---|---|---|
| `GET /lb/health` | `200 {"status": "ok", "healthy": 3, "total": 3}`; `503` when no backend is healthy. `status` is `ok`, `degraded` or `down` | Quick liveness check for monitoring |
| `GET /lb/status` | Every backend: `healthy`, `active`, `served`, `errors`, `restarts`; plus `pid` and `started` | Watch the load spreading; the app reads it to choose its parallelism |
| `GET /lb/selftest` | Tests **every backend directly**: `/languages`, `/detect` of one sample per loaded language (must detect that language), one `/translate`. `200` when all pass, `503` when any fails; the body lists each check with result and time. Add `?api_key=KEY` with `api_keys` on | "Is every worker really working?" |

Everything else (`/languages`, `/detect`, `/translate`, ...) is forwarded to a worker, so the load
balancer behaves like one LibreTranslate server.

**Three ways to test:**

1. **Browser:** open `http://127.0.0.1:5000/lb/selftest`.
2. **Command line**, all routes with PASS/FAIL and an exit code (0 = all passed), usable in scripts:
   ```powershell
   .venv\Scripts\python check_server.py                        # http://127.0.0.1:5000
   .venv\Scripts\python check_server.py http://server2:5000 --api-key KEY
   ```
   It tests the three `/lb` routes, `/languages`, `/detect`, `/translate` (single, batch, mixed
   languages, same source and target), error answers (missing text, unknown language), and
   40 parallel requests, checking that they are spread over the workers.
3. **Postman:** import `..\postman\Translator.postman_collection.json` (folder 1 = test routes).
4. **Translator app:** **Check Connection** shows `/lb/health`, `/lb/selftest` with one row per worker,
   and a translation through the app's own code.

## How the load balancer works

- **Least connections:** each request goes to the healthy backend with the fewest active requests.
- **Health checks** every 3 seconds (`GET /languages`); a backend that is down or still loading gets
  no requests until it recovers (marked down after 2 failed checks in a row).
- **Warm-up:** each worker gets one detection request before any traffic (LibreTranslate's detector
  gives wrong results when its very first requests arrive in parallel).
- **Failover:** if a backend fails during a request, or answers with a server error (HTTP 5xx, e.g.
  out of memory), the request is retried on another backend.
- **Self-healing:** crashed local workers are restarted automatically.
- **Exclusive port:** a second server cannot share the port; it stops with a clear error instead.

### Choosing the number of workers

One LibreTranslate process translates one request at a time per language model, using its share
of the CPU. Each worker gets `logical CPUs / workers` threads and needs about 1 GB of RAM.

- Laptop (4 cores / 8 threads, 16 GB): 2-3 workers. Watch the free RAM: workers that run out of
  memory answer HTTP 500 (`mkl_malloc: failed to allocate memory` in `logs\worker-*.log`).
- Server (16 cores / 32 threads, 64 GB): 4-8 workers.
- More than that on one machine does not help: add machines with `backends` or use a GPU server.

## Notes

- Licence: AGPL-3.0. Internal use is fine; if you modify LibreTranslate and offer it to users
  outside the company, the changes must be published.
- Quality is below commercial services, especially for idioms and technical terms
  (e.g. "timing belt"); have important documents reviewed.
- To share one service with colleagues, run it on a server with `"host": "0.0.0.0"`, allow the
  port in the firewall, and set that address in each app's Settings.
