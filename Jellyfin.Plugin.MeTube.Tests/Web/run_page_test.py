"""Runs page-test.html in headless Chrome and checks what the YouTube page did.

Usage: python run_page_test.py            (exit code 0 = all checks passed)
Set CHROME to the browser binary if it is not found automatically.

The page runs every user action against a mocked API and writes its observations
into <pre id="result">; this script reads them back from Chrome's --dump-dom output.
Chrome runs with --lang=en-US, so the script's English strings are expected.
"""
import html
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
PAGE = HERE / "page-test.html"


def find_chrome():
    candidates = [
        os.environ.get("CHROME"),
        shutil.which("google-chrome"),
        shutil.which("google-chrome-stable"),
        shutil.which("chromium"),
        shutil.which("chromium-browser"),
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    ]
    for candidate in candidates:
        if candidate and pathlib.Path(candidate).exists():
            return candidate
    sys.exit("No Chrome found; set CHROME to its path.")


def run_page(chrome):
    args = [
        chrome, "--headless=new", "--disable-gpu", "--no-sandbox", "--lang=en-US",
        "--allow-file-access-from-files", "--virtual-time-budget=30000", "--dump-dom",
        PAGE.as_uri() + "#/home",
    ]
    out = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", timeout=120, check=False).stdout
    match = re.search(r'<pre id="result">(.*?)</pre>', out, re.S)
    if not match:
        sys.exit("The page did not report a result. Chrome output starts with:\n" + out[:2000])
    text = html.unescape(match.group(1))
    if text.startswith("RUNNER FAILED"):
        sys.exit(text)
    return json.loads(text)


GLYPH = '"\uf06a"'  # smart_display in Jellyfin's icon font, as getComputedStyle quotes it


def main():
    r = run_page(find_chrome())
    checks = [
        ("no script errors", r.get("errors") == [], r.get("errors")),
        ("the YouTube library link loses Jellyfin's photo icon", r.get("lib_svg_hidden") is True, r.get("lib_svg_hidden")),
        ("the YouTube library link shows the YouTube icon", r.get("lib_before") == GLYPH, r.get("lib_before")),
        ("the YouTube library button on the home screen too", r.get("home_before") == GLYPH, r.get("home_before")),
        ("other libraries keep their own icon", r.get("radio_svg_display") != "none" and r.get("radio_before") in ("none", "normal"),
         (r.get("radio_svg_display"), r.get("radio_before"))),
        ("the YouTube menu entry uses the YouTube icon", bool(r.get("entry_icons")) and set(r.get("entry_icons")) == {"smart_display"}, r.get("entry_icons")),
        ("the download format shows the user's choice", r.get("format_initial") == "compatible", r.get("format_initial")),
        ("changing the download format saves it", r.get("format_posts") == ["best"], r.get("format_posts")),
        ("Add shows Adding... at once", r.get("add_busy") == "Adding…", r.get("add_busy")),
        ("Add sends one request", r.get("add_calls") == 1, r.get("add_calls")),
        ("Add ends as Queued", r.get("add_after") == "Queued", r.get("add_after")),
        ("a refused Add shows the reason", "MeTube refused it" in (r.get("addFail_after") or ""), r.get("addFail_after")),
        ("Delete asks and then deletes", r.get("delete_calls") == 1, r.get("delete_calls")),
        ("Try again adds again", r.get("retry_found") and r.get("retry_add_calls") == 1, (r.get("retry_found"), r.get("retry_add_calls"))),
        ("Remove from list", r.get("dismiss_found") and r.get("dismiss_calls") == 1, (r.get("dismiss_found"), r.get("dismiss_calls"))),
        ("Clean up list", r.get("clean_found") and r.get("dismiss_calls_after_clean") == 2, (r.get("clean_found"), r.get("dismiss_calls_after_clean"))),
        ("Merge from the Folders tab", r.get("merge_box") and r.get("merge_calls") == 1, (r.get("merge_box"), r.get("merge_calls"))),
        ("Merge folders in Jellyfin's selection menu", r.get("sheet_item") == "Merge folders", r.get("sheet_item")),
        ("the menu opens the Folders tab with both folders", "tab=folders" in (r.get("sheet_hash") or "") and len(r.get("sheet_radios") or []) == 2,
         (r.get("sheet_hash"), r.get("sheet_radios"))),
    ]
    failed = 0
    for name, ok, seen in checks:
        print(("PASS " if ok else "FAIL ") + name + ("" if ok else f"  (got {seen!r})"))
        failed += 0 if ok else 1
    print(f"{len(checks) - failed}/{len(checks)} page checks passed")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
