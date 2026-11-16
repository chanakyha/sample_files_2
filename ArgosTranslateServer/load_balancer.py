"""Load balancer for the Argos Translate workers (Python standard library only).

Starts N worker processes (worker.py, Argos Translate) and distributes requests between them, so
large data sets are translated in parallel. Clients (the Translator app, Postman, ...) only talk
to the load balancer, which behaves like a single translation server.

    client ──> load balancer :5000 ──┬──> worker :5001  (worker.py, Argos Translate)
                                     ├──> worker :5002  (worker.py, Argos Translate)
                                     └──> other server (optional, "backends")

Why several workers: one worker translates one request at a time per language model and uses
its share of the CPU cores for it. Several workers keep the CPU busy with many requests at once.
Other servers running this folder ("backends") add capacity beyond one machine.

Features
- least-connections balancing (the healthy backend with the fewest active requests)
- health checks every few seconds; unhealthy backends get no traffic until they recover
- warm-up: each worker gets one detection request before any traffic
- failover: a request that fails because a backend is down, or that a backend answers with a
  server error (5xx, e.g. out of memory), is retried on another backend
- crashed local workers are restarted automatically
- runs on its own, independent of any client: it is started with start.cmd / start.ps1 and
  stopped with stop.cmd / Ctrl+C. Clients (the Translator app, Postman) only send requests to it
  and never start, stop or configure it.
- works offline: the workers only use the models installed by install_models.py

Settings: config.json in this folder (port, workers, languages, ...). Command-line options
override it for one run.

Test routes (handled by the load balancer itself, not forwarded):
- GET /lb/health    quick check: 200 when at least one backend is healthy, 503 otherwise
- GET /lb/status    every backend with its counters (active, served, errors, restarts)
- GET /lb/selftest  runs a real test on EVERY backend directly (languages, detection of a sample
                    per loaded language, one translation) and reports per backend what passed:
                    200 when all backends pass, 503 when any fails

Usage (normally via start.ps1):
    python load_balancer.py                       (settings from config.json)
    python load_balancer.py --port 5000 --workers 2 --languages en,fr,es
"""
import argparse
import http.client
import json
import os
import socket
import subprocess
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import common

HERE = common.HERE
WORKER_SCRIPT = os.path.join(HERE, "worker.py")
LOG_DIR = os.path.join(HERE, "logs")
CONFIG_FILE = common.CONFIG_FILE
DEFAULTS = common.DEFAULTS

# /lb/selftest: one sample per language; detection must return this language
SELFTEST_SAMPLES = {
    "en": "The technician checks the brakes before the car is delivered to the customer.",
    "fr": "Le technicien vérifie les freins avant que la voiture soit livrée au client.",
    "es": "El técnico revisa los frenos antes de que el coche sea entregado al cliente.",
    "de": "Der Techniker prüft die Bremsen, bevor das Auto an den Kunden geliefert wird.",
    "it": "Il tecnico controlla i freni prima che l'auto venga consegnata al cliente.",
    "pt": "O técnico verifica os freios antes de o carro ser entregue ao cliente.",
}
SELFTEST_TIMEOUT = 120       # seconds per request; a busy worker can be slow

HEALTH_INTERVAL = 3          # seconds between health checks
HEALTH_TIMEOUT = 15          # seconds; a busy worker answers slowly
HEALTH_FAILURES = 2          # failed checks in a row before a worker is marked DOWN
REQUEST_TIMEOUT = 600        # seconds; translating large batches on CPU can be slow
HOP_BY_HOP = {"connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
              "te", "trailers", "transfer-encoding", "upgrade", "host", "content-length"}


_log_lock = threading.Lock()
_log_file = None


