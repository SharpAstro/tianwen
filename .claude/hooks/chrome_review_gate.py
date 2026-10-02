"""Ask for a chrome review once a round of UI changes is done: the SessionStart and Stop hooks in .claude/settings.json.

The chrome-review agent (.claude/agents/chrome-review.md) reads a diff for layout arithmetic the engine should own. A rule
that says "run it before a PR" is skipped as easily as any other rule, so this makes the ask mechanical. When the session is
about to finish, the branch's UI diff has changed since the session started, and that exact diff has not been reviewed, the
stop is blocked ONCE with a request to run the agent. The continuation that follows counts as the review, so the fixes made
in answer to it do not ask again; the next round of UI changes does.

It reads the git diff rather than watching the edit tools, because a session edits through scripts as often as through
Edit, and a hook on the tools would miss those. State lives under the git dir, per worktree and never committed: the diff
each session started with, and the diffs already reviewed. Any failure lets the stop through: a broken hook must never
wedge a session.

    python .claude/hooks/chrome_review_gate.py session-start|stop   (the hook's JSON on stdin)
"""
import hashlib
import json
import os
import re
import subprocess
import sys
import time

# The chrome: the viewer and GUI widgets, their hosts, and the web host's pages. Not the benchmarks or the E2E harness.
UI = re.compile(r"^src/TianWen\.UI\.(Abstractions|Shared|Gui|FitsViewer|Web)/.+\.(cs|razor)$")
KEEP_SESSIONS_DAYS = 30
KEEP_REVIEWED = 200


def git(*args):
    r = subprocess.run(["git", *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.stdout if r.returncode == 0 else ""


def fingerprint():
    """A hash of the branch's UI diff against where it left main, uncommitted and untracked files included; None if none."""
    base = git("merge-base", "HEAD", "origin/main").strip() or "HEAD"
    changed = git("diff", "--name-only", base).splitlines()
    untracked = set(git("ls-files", "--others", "--exclude-standard").splitlines())
    files = sorted({f for f in changed + list(untracked) if UI.match(f)})
    if not files:
        return None, []
    h = hashlib.sha256(git("diff", base, "--", *files).encode("utf-8"))
    for f in files:
        if f in untracked and os.path.isfile(f):
            with open(f, "rb") as stream:
                h.update(stream.read())
    return h.hexdigest(), files


def read(path):
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except OSError:
        return ""


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def prune(state):
    cutoff = time.time() - KEEP_SESSIONS_DAYS * 86400
    for name in os.listdir(state):
        path = os.path.join(state, name)
        if name.startswith("session-") and os.path.getmtime(path) < cutoff:
            os.remove(path)
    reviewed = os.path.join(state, "reviewed")
    lines = read(reviewed).split()
    if len(lines) > KEEP_REVIEWED:
        write(reviewed, "\n".join(lines[-KEEP_REVIEWED:]) + "\n")


def main():
    event = sys.argv[1] if len(sys.argv) > 1 else ""
    data = json.loads(sys.stdin.read() or "{}")
    os.chdir(os.environ.get("CLAUDE_PROJECT_DIR") or data.get("cwd") or ".")

    state = git("rev-parse", "--git-path", "chrome-review").strip()
    if not state:
        return
    os.makedirs(state, exist_ok=True)
    session = re.sub(r"[^\w-]", "", data.get("session_id") or "") or "unknown"
    baseline = os.path.join(state, f"session-{session}")
    reviewed = os.path.join(state, "reviewed")
    fp, files = fingerprint()

    if event == "session-start":
        prune(state)
        # Kept across a resume or a compaction, so what the session changed before either still counts as its own.
        if not os.path.exists(baseline):
            write(baseline, fp or "")
        return

    if event != "stop" or fp is None:
        return
    if data.get("stop_hook_active"):
        # This is the continuation the block below asked for: whatever it found and fixed, this round is reviewed.
        with open(reviewed, "a", encoding="utf-8", newline="\n") as f:
            f.write(fp + "\n")
        return
    if fp == read(baseline).strip() or fp in read(reviewed).split():
        return

    shown = ", ".join(files[:8]) + (f" and {len(files) - 8} more" if len(files) > 8 else "")
    print(json.dumps({
        "decision": "block",
        "reason": (
            f"UI chrome changed on this branch since the last chrome review ({shown}). Before finishing, run the "
            "chrome-review agent over it (Agent tool, subagent_type \"chrome-review\", base origin/main, or the /chrome-review "
            "skill), open each line it reports to confirm it, and tell the user what it found and what you confirmed. If this "
            "change cannot carry layout arithmetic (a rename, a comment, a non-chrome type), say so in one line instead."
        ),
    }))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:  # never wedge a session over a review reminder
        print(f"chrome_review_gate: {e}", file=sys.stderr)
    sys.exit(0)
