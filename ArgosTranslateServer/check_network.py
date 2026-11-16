"""Checks whether this network can download what the Argos Translate server needs.

Run it on the network you want to check (e.g. the corporate Wi-Fi), BEFORE setup:
    check_network.cmd
    py check_network.py --proxy http://proxy.company.com:8080

Standard library only: works with any Python 3, no installation needed.

For every download site it checks, step by step:
  DNS     can the name be looked up?  ("getaddrinfo failed" = no)
  HTTPS   can a secure connection be made and a real file be downloaded?
  content is it the real file, not a block page from a company proxy or firewall?
Each site is tried directly and through every proxy found (config.json, --proxy, Windows
settings, HTTP(S)_PROXY), so the result also tells you which proxy setting to use.
Exit code 0 = everything needed can be downloaded.
"""
import argparse
import json
import os
import socket
import ssl
import sys
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))

# (name, url, what it is used for, expected start of the file or None, needed for)
SITES = [
    ("pypi.org", "https://pypi.org/simple/argostranslate/", "Python package list (setup)", b"<!DOCTYPE", "setup"),
    ("files.pythonhosted.org", None, "Python packages incl. PyTorch (setup)", b"PK", "setup"),
    ("raw.githubusercontent.com", "https://raw.githubusercontent.com/argosopentech/argospm-index/main/index.json",
     "list of language models", b"[", "models"),
    ("argos-net.com", "https://argos-net.com/v1/translate-en_fr-1_9.argosmodel", "translation models", b"PK", "models"),
    ("github.com", "https://github.com/LibreTranslate/MiniSBD/releases/download/v0.0.1/en.onnx",
     "sentence splitting models (redirects to release-assets.githubusercontent.com)", None, "models"),
]


def proxies_to_try(cli_proxy):
    found = []
    if cli_proxy:
        found.append(("--proxy", cli_proxy))
    try:
        with open(os.path.join(HERE, "config.json"), encoding="utf-8-sig") as f:
            proxy = json.load(f).get("proxy", "")
        if proxy:
            found.append(("config.json", proxy))
    except (OSError, ValueError):
        pass
    system = urllib.request.getproxies()  # Windows settings (registry) and HTTP(S)_PROXY variables
    for key in ("https", "http"):
        if system.get(key):
            found.append((f"Windows/environment ({key})", system[key]))
    unique, seen = [], set()
    for source, proxy in found:
        proxy = proxy if "://" in proxy else "http://" + proxy
        if proxy not in seen:
            seen.add(proxy)
            unique.append((source, proxy))
    return unique


def check_dns(host):
    try:
        addresses = {a[4][0] for a in socket.getaddrinfo(host, 443, proto=socket.IPPROTO_TCP)}
        return True, ", ".join(sorted(addresses)[:2])
    except socket.gaierror as error:
        return False, f"getaddrinfo failed ({error.strerror or error})"


def download(url, proxy, expected, limit=4096, timeout=20):
    """GET the first bytes of url, directly (proxy=None) or through proxy. Returns (ok, details)."""
    handlers = [urllib.request.ProxyHandler({"http": proxy, "https": proxy} if proxy else {})]
    opener = urllib.request.build_opener(*handlers)
    request = urllib.request.Request(url, headers={"User-Agent": "network-check/1.0", "Range": f"bytes=0-{limit - 1}"})
    started = time.perf_counter()
    try:
        with opener.open(request, timeout=timeout) as response:
            data = response.read(limit)
            final = response.geturl()
            status = response.status
    except urllib.error.HTTPError as error:
        return False, f"HTTP {error.code} {error.reason}" + (" (proxy needs a login?)" if error.code == 407 else "")
    except urllib.error.URLError as error:
        reason = error.reason
        if isinstance(reason, socket.gaierror):
            return False, "getaddrinfo failed (name not found" + (" - check the proxy address)" if proxy else ")")
        if isinstance(reason, ssl.SSLError):
            return False, f"SSL error ({reason}) - a company proxy may inspect HTTPS traffic"
        return False, f"{reason}"
    except (OSError, ssl.SSLError) as error:
        return False, f"{error}"
    ms = (time.perf_counter() - started) * 1000
    if expected and not data.startswith(expected):
        snippet = data[:80].decode("utf-8", "replace").replace("\n", " ")
        return False, f"got something else than the file (block page?): {snippet!r}"
    host_note = "" if urllib.request.urlparse(final).netloc == urllib.request.urlparse(url).netloc \
        else f", redirected to {urllib.request.urlparse(final).netloc}"
    return True, f"HTTP {status}, {len(data)} bytes in {ms:.0f} ms{host_note}"


def package_file_url():
    """A real wheel URL on files.pythonhosted.org, taken from pypi.org (None if pypi is blocked)."""
    try:
        with urllib.request.urlopen("https://pypi.org/pypi/langdetect/json", timeout=20) as r:
            data = json.load(r)
        return data["urls"][0]["url"]
    except Exception:
        return "https://files.pythonhosted.org/packages/source/l/langdetect/langdetect-1.0.9.tar.gz"


def main():
    parser = argparse.ArgumentParser(description="Check the download sites of the Argos Translate server")
    parser.add_argument("--proxy", default="", help="proxy to try, e.g. http://proxy.company.com:8080")
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    proxies = proxies_to_try(args.proxy)
    print("Network check for the Argos Translate server")
    print("Proxies found: " + (", ".join(f"{p} ({s})" for s, p in proxies) if proxies else "none (direct connection only)"))
    print()

    works = {}  # site -> how it works ("direct" or proxy) or None
    for name, url, purpose, expected, needed_for in SITES:
        if url is None:
            url = package_file_url()
            expected = b"PK" if url.endswith(".whl") else b"\x1f\x8b"  # wheel (zip) or .tar.gz
        print(f"{name}  -  {purpose}")
        dns_ok, dns_details = check_dns(name)
        print(f"   DNS            {'OK  ' if dns_ok else 'FAIL'}  {dns_details}")
        ways = []
        if dns_ok:
            ok, details = download(url, None, expected)
            print(f"   direct         {'OK  ' if ok else 'FAIL'}  {details}")
            if ok:
                ways.append("direct")
        else:
            print("   direct         skip  (the name cannot be looked up without a proxy)")
        for source, proxy in proxies:
            ok, details = download(url, proxy, expected)
            print(f"   via proxy      {'OK  ' if ok else 'FAIL'}  {details}  [{proxy}]")
            if ok:
                ways.append(proxy)
        works[name] = (ways, needed_for)
        print()

    print("Result")
    blocked = [n for n, (ways, _) in works.items() if not ways]
    direct = all("direct" in ways for ways, _ in works.values())
    for n, (ways, needed) in works.items():
        print(f"   {'OK     ' if ways else 'BLOCKED'}  {n:<27} ({needed}) " + (f"via {ways[0]}" if ways else ""))
    print()
    if not blocked and direct:
        print("Everything can be downloaded directly. Run setup.cmd.")
    elif not blocked:
        proxy = next(w for ways, _ in works.values() for w in ways if w != "direct")
        print(f"Everything can be downloaded through the proxy {proxy}.")
        print(f'Set  "proxy": "{proxy}"  in config.json, then run setup.cmd.')
    else:
        print("Blocked: " + ", ".join(blocked))
        print("  - Ask IT to allow these sites (or for the proxy address, then: check_network.cmd --proxy http://proxy:port), or")
        print("  - Run setup.cmd on another network (e.g. at home) and copy the folders .venv and models to this PC:")
        print("    after setup the server works completely offline.")
    return 0 if not blocked else 1


if __name__ == "__main__":
    sys.exit(main())