def log(message):
    """Writes to the console (if there is one) and to --log-file.

    The load balancer runs on its own, independent of whoever started it: if the console or the
    pipe it was started with goes away, writing to it fails silently instead of stopping the server.
    """
    line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] [load-balancer] {message}"
    with _log_lock:
        try:
            if sys.stdout is not None:
                print(line, flush=True)
        except (OSError, ValueError):
            pass
        if _log_file is not None:
            try:
                _log_file.write(line + "\n")
                _log_file.flush()
            except (OSError, ValueError):
                pass


class ExclusiveHTTPServer(ThreadingHTTPServer):
    """HTTP server that refuses to share its port.

    Python's HTTPServer sets SO_REUSEADDR, which on Windows lets a second program bind a port that
    is already in use (two servers on :5000, requests going to either). Use the port exclusively
    instead, so a busy port fails fast with a clear error.
    """
    allow_reuse_address = False
    daemon_threads = True

    def server_bind(self):
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):  # Windows
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()

    def handle_error(self, request, client_address):
        """A client that closes its connection is normal, not an error.

        Clients keep connections open between requests (HTTP keep-alive). When a client exits
        (e.g. the Translator app is closed), Windows resets those connections and Python's server
        would print a traceback per connection (ConnectionResetError, WinError 10054). Ignore
        these; anything else is still reported.
        """
        error = sys.exc_info()[1]
        if isinstance(error, (ConnectionResetError, ConnectionAbortedError, BrokenPipeError, TimeoutError)):
            return
        log(f"error while handling a request from {client_address[0]}:{client_address[1]}: {error!r}")
        super().handle_error(request, client_address)


