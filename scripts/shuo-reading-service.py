#!/usr/bin/env python3
"""Manage the private Mac reading LaunchAgent from a Shuo checkout."""
import argparse
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import time
import urllib.request

LABEL = "ai.shuo.reading"
DATA = Path.home() / ".local/share/shuo-reading"
PLIST = Path.home() / f"Library/LaunchAgents/{LABEL}.plist"
LOGS = Path.home() / "Library/Logs/ShuoReading"
TARGET = f"gui/{os.getuid()}/{LABEL}" if sys.platform == "darwin" else ""
ROOT = Path(__file__).resolve().parent.parent


def run(*args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)


def loaded():
    return subprocess.run(["launchctl", "print", TARGET], stdout=subprocess.DEVNULL,
                          stderr=subprocess.DEVNULL).returncode == 0


def health(host):
    with urllib.request.urlopen(f"http://{host}:18766/health", timeout=3) as response:
        return json.load(response)


def address():
    cli = shutil.which("tailscale") or str(Path.home() / "go/bin/tailscale")
    return subprocess.check_output([cli, "ip", "-4"], text=True).strip().splitlines()[0]


def stop_if_idle():
    if not loaded():
        return
    with PLIST.open("rb") as stream:
        previous = plistlib.load(stream)["ProgramArguments"]
    host = previous[previous.index("--host") + 1]
    # A running service with unknown health is not safe to interrupt.
    if health(host)["busy"]:
        raise RuntimeError("Mac is reading; stop playback before updating the service.")
    run("launchctl", "bootout", TARGET)
    for _ in range(30):
        if not loaded():
            return
        time.sleep(1)
    raise RuntimeError("Reading service did not unload")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("up", "restart", "down", "status", "logs"))
    args = parser.parse_args()
    if sys.platform != "darwin":
        parser.error("Run this on the Apple Silicon Mac.")
    if args.action == "logs":
        run("tail", "-n", "100", "-f", str(LOGS / "stderr.log"), str(LOGS / "stdout.log"))
        return
    if args.action == "status":
        print(json.dumps(health(address()), ensure_ascii=False))
        return
    stop_if_idle()
    if args.action == "down":
        run("launchctl", "disable", TARGET)
        print("Reading service stopped and disabled")
        return
    host = address()
    DATA.mkdir(parents=True, exist_ok=True)
    LOGS.mkdir(parents=True, exist_ok=True)
    PLIST.parent.mkdir(parents=True, exist_ok=True)
    executable = DATA / "venv/bin/shuo-reading"
    if args.action == "up":
        uv = shutil.which("uv") or "/opt/homebrew/bin/uv"
        run(uv, "sync", "--project", str(ROOT / "reading-server"), "--frozen", "--no-editable", "--no-dev",
            "--reinstall-package", "shuo-reading",
            env={**os.environ, "UV_PROJECT_ENVIRONMENT": str(DATA / "venv")})
    if not executable.exists():
        raise RuntimeError("Run up to install the reading service first")
    config = dict(Label=LABEL, ProgramArguments=[str(executable), "--host", host, "--port", "18766"],
                  WorkingDirectory=str(DATA), RunAtLoad=True, KeepAlive=True, ThrottleInterval=15,
                  EnvironmentVariables={"PYTHONUNBUFFERED": "1", "HF_HUB_DISABLE_XET": "1",
                                        "HF_HOME": str(Path.home() / ".cache/huggingface")},
                  StandardOutPath=str(LOGS / "stdout.log"), StandardErrorPath=str(LOGS / "stderr.log"))
    temporary = PLIST.with_suffix(".plist.tmp")
    with temporary.open("wb") as stream:
        plistlib.dump(config, stream)
    temporary.replace(PLIST)
    run("launchctl", "enable", TARGET)
    run("launchctl", "bootstrap", f"gui/{os.getuid()}", str(PLIST))
    print(f"Waiting for reading service at {host}:18766", flush=True)
    for _ in range(300):
        try:
            result = health(host)
            if result.get("ready"):
                print(json.dumps(result, ensure_ascii=False))
                return
        except (OSError, ValueError):
            pass
        time.sleep(1)
    raise RuntimeError(f"Service not ready; inspect {LOGS / 'stderr.log'}")


if __name__ == "__main__":
    main()
