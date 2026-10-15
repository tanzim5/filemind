"""Opt-in background and login settings for the local desktop app."""
import json
import os
import plistlib
import subprocess
import sys
from pathlib import Path

from .db import DATA_DIR

SETTINGS = DATA_DIR / "desktop.json"
RUN_NAME = "FileMind"
AGENT_NAME = "app.filemind.background"


def preferences() -> dict:
    try:
        data = json.loads(SETTINGS.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        data = {}
    return {"background": bool(data.get("background", False)), "startup": startup_enabled()}


def _command() -> list[str]:
    if getattr(sys, "frozen", False):
        return [str(Path(sys.executable).resolve()), "--background"]
    return [str(Path(sys.executable).resolve()), str(Path(__file__).resolve().parent.parent / "filemind.py"), "--background"]


def _agent_path() -> Path:
    return Path.home() / "Library" / "LaunchAgents" / f"{AGENT_NAME}.plist"


def startup_enabled() -> bool:
    if sys.platform == "win32":
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as key:
                value, _ = winreg.QueryValueEx(key, RUN_NAME)
            return value == subprocess.list2cmdline(_command())
        except OSError:
            return False
    if sys.platform == "darwin":
        try:
            with _agent_path().open("rb") as handle:
                return plistlib.load(handle).get("ProgramArguments") == _command()
        except (OSError, ValueError):
            return False
    return False


def set_startup(enabled: bool) -> None:
    if sys.platform == "win32":
        import winreg
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as key:
            if enabled:
                winreg.SetValueEx(key, RUN_NAME, 0, winreg.REG_SZ, subprocess.list2cmdline(_command()))
            else:
                try:
                    winreg.DeleteValue(key, RUN_NAME)
                except FileNotFoundError:
                    pass
    elif sys.platform == "darwin":
        path = _agent_path()
        if enabled:
            path.parent.mkdir(parents=True, exist_ok=True)
            payload = {"Label": AGENT_NAME, "ProgramArguments": _command(), "RunAtLoad": True,
                       "KeepAlive": False, "WorkingDirectory": str(Path(sys.executable).resolve().parent)}
            temporary = path.with_suffix(".tmp")
            temporary.write_bytes(plistlib.dumps(payload))
            temporary.replace(path)
        else:
            path.unlink(missing_ok=True)
    else:
        raise ValueError("Start at login is available in the Windows and macOS desktop apps")


def update(*, background: bool | None = None, startup: bool | None = None) -> dict:
    if startup is not None:
        set_startup(startup)
    if background is not None:
        SETTINGS.parent.mkdir(parents=True, exist_ok=True)
        temporary = SETTINGS.with_suffix(".tmp")
        temporary.write_text(json.dumps({"background": background}), encoding="utf-8")
        temporary.replace(SETTINGS)
    return preferences()