class WorkerJob:
    """Windows Job Object that kills all workers when the load balancer exits.

    On Windows, child processes keep running when their parent dies. Every worker (and the processes
    it starts) is put in this job; the job is closed by Windows when this process ends for ANY
    reason (normal stop, killed by the app, crash, Task Manager), which kills all workers too.
    """

    def __init__(self):
        self.handle = None
        if os.name != "nt":
            return
        import ctypes
        from ctypes import wintypes

        class IO_COUNTERS(ctypes.Structure):
            _fields_ = [(name, ctypes.c_ulonglong) for name in (
                "ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]

        class BASIC_LIMIT(ctypes.Structure):
            _fields_ = [("PerProcessUserTimeLimit", ctypes.c_int64), ("PerJobUserTimeLimit", ctypes.c_int64),
                        ("LimitFlags", wintypes.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                        ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wintypes.DWORD),
                        ("Affinity", ctypes.c_size_t), ("PriorityClass", wintypes.DWORD),
                        ("SchedulingClass", wintypes.DWORD)]

        class EXTENDED_LIMIT(ctypes.Structure):
            _fields_ = [("BasicLimitInformation", BASIC_LIMIT), ("IoInfo", IO_COUNTERS),
                        ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                        ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]

        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000
        JobObjectExtendedLimitInformation = 9

        self.kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel32.CreateJobObjectW.restype = wintypes.HANDLE
        self.kernel32.OpenProcess.restype = wintypes.HANDLE
        self.kernel32.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        self.handle = self.kernel32.CreateJobObjectW(None, None)
        info = EXTENDED_LIMIT()
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not self.kernel32.SetInformationJobObject(self.handle, JobObjectExtendedLimitInformation,
                                                     ctypes.addressof(info), ctypes.sizeof(info)):
            log(f"warning: could not configure job object (error {ctypes.get_last_error()})")
            self.handle = None
        else:
            # Put the load balancer itself in the job BEFORE starting workers: every process it creates
            # (workers and their own child processes) is then in the job from the moment it exists.
            self.kernel32.GetCurrentProcess.restype = wintypes.HANDLE
            self.kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
            if not self.kernel32.AssignProcessToJobObject(self.handle, self.kernel32.GetCurrentProcess()):
                log(f"warning: job object not available (error {ctypes.get_last_error()}); "
                    f"workers may keep running if the load balancer is killed")
                self.handle = None


def port_in_use(port):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(1)
        return s.connect_ex(("127.0.0.1", port)) == 0


class Backend:
    """One translation server (a local worker process or a remote server)."""

    def __init__(self, url, process_args=None, env=None, log_path=None):
        self.url = url.rstrip("/")
        parsed = urllib.parse.urlparse(self.url)
        self.host, self.port = parsed.hostname, parsed.port or (443 if parsed.scheme == "https" else 80)
        self.https = parsed.scheme == "https"
        self.process_args, self.env, self.log_path = process_args, env, log_path
        self.process = None
        self.healthy = False
        self.warmed = False
        self.failures = 0
        self.check_lock = threading.Lock()
        self.active = 0
        self.served = 0
        self.errors = 0
        self.restarts = 0
        self.lock = threading.Lock()

    @property
    def local(self):
        return self.process_args is not None

    def start(self):
        if not self.local:
            return
        self.warmed = False
        log_file = open(self.log_path, "a", encoding="utf-8")
        flags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
        self.process = subprocess.Popen(self.process_args, env=self.env, stdout=log_file,
                                        stderr=subprocess.STDOUT, cwd=HERE, creationflags=flags)
        log(f"started worker {self.url} (pid {self.process.pid}, log: {self.log_path})")

    def stop(self):
        if self.process and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(10)
            except subprocess.TimeoutExpired:
                self.process.kill()

    def connection(self, timeout):
        cls = http.client.HTTPSConnection if self.https else http.client.HTTPConnection
        return cls(self.host, self.port, timeout=timeout)

    def check_health(self):
        if not self.check_lock.acquire(blocking=False):
            return  # previous check (or warm-up) still running
        try:
            # A worker busy translating can answer slowly: generous timeout, and only mark it DOWN after
            # HEALTH_FAILURES failed checks in a row (a failed real request still marks it down at once)
            ok = self.request("GET", "/languages", timeout=HEALTH_TIMEOUT) == 200
            if ok and not self.warmed:
                ok = self.warm_up()
            self.failures = 0 if ok else self.failures + 1
            healthy = ok or (self.healthy and self.failures < HEALTH_FAILURES)
            if healthy != self.healthy:
                log(f"{self.url} is now {'HEALTHY' if healthy else 'DOWN'}")
            self.healthy = healthy
        finally:
            self.check_lock.release()

    def warm_up(self):
        """One detection request before the backend gets traffic (loads the detector, and checks
        that the backend really answers requests, not only /languages)."""
        body = json.dumps({"q": "Bonjour, ceci est un test de démarrage."}).encode("utf-8")
        status = self.request("POST", "/detect", body, {"Content-Type": "application/json"}, timeout=120)
        self.warmed = status == 200
        if self.warmed:
            log(f"{self.url} warmed up")
        return self.warmed

    def request(self, method, path, body=None, headers=None, timeout=5):
        try:
            conn = self.connection(timeout=timeout)
            conn.request(method, path, body=body, headers=headers or {})
            status = conn.getresponse().status
            conn.close()
            return status
        except OSError:
            return None

    def call_json(self, method, path, payload=None, timeout=SELFTEST_TIMEOUT):
        """Request with a JSON body; returns (status or None, parsed JSON or error text, milliseconds)."""
        started = time.perf_counter()
        try:
            conn = self.connection(timeout=timeout)
            body = json.dumps(payload).encode("utf-8") if payload is not None else None
            conn.request(method, path, body=body, headers={"Content-Type": "application/json"} if body else {})
            response = conn.getresponse()
            data = response.read()
            conn.close()
            try:
                parsed = json.loads(data.decode("utf-8"))
            except ValueError:
                parsed = data.decode("utf-8", "replace")[:200]
            return response.status, parsed, round((time.perf_counter() - started) * 1000)
        except OSError as error:
            return None, str(error), round((time.perf_counter() - started) * 1000)

    def selftest(self):
        """Real test of this backend, sent to it directly (not through the balancing):
        1. GET /languages
        2. POST /detect for one sample per loaded language: the sample's language must come first
        3. POST /translate for one sample: must return a non-empty text different from the input
        """
        checks = []

        def add(name, passed, ms, details):
            checks.append({"check": name, "passed": bool(passed), "ms": ms, "details": details})

        status, data, ms = self.call_json("GET", "/languages", timeout=15)
        codes = [l.get("code") for l in data] if status == 200 and isinstance(data, list) else []
        add("languages", bool(codes), ms, f"{len(codes)} loaded: {', '.join(codes)}" if codes
            else f"HTTP {status}: {data}" if status else f"no answer: {data}")

        if codes:
            for code, text in SELFTEST_SAMPLES.items():
                if code not in codes:
                    continue
                status, data, ms = self.call_json("POST", "/detect", dict(q=text))
                if status == 200 and isinstance(data, list) and data:
                    got, confidence = data[0].get("language"), data[0].get("confidence")
                    add(f"detect {code}", got == code, ms, f"detected {got} ({confidence}%)")
                else:
                    add(f"detect {code}", False, ms, f"HTTP {status}: {data}")

            source = "en" if "en" in codes else codes[0]
            target = next((c for c in ("fr", "es", "de") if c in codes and c != source),
                          next((c for c in codes if c != source), None))
            if target and source in SELFTEST_SAMPLES:
                text = SELFTEST_SAMPLES[source]
                status, data, ms = self.call_json("POST", "/translate",
                                                  dict(q=text, source=source, target=target, format="text"))
                translated = data.get("translatedText") if status == 200 and isinstance(data, dict) else None
                add(f"translate {source}->{target}", bool(translated) and translated != text, ms,
                    translated if translated else f"HTTP {status}: {data}")

        return {"url": self.url, "local": self.local, "healthy": self.healthy,
                "passed": bool(checks) and all(c["passed"] for c in checks), "checks": checks}

    def status(self):
        return {"url": self.url, "local": self.local, "healthy": self.healthy, "active": self.active,
                "served": self.served, "errors": self.errors, "restarts": self.restarts}


class Pool:
    def __init__(self, backends):
        self.backends = backends
        self.lock = threading.Lock()
        self.next_index = 0

    def healthy(self):
        return [b for b in self.backends if b.healthy]

    def acquire(self, exclude):
        """Least-connections: the healthy backend with the fewest active requests (round robin on ties)."""
        with self.lock:
            candidates = [b for b in self.backends if b.healthy and b not in exclude]
            if not candidates:
                return None
            self.next_index = (self.next_index + 1) % len(self.backends)
            rotated = sorted(candidates, key=lambda b: (b.active, (self.backends.index(b) - self.next_index) % len(self.backends)))
            backend = rotated[0]
            backend.active += 1
            return backend

    def acquire_possible(self, exclude):
        """True when another healthy backend (not in exclude) could take the request."""
        with self.lock:
            return any(b.healthy and b not in exclude for b in self.backends)

    def release(self, backend, ok):
        with self.lock:
            backend.active -= 1
            backend.served += 1 if ok else 0
            backend.errors += 0 if ok else 1


def monitor(pool, stop_event):
    """Health checks + restart of crashed local workers."""
    while not stop_event.is_set():
        for backend in pool.backends:
            if backend.local and backend.process and backend.process.poll() is not None:
                backend.healthy = False
                backend.restarts += 1
                log(f"worker {backend.url} exited (code {backend.process.returncode}), restarting")
                backend.start()
            threading.Thread(target=backend.check_health, daemon=True).start()
        stop_event.wait(HEALTH_INTERVAL)


def make_handler(pool):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):
            pass  # keep the console quiet; failures are logged explicitly

        def send_body(self, status, body, content_type="application/json", headers=()):
            self.send_response(status)
            for key, value in headers:
                if key.lower() not in HOP_BY_HOP and key.lower() != "content-type":
                    self.send_header(key, value)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            try:
                self.wfile.write(body)
            except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
                self.close_connection = True  # the client left before the answer (e.g. app closed or cancelled)

        def send_json(self, status, value):
            return self.send_body(status, json.dumps(value, indent=2, ensure_ascii=False).encode("utf-8"))

        def do_GET(self):
            parsed = urllib.parse.urlparse(self.path)
            route = parsed.path.rstrip("/")
            if route == "/lb/health":
                healthy, total = len(pool.healthy()), len(pool.backends)
                return self.send_json(200 if healthy else 503, {
                    "status": "ok" if healthy == total else "degraded" if healthy else "down",
                    "healthy": healthy, "total": total})
            if route == "/lb/status":
                return self.send_json(200, {"pid": os.getpid(), "started": STARTED,
                                            "backends": [b.status() for b in pool.backends],
                                            "healthy": len(pool.healthy()), "total": len(pool.backends)})
            if route == "/lb/selftest":
                return self.selftest()
            self.forward()

        def selftest(self):
            """Tests every backend at the same time and reports per backend which checks passed."""
            started = time.perf_counter()
            results = [None] * len(pool.backends)

            def run(i, backend):
                results[i] = backend.selftest()

            threads = [threading.Thread(target=run, args=(i, b), daemon=True) for i, b in enumerate(pool.backends)]
            for t in threads:
                t.start()
            for t in threads:
                t.join()

            passed = sum(1 for r in results if r["passed"])
            report = {"passed": passed == len(results) and passed > 0,
                      "backends_passed": passed, "backends_total": len(results),
                      "ms": round((time.perf_counter() - started) * 1000), "backends": results}
            log(f"selftest: {passed} of {len(results)} backend(s) passed")
            return self.send_json(200 if report["passed"] else 503, report)

        def do_POST(self):
            self.forward()

        def forward(self):
            length = int(self.headers.get("Content-Length") or 0)
            body = self.rfile.read(length) if length else None
            headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP_BY_HOP}
            tried = []

            while True:
                backend = pool.acquire(exclude=tried)
                if backend is None:
                    message = "No translation worker is available yet (starting or down)." if not tried \
                        else "All translation workers failed for this request."
                    return self.send_body(503, json.dumps({"error": message}).encode("utf-8"), headers=[("Retry-After", "5")])

                tried.append(backend)
                try:
                    conn = backend.connection(timeout=REQUEST_TIMEOUT)
                    conn.request(self.command, self.path, body=body, headers=headers)
                    response = conn.getresponse()
                    data = response.read()
                    conn.close()
                except OSError as error:
                    # Backend down or crashed mid-request: mark it unhealthy and try another one
                    pool.release(backend, ok=False)
                    backend.healthy = False
                    log(f"{backend.url} failed ({error}); retrying on another backend")
                    continue

                pool.release(backend, ok=response.status < 500)
                if response.status >= 500 and pool.acquire_possible(exclude=tried):
                    # Worker error (e.g. out of memory): translation and detection can safely be
                    # repeated, so try another worker before giving the error to the client
                    log(f"{backend.url} answered HTTP {response.status}; retrying on another backend")
                    continue
                return self.send_body(response.status, data,
                                      response.getheader("Content-Type", "application/json"),
                                      response.getheaders())

    return Handler


