"""Route test for the LibreTranslate load balancer (standard library only).

Usage:
    .venv\\Scripts\\python check_server.py [http://127.0.0.1:5000] [--api-key KEY]

Tests every route a client uses, through the load balancer, and prints PASS / FAIL per test.
Exit code 0 = all passed, 1 = something failed (usable in scripts and CI).

  load balancer routes   GET /lb/health, GET /lb/status, GET /lb/selftest (every worker directly)
  LibreTranslate routes  GET /languages, POST /detect, POST /translate (single, batch, mixed
                         languages, already-target), error handling (missing text, bad language)
  load                   40 parallel requests: all succeed and are spread over the workers
"""
import json
import sys
import threading
import time
import urllib.error
import urllib.request

args = [a for a in sys.argv[1:]]
API_KEY = None
if "--api-key" in args:
    i = args.index("--api-key")
    API_KEY = args[i + 1]
    del args[i:i + 2]
URL = (args[0] if args else "http://127.0.0.1:5000").rstrip("/")

results = []


def call(method, path, payload=None, timeout=300):
    """Returns (HTTP status, parsed JSON or text). Never raises for HTTP errors."""
    if payload is not None and API_KEY:
        payload = dict(payload, api_key=API_KEY)
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    request = urllib.request.Request(URL + path, data=data, method=method,
                                     headers={"Content-Type": "application/json"} if data else {})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            status, body = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, body = error.code, error.read()
    try:
        return status, json.loads(body.decode("utf-8"))
    except ValueError:
        return status, body.decode("utf-8", "replace")


def test(name, condition, details=""):
    results.append(bool(condition))
    print(f"{'PASS' if condition else 'FAIL'}  {name}" + (f"  ({details})" if details else ""))
    return condition


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")  # show accents correctly
    print(f"Testing {URL}\n")

    # --- Load balancer routes ---------------------------------------------------------------
    try:
        status, health = call("GET", "/lb/health", timeout=10)
    except (urllib.error.URLError, OSError) as error:
        test("server reachable", False, f"{error}. Start it with start.cmd")
        return 1
    is_lb = status in (200, 503) and isinstance(health, dict) and "healthy" in health
    if is_lb:
        test("GET /lb/health", status == 200 and health["healthy"] > 0,
             f"{health['status']}: {health['healthy']} of {health['total']} backend(s) healthy")

        status, lb = call("GET", "/lb/status", timeout=10)
        test("GET /lb/status", status == 200 and len(lb.get("backends", [])) == lb.get("total"),
             f"pid {lb.get('pid')}, started {lb.get('started')}, {lb.get('total')} backend(s)")

        query = f"?api_key={API_KEY}" if API_KEY else ""
        status, report = call("GET", "/lb/selftest" + query)
        test("GET /lb/selftest", status == 200 and report.get("passed"),
             f"{report.get('backends_passed')} of {report.get('backends_total')} backend(s) passed in {report.get('ms')} ms")
        for backend in report.get("backends", []):
            failed = [c for c in backend["checks"] if not c["passed"]]
            print(f"        {'ok  ' if backend['passed'] else 'FAIL'} {backend['url']}: {len(backend['checks'])} checks"
                  + "".join(f"\n             {c['check']}: {c['details']}" for c in failed))
    else:
        print("INFO  /lb routes not found: a single LibreTranslate server, not the load balancer")

    # --- LibreTranslate routes, through the load balancer ------------------------------------
    status, languages = call("GET", "/languages")
    codes = [l["code"] for l in languages] if status == 200 else []
    test("GET /languages", codes, f"{len(codes)} loaded: {', '.join(codes)}")
    if not {"en", "fr", "es"} <= set(codes):
        test("languages en, fr, es loaded (needed by the next tests)", False)
        return summary()

    status, detected = call("POST", "/detect", {"q": "Gracias por su ayuda, nos vemos mañana."})
    test("POST /detect (Spanish)", status == 200 and detected[0]["language"] == "es",
         f"{detected[0]['language']} {detected[0]['confidence']}%" if status == 200 else f"HTTP {status}")

    status, single = call("POST", "/translate", {"q": "Good morning", "source": "en", "target": "fr", "format": "text"})
    test("POST /translate single text en->fr", status == 200 and single["translatedText"].strip(),
         single.get("translatedText") if status == 200 else f"HTTP {status}: {single}")

    batch = ["The car is red.", "Where is the workshop?", "Please check the engine."]
    status, out = call("POST", "/translate", {"q": batch, "source": "en", "target": "es", "format": "text"})
    test("POST /translate batch of 3 en->es", status == 200 and len(out["translatedText"]) == 3,
         " | ".join(out["translatedText"]) if status == 200 else f"HTTP {status}: {out}")

    # Mixed languages: detect each text, translate with its language as source (what the app does)
    mixed = {"Je voudrais un café.": "fr", "¿Dónde está la estación?": "es", "Thank you very much.": "en"}
    ok = True
    shown = []
    for text, expected in mixed.items():
        status, d = call("POST", "/detect", {"q": text})
        got = d[0]["language"] if status == 200 else None
        target = "de" if "de" in codes else ("en" if expected != "en" else "fr")
        status, t = call("POST", "/translate", {"q": text, "source": got or "auto", "target": target, "format": "text"})
        ok = ok and got == expected and status == 200
        shown.append(f"[{got}] {t.get('translatedText') if status == 200 else status}")
    test("mixed languages: detect each + translate", ok, " | ".join(shown))

    status, same = call("POST", "/translate", {"q": "Bonjour", "source": "fr", "target": "fr", "format": "text"})
    test("POST /translate source = target", status in (200, 400), f"HTTP {status}")

    status, error = call("POST", "/translate", {"source": "en", "target": "fr"})
    test("POST /translate without q -> 400 with error", status == 400 and "error" in error, f"HTTP {status}: {error}")

    status, error = call("POST", "/translate", {"q": "Hello", "source": "en", "target": "xx"})
    test("POST /translate unknown target -> 400 with error", status == 400 and "error" in error, f"HTTP {status}: {error}")

    # --- Load: parallel requests are spread over the workers ---------------------------------
    before = call("GET", "/lb/status", timeout=10)[1] if is_lb else None
    statuses = []
    lock = threading.Lock()

    def one(i):
        s, _ = call("POST", "/translate", {"q": f"Vehicle {i} is ready for delivery.", "source": "en",
                                           "target": "fr", "format": "text"})
        with lock:
            statuses.append(s)

    started = time.perf_counter()
    threads = [threading.Thread(target=one, args=(i,)) for i in range(40)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    seconds = time.perf_counter() - started
    test("40 parallel /translate requests", statuses.count(200) == 40,
         f"{statuses.count(200)}/40 OK in {seconds:.1f}s = {40 / seconds:.1f} req/s")

    if is_lb:
        after = call("GET", "/lb/status", timeout=10)[1]
        served = [a["served"] - b["served"] for a, b in zip(after["backends"], before["backends"])]
        used = sum(1 for s in served if s > 0)
        healthy = sum(1 for b in after["backends"] if b["healthy"])
        test("requests spread over the workers", used >= min(healthy, 2),
             "per backend: " + ", ".join(f"{b['url'].rsplit(':', 1)[-1]}={s}" for b, s in zip(after["backends"], served)))

    return summary()


def summary():
    passed = sum(results)
    print(f"\n{'ALL PASSED' if passed == len(results) else 'FAILED'}: {passed} of {len(results)} tests passed")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
