"""Translation worker: Argos Translate behind a small JSON HTTP API.

One worker process translates with the Argos Translate models installed in the models folder
(install_models.py). The load balancer (load_balancer.py) starts several workers and spreads the
requests over them; a worker can also run on its own:

    .venv\\Scripts\\python worker.py --port 5001

Routes (JSON in, JSON out):
    GET  /languages   [{"code": "en", "name": "English", "targets": ["de", "es", ...]}, ...]
    GET  /health      {"status": "ok", "languages": 6}
    POST /detect      {"q": "text"}
                      -> [{"language": "fr", "confidence": 99.9}, ...]   (best first, 0..100)
    POST /translate   {"q": "text" or ["a", "b"], "source": "en" or "auto", "target": "fr"}
                      -> {"translatedText": "..." or [...],
                          "detectedLanguage": {...} or [...]}            (only with source "auto")
Errors: HTTP 400 {"error": "..."} for bad requests, 500 for translation failures.

Language detection: Lingua, limited to the installed languages. With source "auto" every text is
detected on its own, so a batch may mix languages. For short or ambiguous texts the confidence
is low and the next candidates are listed: the Translator app verifies those.
Translation between two languages without a direct model goes through English (Argos pivot).
Everything runs offline: no internet access is needed once the models are installed.
"""
import argparse
import json
import socket
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import common

common.configure_argos()  # before importing argostranslate

from argostranslate import translate as argos  # noqa: E402
from lingua import IsoCode639_1, Language, LanguageDetectorBuilder  # noqa: E402

# Argos codes that are not ISO 639-1 codes -> the language Lingua knows them as
ARGOS_TO_ISO = {"pb": "pt", "zt": "zh"}
MAX_CHARS = 100_000      # per request, protects the worker from huge bodies
MAX_CANDIDATES = 5       # languages returned by /detect, best first


def log(message):
    print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] [worker] {message}", flush=True)


class Translator:
    """The installed Argos languages and the translations between them."""

    def __init__(self, wanted_codes):
        installed = argos.get_installed_languages()
        self.languages = {l.code: l for l in installed if wanted_codes is None or l.code in wanted_codes}
        self.targets = {}
        for code, language in self.languages.items():
            self.targets[code] = sorted(t for t, other in self.languages.items()
                                        if t != code and language.get_translation(other) is not None)
        self._translations = {}
        self._first_use = {}
        self._lock = threading.Lock()

        # Lingua, limited to the installed languages it knows (accurate on short texts, and its
        # confidence is low when a text is ambiguous, e.g. single borrowed words like "Ciao")
        self._lingua_to_codes = {}
        for code in self.languages:
            try:
                language = Language.from_iso_code_639_1(IsoCode639_1.from_str(ARGOS_TO_ISO.get(code, code)))
            except (ValueError, KeyError):
                continue  # Lingua cannot detect it; it can still be translated with an explicit source
            self._lingua_to_codes.setdefault(language, []).append(code)
        self.undetectable = sorted(c for c in self.languages
                                   if not any(c in codes for codes in self._lingua_to_codes.values()))
        languages = list(self._lingua_to_codes)
        if len(languages) >= 2:
            self._detector = LanguageDetectorBuilder.from_languages(*languages).with_preloaded_language_models().build()
        else:
            self._detector = None  # zero or one detectable language: nothing to choose between

    def detect(self, text):
        """[(code, confidence 0..100)] among the installed languages, best first."""
        if self._detector is None:
            only = next(iter(self._lingua_to_codes.values()), list(self.languages))[0]
            return [(only, 100.0)]
        values = self._detector.compute_language_confidence_values(text)
        found = []
        for value in values[:MAX_CANDIDATES]:
            for code in self._lingua_to_codes[value.language]:
                found.append((code, round(value.value * 100, 1)))
        if not found or found[0][1] == 0:
            return [(found[0][0] if found else next(iter(self.languages)), 0.0)]  # no letters: unknown
        return found

    def translation(self, source, target):
        key = (source, target)
        with self._lock:
            if key not in self._translations:
                self._translations[key] = self.languages[source].get_translation(self.languages[target])
                self._first_use[key] = threading.Lock()
            return self._translations[key], self._first_use[key]

    def translate(self, text, source, target):
        if source == target or not text.strip():
            return text
        translation, first_use = self.translation(source, target)
        if first_use is not None:
            # The first call loads the model (and the sentence splitter): once, not in parallel
            with first_use:
                result = translation.translate(text)
                with self._lock:
                    self._first_use[(source, target)] = None
                return result
        return translation.translate(text)


class BadRequest(Exception):
    pass