def build_backends(args):
    backends = []
    logical_cpus = os.cpu_count() or 2
    intra_threads = args.threads_per_worker or max(1, logical_cpus // max(1, args.workers))
    os.makedirs(LOG_DIR, exist_ok=True)

    port = args.worker_base_port - 1
    for i in range(args.workers):
        # Next free port (skip ports used by the load balancer or other programs)
        port += 1
        while port == args.port or port_in_use(port):
            log(f"port {port} is in use, trying {port + 1}")
            port += 1
        command = [sys.executable, "-u", WORKER_SCRIPT, "--port", str(port), "--languages", args.languages]
        env = dict(os.environ, ARGOS_INTRA_THREADS=str(intra_threads), ARGOS_INTER_THREADS="1",
                   PYTHONIOENCODING="utf-8")
        backends.append(Backend(f"http://127.0.0.1:{port}", command, env, os.path.join(LOG_DIR, f"worker-{port}.log")))

    for url in args.backends:
        backends.append(Backend(url))

    log(f"{args.workers} local worker(s) x {intra_threads} CPU thread(s) each "
        f"({logical_cpus} logical CPUs), {len(backends) - args.workers} remote backend(s)")
    return backends


def load_settings(argv=None):
    """Settings: built-in DEFAULTS < config.json < command-line options (only those given)."""
    parser = argparse.ArgumentParser(description="Load balancer for the Argos Translate workers. "
                                                 "Settings come from config.json; options override them.")
    parser.add_argument("--config", default=CONFIG_FILE, help="settings file (default: config.json in this folder)")
    parser.add_argument("--host")
    parser.add_argument("--port", type=int)
    parser.add_argument("--workers", type=int, help="local worker processes")
    parser.add_argument("--worker-base-port", type=int, help="first worker port (default: port + 1)")
    parser.add_argument("--languages", help='comma-separated codes, or "all"')
    parser.add_argument("--threads-per-worker", type=int, help="CPU threads per worker (0 = split evenly)")
    parser.add_argument("--backends", help="comma-separated URLs of other translation servers to include")
    parser.add_argument("--log-file", help='load balancer log file ("" = console only)')
    options = parser.parse_args(argv)

    settings = common.read_config(options.config)
    for key in DEFAULTS:
        value = getattr(options, key, None)
        if value is not None:
            settings[key] = value

    if isinstance(settings["backends"], str):
        settings["backends"] = [u.strip() for u in settings["backends"].split(",") if u.strip()]
    if isinstance(settings["languages"], list):
        settings["languages"] = ",".join(settings["languages"])
    settings["worker_base_port"] = settings["worker_base_port"] or settings["port"] + 1
    return argparse.Namespace(config=options.config, **settings)


STARTED = time.strftime("%Y-%m-%d %H:%M:%S")


def main():
    try:
        args = load_settings()
    except (ValueError, OSError) as error:
        log(f"ERROR: invalid settings: {error}")
        return 1

    global _log_file
    if args.log_file:
        path = args.log_file if os.path.isabs(args.log_file) else os.path.join(HERE, args.log_file)
        _log_file = open(path, "a", encoding="utf-8")
    log(f"settings ({args.config if os.path.exists(args.config) else 'defaults'}): port {args.port}, "
        f"{args.workers} worker(s), languages {args.languages}, {len(args.backends)} remote backend(s)")

    if args.workers > 0 and not os.path.isdir(os.path.join(common.argos_data_dir(), "packages")):
        log(f"No language models in {common.argos_data_dir()}. Run setup.cmd (or install_models.cmd) first.")
        return 1

    # Claim the port first: if something already runs there, stop before starting any workers
    pool = Pool([])
    try:
        server = ExclusiveHTTPServer((args.host, args.port), make_handler(pool))
    except OSError as error:
        log(f"ERROR: cannot listen on {args.host}:{args.port} ({error}). "
            f"Another program (maybe another translation server) is using this port. "
            f"Stop it or use a different -Port.")
        return 1

    job = WorkerJob()  # keep a reference: closing the job handle (at exit) kills the workers
    pool.backends = build_backends(args)
    if not pool.backends:
        log("No backends configured (use --workers and/or --backends).")
        server.server_close()
        return 1

    for backend in pool.backends:
        backend.start()

    stop_event = threading.Event()
    threading.Thread(target=monitor, args=(pool, stop_event), daemon=True).start()

    base = f"http://{args.host}:{args.port}"
    log(f"listening on {base}  (test routes: {base}/lb/health  {base}/lb/status  {base}/lb/selftest)")
    log("workers are starting (loading Argos Translate takes a few seconds)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        log("stopping workers...")
        stop_event.set()
        server.server_close()
        for backend in pool.backends:
            backend.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
