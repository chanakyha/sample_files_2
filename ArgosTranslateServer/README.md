# Argos Translate server with load balancer (Python, no Docker)

A local, self-hosted translation service built on
[Argos Translate](https://github.com/argosopentech/argos-translate): a **load balancer** in front
of several **workers** (`worker.py`). Free, open source, no usage limits, and it works **offline**:
once the models are installed, the text never leaves this PC and no internet access is needed.

```
clients ──> load_balancer.py :5000 ──┬──> worker.py :5001 (Argos Translate + Lingua)
(Translator app,                     ├──> worker.py :5002 (Argos Translate + Lingua)
 Postman, scripts)                   └──> other servers ("backends", optional)
```

**It runs separately from the Translator app.** You start, stop and configure it here; the app
(and any other client) only sends requests to its address.

## Files

| File | Purpose |
|---|---|
| `config.json` | **Server settings**: port, workers, languages, proxy, ... |
| `check_network.py` / `.cmd` | **Checks whether this network can download** what setup needs (run it first) |
| `setup.ps1` / `setup.cmd` | One-time install: Python packages into `.venv`, then the language models |
| `install_models.py` / `.cmd` | Downloads (or lists / removes) the language models into `models\` |
| `start.ps1` / `start.cmd` | Starts the load balancer + workers |
| `stop.ps1` / `stop.cmd` | Stops the load balancer + workers, however they were started |
| `load_balancer.py` | The load balancer (Python standard library only) |
| `worker.py` | One translation worker: Argos Translate + Lingua language detection |
| `common.py` | Shared settings code (config.json, models folder, proxy) |
| `check_server.py` | Route test: every route, PASS/FAIL per test, exit code 0/1 |
| `requirements.txt` | Pinned packages: `argostranslate`, `lingua-language-detector` |
| `models\`, `.venv\`, `server.log`, `logs\` | Created by setup / at runtime (not in git) |

## 1. Check the network (corporate Wi-Fi)

Setup downloads about 1.5 GB from these sites:

| Site | What | When |
|---|---|---|
| `pypi.org`, `files.pythonhosted.org` | Python packages (Argos Translate, PyTorch, Lingua) | setup |
| `raw.githubusercontent.com` | list of language models | setup / install_models |
| `argos-net.com` | translation models (about 100 MB per language pair) | setup / install_models |
| `github.com` → `release-assets.githubusercontent.com` | sentence splitting models (MiniSBD) | setup / install_models |

Double-click **`check_network.cmd`** on the network you want to use. It needs no installation and
checks every site step by step:

```
argos-net.com  -  translation models
   DNS            OK    104.21.18.140, 172.67.182.79
   direct         OK    HTTP 206, 4096 bytes in 408 ms
...
Everything can be downloaded directly. Run setup.cmd.
```

- **DNS FAIL / "getaddrinfo failed"**: the network cannot look up the name. Corporate networks
  usually only allow the internet through a proxy. The check also tries every proxy it finds
  (Windows settings, `HTTP(S)_PROXY`, `config.json`); to try one yourself:
  `check_network.cmd --proxy http://proxy.company.com:8080`. If it works, set it in `config.json`
  (`"proxy": "http://proxy.company.com:8080"`); setup and install_models use it.
- **HTTP 407**: the proxy needs a login (ask IT).
- **SSL error**: a company proxy inspects HTTPS traffic (ask IT).
- **"got something else than the file"**: a block page instead of the file (ask IT to allow the site).
- **Blocked and no proxy helps**: run setup on another network (e.g. at home) and copy the folder
  (with `.venv` and `models`) to the PC. After setup the server needs no internet at all.

## 2. Install (once)

Needs **Python 3.10 - 3.13**. If you don't have it: `winget install -e --id Python.Python.3.12 --scope user`

Then double-click **`setup.cmd`** (or `powershell -ExecutionPolicy Bypass -File setup.ps1`, with
`-Proxy http://proxy:port` to override `config.json`). It installs the packages into `.venv`
(about 1 GB, including PyTorch, which Argos Translate depends on) and downloads the models for the
languages in `config.json` into `models\` (about 2-3 minutes for en, fr, es, de, it, pt), then
translates "Good morning" into every language as a check.

Models later:

```powershell
install_models.cmd                       # the languages in config.json (only downloads what is missing)
install_models.cmd --languages en,nl,pl  # other languages
install_models.cmd --list                # what is installed (offline)
install_models.cmd --remove              # delete all models
```

For every language, the models from and to English are installed (plus direct pairs between
listed languages where they exist); other pairs are translated through English.

## 3. Configure: `config.json`

| Setting | Meaning |
|---|---|
| `host` | `127.0.0.1` = only this PC; `0.0.0.0` = other PCs too (allow the port in the firewall) |
| `port` | Port of the load balancer. Clients use `http://host:port` |
| `workers` | Worker processes (about 1.2 GB RAM each). `0` = only remote backends |
| `worker_base_port` | First worker port; `0` = `port + 1` (busy ports are skipped) |
| `languages` | Codes the workers offer, or `"all"` (only installed models can be used) |
| `threads_per_worker` | CPU threads per worker; `0` = split the cores evenly |
| `backends` | Other servers to include, e.g. `["http://server2:5000"]` |
| `proxy` | Proxy for **downloads** (setup, install_models); the server itself works offline |
| `log_file` | Load balancer log, relative to this folder; `""` = console only |

Unknown keys are rejected at start (catches typos). Restart after a change: `stop.cmd`, `start.cmd`.

## 4. Start and stop

Double-click **`start.cmd`** (settings from `config.json`; ready in a few seconds), or override
settings for one run:

```powershell
powershell -ExecutionPolicy Bypass -File start.ps1 -Workers 4               # more workers
powershell -ExecutionPolicy Bypass -File start.ps1 -BindHost 0.0.0.0        # allow other PCs
powershell -ExecutionPolicy Bypass -File start.ps1 -Backends "http://server2:5000,http://server3:5000"
```

**Stop:** Ctrl+C in its window, or `stop.cmd` from anywhere. The workers always stop with it.

## 5. API and test routes

Translation routes (forwarded to a worker):

| Route | Body | Answer |
|---|---|---|
| `GET /languages` | | `[{"code": "en", "name": "English", "targets": [...]}, ...]` |
| `POST /detect` | `{"q": "Gracias"}` | `[{"language": "es", "confidence": 70.4}, {"language": "pt", ...}]` best first, 0-100 |
| `POST /translate` | `{"q": "text" or ["a", "b"], "source": "en" or "auto", "target": "fr"}` | `{"translatedText": ..., "detectedLanguage": ...}` (`detectedLanguage` only with `auto`) |

With `"source": "auto"` every text is detected on its own, so one batch may mix languages.
Errors: HTTP 400 `{"error": "..."}` (e.g. missing `q`, unknown language), 500 if translating fails.

Test routes (answered by the load balancer itself):

| Route | Answer |
|---|---|
| `GET /lb/health` | `200 {"status": "ok", "healthy": 2, "total": 2}`; `503` when no worker is healthy |
| `GET /lb/status` | Every backend: healthy, active, served, errors, restarts; plus `pid` and `started` |
| `GET /lb/selftest` | Tests **every worker directly**: languages, detection of one sample per language, one translation. `200` = all passed, `503` + details when any fails |

Ways to test:

1. **Browser:** `http://127.0.0.1:5000/lb/selftest`
2. **Command line:** `.venv\Scripts\python check_server.py` (13 tests, exit code 0 = all passed)
3. **Postman:** import `..\postman\Translator.postman_collection.json`
4. **PowerShell:**
   ```powershell
   Invoke-RestMethod http://127.0.0.1:5000/translate -Method Post -ContentType "application/json" `
     -Body '{"q":"Where is the workshop?","source":"auto","target":"fr"}'
   ```
5. **Translator app:** **Check Connection**

## How it works

- **Translation:** Argos Translate (OpenNMT models run with CTranslate2 on the CPU). Sentences are
  split with MiniSBD (small ONNX models, downloaded by setup), so translating never goes online.
- **Language detection:** Lingua, limited to the installed languages. It is accurate on sentences
  and honest on single words: "Ciao" gives Italian 38% with Portuguese 32% next. The Translator
  app double-checks short, unsure texts with a round trip (candidate → English → back).
- **Load balancer:** least connections; health checks every 3 s (down after 2 failures); warm-up
  before traffic; failover on connection errors and HTTP 5xx; crashed workers are restarted;
  exclusive port; a Windows job object stops all workers with it.

Measured on a laptop (4 cores / 8 threads, 16 GB) with 2 workers: about 5 texts/s through the
Translator app (200 sentences in 37 s) and 20-30 short requests/s directly.

### Choosing the number of workers

Each worker gets `logical CPUs / workers` threads and needs about 1.2 GB of RAM.

- Laptop (4 cores / 8 threads, 16 GB): 2 workers
- Server (16 cores / 32 threads, 64 GB): 4-8 workers
- More than that on one machine does not help: add machines with `backends`.

## Notes

- Licences: Argos Translate MIT, models CC0 / per model (see `models\argos-translate\packages\*\README.md`),
  Lingua Apache-2.0, PyTorch BSD.
- Quality is below commercial services, especially for idioms and technical terms; have important
  documents reviewed.
- To share one server with colleagues: `"host": "0.0.0.0"` in config.json, allow the port in the
  firewall, and set that address in each app's Settings.