def make_handler(engine):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):
            pass

        def send_json(self, status, value):
            body = json.dumps(value, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            try:
                self.wfile.write(body)
            except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
                self.close_connection = True

        def read_json(self):
            length = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(length) if length else b""
            if not raw:
                return {}
            try:
                data = json.loads(raw.decode("utf-8"))
            except (ValueError, UnicodeDecodeError):
                raise BadRequest("Invalid request: the body must be JSON")
            if not isinstance(data, dict):
                raise BadRequest("Invalid request: the body must be a JSON object")
            return data

        def do_GET(self):
            route = self.path.split("?")[0].rstrip("/")
            if route == "/languages":
                return self.send_json(200, [{"code": c, "name": l.name, "targets": engine.targets[c]}
                                            for c, l in engine.languages.items()])
            if route == "/health":
                return self.send_json(200, {"status": "ok", "languages": len(engine.languages)})
            self.send_json(404, {"error": f"Not found: GET {route}"})

        def do_POST(self):
            route = self.path.split("?")[0].rstrip("/")
            try:
                data = self.read_json()
                if route == "/detect":
                    return self.send_json(200, self.detect(data))
                if route == "/translate":
                    return self.send_json(200, self.translate(data))
                self.send_json(404, {"error": f"Not found: POST {route}"})
            except BadRequest as error:
                self.send_json(400, {"error": str(error)})
            except Exception as error:  # translation failure: report it, keep the worker running
                log(f"error on {route}: {error!r}")
                self.send_json(500, {"error": f"Translation failed: {error}"})

        @staticmethod
        def texts(data):
            q = data.get("q")
            if q is None or q == "" or q == []:
                raise BadRequest("Invalid request: missing q parameter")
            texts = q if isinstance(q, list) else [q]
            if not all(isinstance(t, str) for t in texts):
                raise BadRequest("Invalid request: q must be a text or a list of texts")
            if sum(len(t) for t in texts) > MAX_CHARS:
                raise BadRequest(f"Invalid request: more than {MAX_CHARS} characters")
            return q, texts

        def detect(self, data):
            q, texts = self.texts(data)
            if isinstance(q, list):
                raise BadRequest("Invalid request: /detect takes one text (q as a string)")
            return [{"language": code, "confidence": confidence} for code, confidence in engine.detect(q)]

        def translate(self, data):
            q, texts = self.texts(data)
            source = (data.get("source") or "auto").strip()
            target = (data.get("target") or "").strip()
            if not target:
                raise BadRequest("Invalid request: missing target parameter")
            if target not in engine.languages:
                raise BadRequest(f"{target} is not supported")
            if source != "auto" and source not in engine.languages:
                raise BadRequest(f"{source} is not supported")

            results, detected = [], []
            for text in texts:
                text_source = source
                if source == "auto":
                    code, confidence = engine.detect(text)[0]
                    detected.append({"language": code, "confidence": confidence})
                    if code not in engine.languages:
                        raise BadRequest(f"Detected language {code} is not supported")
                    text_source = code
                results.append(engine.translate(text, text_source, target))

            response = {"translatedText": results if isinstance(q, list) else results[0]}
            if source == "auto":
                response["detectedLanguage"] = detected if isinstance(q, list) else detected[0]
            return response

    return Handler


class QuietServer(ThreadingHTTPServer):
    allow_reuse_address = False
    daemon_threads = True

    def server_bind(self):
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):  # Windows: refuse a port that is in use
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()

    def handle_error(self, request, client_address):
        if isinstance(sys.exc_info()[1], (ConnectionResetError, ConnectionAbortedError, BrokenPipeError, TimeoutError)):
            return  # a client closed its connection: normal
        super().handle_error(request, client_address)


def main():
    config = common.read_config()
    parser = argparse.ArgumentParser(description="Argos Translate worker")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5001)
    parser.add_argument("--languages", default=config["languages"], help='comma-separated codes, or "all"')
    args = parser.parse_args()

    started = time.perf_counter()
    engine = Translator(common.language_codes(args.languages))
    if not engine.languages:
        log(f"ERROR: no language models installed in {common.argos_data_dir()}. Run install_models.cmd.")
        return 1
    missing = [c for c in (common.language_codes(args.languages) or []) if c not in engine.languages]
    if missing:
        log(f"warning: no models for {', '.join(missing)} (run install_models.cmd)")
    if engine.undetectable:
        log(f"note: language detection does not know {', '.join(engine.undetectable)} (use an explicit source)")

    try:
        server = QuietServer((args.host, args.port), make_handler(engine))
    except OSError as error:
        log(f"ERROR: cannot listen on {args.host}:{args.port} ({error})")
        return 1
    log(f"Argos Translate worker on http://{args.host}:{args.port}: {len(engine.languages)} languages "
        f"({', '.join(engine.languages)}), ready in {time.perf_counter() - started:.1f}s")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
