"""Shared settings for the Argos Translate server scripts (standard library only).

config.json in this folder holds the server settings. Every script reads it through
read_config(), so a setting means the same everywhere.

configure_argos() must run BEFORE argostranslate is imported: Argos reads its folders and
options from environment variables when it is imported.
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIG_FILE = os.path.join(HERE, "config.json")

# Language models live in this folder (not in the user profile), so the whole server folder can
# be copied to a PC that cannot download them (e.g. on a corporate network).
MODELS_DIR = os.path.join(HERE, "models")

# Built-in defaults; config.json overrides them, command-line options override both
DEFAULTS = {
    "host": "127.0.0.1",
    "port": 5000,
    "workers": 2,
    "worker_base_port": 0,        # 0 = port + 1
    "languages": "en,fr,es",
    "threads_per_worker": 0,      # 0 = split the CPU cores evenly
    "backends": [],               # other translation servers, e.g. ["http://server2:5000"]
    "proxy": "",                  # for downloads only, e.g. "http://proxy.company.com:8080"
    "log_file": "server.log",     # relative to this folder; "" = console only
}


def read_config(path=CONFIG_FILE):
    """DEFAULTS updated with config.json. Unknown keys are an error (catches typos)."""
    settings = dict(DEFAULTS)
    if os.path.exists(path):
        with open(path, encoding="utf-8-sig") as f:
            from_file = json.load(f)
        unknown = set(from_file) - set(DEFAULTS) - {"comment"}
        if unknown:
            raise ValueError(f"{path}: unknown setting(s) {', '.join(sorted(unknown))}")
        settings.update({k: v for k, v in from_file.items() if k in DEFAULTS})
    return settings


def language_codes(languages):
    """'en,fr' or ['en', 'fr'] -> ['en', 'fr']; 'all' -> None (= every available language)."""
    if isinstance(languages, list):
        languages = ",".join(languages)
    languages = (languages or "").strip()
    if languages.lower() == "all":
        return None
    return [c.strip() for c in languages.split(",") if c.strip()]


def configure_argos(proxy=""):
    """Environment for Argos Translate. Call before importing argostranslate.

    - models in MODELS_DIR (Argos uses XDG_DATA_HOME/argos-translate)
    - MiniSBD sentence splitting: small ONNX models downloaded by install_models.py. The default
      (Stanza) can try to download its resource list while translating, which fails offline or
      on networks that block it ("getaddrinfo failed").
    - proxy for downloads (install_models.py); urllib reads HTTP(S)_PROXY
    """
    os.makedirs(MODELS_DIR, exist_ok=True)
    os.environ["XDG_DATA_HOME"] = MODELS_DIR
    os.environ["XDG_CACHE_HOME"] = os.path.join(MODELS_DIR, "cache")
    os.environ["XDG_CONFIG_HOME"] = os.path.join(MODELS_DIR, "config")
    os.environ.setdefault("ARGOS_CHUNK_TYPE", "MINISBD")
    os.environ.setdefault("ARGOS_DEVICE_TYPE", "cpu")
    if proxy:
        os.environ["HTTP_PROXY"] = os.environ["HTTPS_PROXY"] = proxy
        os.environ["http_proxy"] = os.environ["https_proxy"] = proxy
        os.environ.setdefault("NO_PROXY", "127.0.0.1,localhost")


def argos_data_dir():
    return os.path.join(MODELS_DIR, "argos-translate")
