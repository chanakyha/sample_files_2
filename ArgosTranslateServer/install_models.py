"""Downloads and installs the Argos Translate language models into the models folder.

Usage:
    .venv\\Scripts\\python install_models.py                     languages from config.json
    .venv\\Scripts\\python install_models.py --languages en,fr   other languages
    .venv\\Scripts\\python install_models.py --proxy http://proxy.company.com:8080
    .venv\\Scripts\\python install_models.py --list              what is installed (offline)
    .venv\\Scripts\\python install_models.py --remove            delete all installed models
    .venv\\Scripts\\python install_models.py --remove-others     delete models of languages not in the list

Downloads (run check_network.py first on a corporate network):
    https://raw.githubusercontent.com/argosopentech/argospm-index/main/index.json   model list
    https://argos-net.com/v1/...argosmodel                                         translation models
    https://github.com/LibreTranslate/MiniSBD/releases/...                          sentence splitting
      (redirects to release-assets.githubusercontent.com)

For every language in the list, the models from and to English are installed, plus direct
models between two listed languages when they exist. Other pairs are translated through English.
Afterwards everything works offline. The models folder can be copied to another PC.
"""
import argparse
import os
import shutil
import sys
import time
import urllib.error

import common

DOWNLOAD_HELP = """
Download failed: {error}

The PC cannot reach the download site. On a corporate network this usually means:
  - the network only allows the internet through a proxy: set "proxy" in config.json
    (or use --proxy http://proxy:port). Your proxy is in Windows Settings > Network > Proxy,
    or ask IT.
  - the site is blocked: ask IT to allow it, or download the models on another network
    (e.g. at home) and copy the folder {models} to this PC.
Run check_network.cmd to see exactly which sites are reachable.
"""


def main():
    config = common.read_config()
    parser = argparse.ArgumentParser(description="Install Argos Translate language models")
    parser.add_argument("--languages", default=config["languages"], help='comma-separated codes, or "all"')
    parser.add_argument("--proxy", default=config["proxy"], help="proxy for the downloads")
    parser.add_argument("--list", action="store_true", help="show the installed models and exit")
    parser.add_argument("--remove", action="store_true", help="delete all installed models and exit")
    parser.add_argument("--remove-others", action="store_true",
                        help="delete the models of languages that are not in the list and exit (offline)")
    args = parser.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    if args.remove:
        if os.path.isdir(common.MODELS_DIR):
            shutil.rmtree(common.MODELS_DIR)
        print(f"Removed all models ({common.MODELS_DIR}).")
        return 0

    common.configure_argos(args.proxy)
    from argostranslate import package, translate  # after configure_argos
    from argostranslate import sbd  # noqa: F401  (points MiniSBD at the models folder)
    from minisbd import models as minisbd_models

    if args.list:
        return show_installed(package, minisbd_models)

    wanted = common.language_codes(args.languages)
    if args.remove_others:
        if wanted is None:
            print('Nothing to remove: languages is "all".')
            return 0
        return remove_others(wanted, package, minisbd_models)
    print(f"Models folder: {common.argos_data_dir()}")
    print(f"Languages: {', '.join(wanted) if wanted else 'all'}" + (f"   proxy: {args.proxy}" if args.proxy else ""))

    # 1. Model list
    try:
        print("Downloading the model list ...")
        package.update_package_index()
        available = package.get_available_packages()
    except (urllib.error.URLError, OSError) as error:
        print(DOWNLOAD_HELP.format(error=error, models=common.MODELS_DIR))
        return 1
    if not available:
        print(DOWNLOAD_HELP.format(error="the model list is empty (blocked or redirected?)", models=common.MODELS_DIR))
        return 1

    codes = {p.from_code for p in available} | {p.to_code for p in available}
    if wanted is None:
        wanted = sorted(codes)
    unknown = [c for c in wanted if c not in codes]
    if unknown:
        print(f"No Argos Translate models for: {', '.join(unknown)}. Available: {', '.join(sorted(codes))}")
        return 1

    # 2. Translation models: X <-> English for every X, plus direct pairs inside the list
    selected = [p for p in available if p.from_code in wanted and p.to_code in wanted]
    installed = {(p.from_code, p.to_code): p for p in package.get_installed_packages()}
    failed = 0
    for i, pkg in enumerate(sorted(selected, key=lambda p: (p.from_code, p.to_code)), 1):
        name = f"[{i}/{len(selected)}] {pkg.from_code} -> {pkg.to_code} {pkg.package_version}"
        have = installed.get((pkg.from_code, pkg.to_code))
        if have is not None and have.package_version == pkg.package_version:
            print(f"{name}: already installed")
            continue
        started = time.perf_counter()
        print(f"{name}: downloading ...", flush=True)
        try:
            if have is not None:
                have.update()
            else:
                pkg.install()
            print(f"{name}: installed in {time.perf_counter() - started:.0f}s")
        except (urllib.error.URLError, OSError, Exception) as error:  # one failure: try the others
            failed += 1
            print(f"{name}: FAILED ({error})")
    if failed == len(selected) and selected:
        print(DOWNLOAD_HELP.format(error="no model could be downloaded", models=common.MODELS_DIR))
        return 1

    # 3. Sentence splitting models (MiniSBD), so translating never needs the internet
    splitters = sorted({sbd_code(c, minisbd_models) for c in wanted} | {"en"})
    for code in splitters:
        try:
            minisbd_models.get_model_file(code)
            print(f"sentence splitting {code}: ok")
        except Exception as error:
            failed += 1
            print(f"sentence splitting {code}: FAILED ({error})")

    # 4. Check offline: translate one word with every installed language
    translate.get_installed_languages.cache_clear()  # cached: reload after installing
    languages = {l.code: l for l in translate.get_installed_languages()}
    print("\nTest translations (offline):")
    english = languages.get("en")
    for code in wanted:
        if code == "en" or code not in languages or english is None:
            continue
        try:
            print(f"  en -> {code}: {english.get_translation(languages[code]).translate('Good morning')}")
        except Exception as error:
            failed += 1
            print(f"  en -> {code}: FAILED ({error})")

    print(f"\n{'Done' if not failed else f'Done with {failed} failure(s)'}: "
          f"{len(package.get_installed_packages())} models installed in {common.argos_data_dir()}")
    return 0 if not failed else 1


