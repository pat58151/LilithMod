"""Checks the release zip as a stranger would receive it.

The mod has only ever run from the folder it was built in, where every absolute
path happens to be correct and every developer asset happens to be present.
This unpacks the actual archive somewhere unrelated and asserts what a fresh
install needs - and, just as importantly, what must NOT be in it.

It cannot prove the mod runs on another machine. It proves the archive is not
obviously unable to.

    python verify-package.py            # packages first, then checks
    python verify-package.py --zip X    # checks an existing archive
"""

import argparse
import os
import re
import subprocess
import sys
import tempfile
import zipfile

ROOT = os.path.dirname(os.path.abspath(__file__))
POWERSHELL = "powershell.exe"

failures = []


def check(condition, message):
    if not condition:
        failures.append(message)


def package():
    script = os.path.join(ROOT, "runtime", "package-mod.ps1")
    result = subprocess.run(
        [POWERSHELL, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script],
        capture_output=True, text=True)
    if result.returncode != 0:
        print(result.stdout)
        print(result.stderr, file=sys.stderr)
        sys.exit("VERIFY FAIL - packaging failed")
    dist = os.path.join(ROOT, "dist")
    zips = [os.path.join(dist, f) for f in os.listdir(dist)] if os.path.isdir(dist) else []
    zips = [z for z in zips if z.endswith(".zip")]
    if not zips:
        sys.exit("VERIFY FAIL - packaging produced no zip")
    return max(zips, key=os.path.getmtime)


parser = argparse.ArgumentParser()
parser.add_argument("--zip", dest="zip_path")
args = parser.parse_args()
zip_path = args.zip_path or package()
print(f"[verify-package] Checking {os.path.basename(zip_path)}")

with tempfile.TemporaryDirectory(prefix="lilithmod-clean-") as room:
    with zipfile.ZipFile(zip_path) as archive:
        archive.extractall(room)
    names = [n.replace("\\", "/") for n in
             (os.path.relpath(os.path.join(dirpath, f), room)
              for dirpath, _, files in os.walk(room) for f in files)]
    lower = [n.lower() for n in names]

    # -- 1. What a fresh install cannot start without ---------------------------
    plugin = "bepinex/plugins/lilithmod/lilithmod.dll"
    check(plugin in lower, "The plugin DLL is missing from the archive")
    check(any(n.endswith("winhttp.dll") for n in lower),
          "The Doorstop proxy is missing, so BepInEx never loads")
    check(any(n.endswith("doorstop_config.ini") for n in lower),
          "doorstop_config.ini is missing")
    check(any(n.endswith("install.txt") for n in lower),
          "INSTALL.txt is missing, leaving no instructions in the archive")

    # -- 2. The setting the whole install hinges on -----------------------------
    # Steam passes DOORSTOP_DISABLE=TRUE on essentially every launch. Without
    # this the mod silently does not load and the logs look perfectly healthy.
    doorstop = next((n for n in names if n.lower().endswith("doorstop_config.ini")), None)
    if doorstop:
        with open(os.path.join(room, doorstop), encoding="utf-8-sig") as handle:
            text = handle.read()
        check(re.search(r"(?m)^ignore_disable_switch\s*=\s*true\s*$", text),
              "doorstop_config.ini does not set ignore_disable_switch = true; "
              "Steam will silently skip the mod on every launch")

    # -- 3. Help, in the languages the mod claims to offer -----------------------
    for language in ("overview.txt", "overview.ja.txt", "overview.zh.txt"):
        check(any(n.endswith("help/" + language) for n in lower),
              f"help/{language} is missing, so Help falls back or opens nothing")

    # -- 4. Nothing of the developers' content, and nothing of mine --------------
    # Extracted game audio, the model derived from it, and the script inventory
    # are the game developers' content. Local use only, never redistributed.
    for pattern, why in (
            (r"\.(wav|mp3|ogg)$", "extracted game audio"),
            (r"\.(ckpt|pth|safetensors)$", "a voice model derived from game audio"),
            (r"\.tsv$", "the game's script inventory"),
            (r"\.cfg$", "a config file, which is where the API key lives"),
            (r"\.(log|pdb)$", "build or run debris")):
        # The pre-seeded BepInEx.cfg (console off) is ours and holds no key.
        offenders = [n for n in lower if re.search(pattern, n)
                     and n != "bepinex/config/bepinex.cfg"]
        check(not offenders, f"Archive contains {why}: {offenders[:3]}")

    # -- 5. No trace of the machine it was built on -----------------------------
    # An absolute path that happens to be right here is wrong everywhere else,
    # and this is the failure that cannot be noticed from this machine.
    #
    # Scoped to the files this project actually produces. Third-party assemblies
    # (NAudio, Capstone, SmartReader) embed their own authors' build paths, which
    # are neither ours to fix nor a portability problem.
    local = re.compile(r"(?i)(d:\\lilith|d:\\steamlibrary|c:\\users\\[a-z0-9_.-]+)")
    ours = tuple(".txt .ini .json .cfg .ps1 .py .md".split()) + ("lilithmod.dll",)
    for name in names:
        if not name.lower().endswith(ours):
            continue
        path = os.path.join(room, name)
        if os.path.getsize(path) > 2_000_000:
            continue
        try:
            with open(path, encoding="utf-8", errors="ignore") as handle:
                body = handle.read()
        except OSError:
            continue
        found = local.search(body)
        check(not found,
              f"{name} contains a path from the build machine: {found.group(0) if found else ''}")

    # -- 6. No credentials, however they got there ------------------------------
    for name in names:
        path = os.path.join(room, name)
        if os.path.getsize(path) > 2_000_000:
            continue
        try:
            with open(path, encoding="utf-8", errors="ignore") as handle:
                body = handle.read()
        except OSError:
            continue
        check(not re.search(r"sk-[a-zA-Z0-9]{16,}", body),
              f"{name} looks like it contains an API key")

if failures:
    print("VERIFY FAIL")
    for failure in failures:
        print("  -", failure)
    sys.exit(1)

print(f"[verify-package] {len(names)} files checked")
print("VERIFY PASS")