def sbd_code(code, minisbd_models):
    """MiniSBD model used for a language (Argos falls back to English when there is none)."""
    from argostranslate.sbd import MiniSBDSentencizer
    mapped = MiniSBDSentencizer.LANGUAGE_CODE_MAPPING.get(code, code)
    return mapped if mapped in minisbd_models.list_models() else "en"


def remove_others(wanted, package, minisbd_models):
    """Deletes the translation and sentence splitting models of languages outside the list."""
    removed = 0
    for p in sorted(package.get_installed_packages(), key=lambda p: (p.from_code, p.to_code)):
        if p.from_code not in wanted or p.to_code not in wanted:
            package.uninstall(p)
            removed += 1
            print(f"removed {p.from_code} -> {p.to_code}")
    keep = {sbd_code(c, minisbd_models) for c in wanted} | {"en"}
    sbd_dir = minisbd_models.cache_dir
    for f in sorted(os.listdir(sbd_dir)) if os.path.isdir(sbd_dir) else []:
        if f.endswith(".onnx") and f[:-5] not in keep:
            os.remove(os.path.join(sbd_dir, f))
            print(f"removed sentence splitting {f[:-5]}")
    print(f"Removed {removed} translation models; kept: {', '.join(wanted)}")
    return show_installed(package, minisbd_models)


def show_installed(package, minisbd_models):
    packages = package.get_installed_packages()
    print(f"Models folder: {common.argos_data_dir()}")
    for p in sorted(packages, key=lambda p: (p.from_code, p.to_code)):
        print(f"  {p.from_code} -> {p.to_code}  {p.package_version}")
    sbd_dir = minisbd_models.cache_dir
    splitters = sorted(f[:-5] for f in os.listdir(sbd_dir) if f.endswith(".onnx")) if os.path.isdir(sbd_dir) else []
    print(f"{len(packages)} translation models; sentence splitting: {', '.join(splitters) or 'none'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
