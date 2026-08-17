#!/usr/bin/env bash
# Post new fuzzer findings to a webhook, so a machine that finds something says
# so without anyone watching it.
#
# The webhook URL is a credential: it lives in fuzz/webhook.conf, which is not
# tracked, or in PLANK_FUZZ_WEBHOOK. Never commit it.
#
#     echo 'https://discord.com/api/webhooks/...' > fuzz/webhook.conf
#     chmod 600 fuzz/webhook.conf
#
# run-fleet.sh starts this on a loop alongside the workers, so it shares their
# lifecycle. It can also be run by hand, or from cron where that is preferred:
#     */5 * * * * cd ~/fuzz/Plank && ./fuzz/notify-crashes.sh >> fuzz/logs/notify.log 2>&1
#
# --hello  announce that the notifier is armed, and prove the webhook works
# --loop   same as no argument; accepted so the supervisor is greppable
#
# Only crash *signatures* are sent — exception type and the first Plank frame —
# never the input bytes.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CRASHES="$ROOT/fuzz/crashes"
STATE="$ROOT/fuzz/.notified"
LABEL="${PLANK_FUZZ_LABEL:-}"
# fuzz/label.conf names this machine in notifications. Untracked, because what
# the boxes are called is infrastructure, not project detail. Falls back to the
# hostname when absent.
if [ -z "$LABEL" ] && [ -f "$ROOT/fuzz/label.conf" ]; then
  LABEL="$(tr -d '[:space:]' < "$ROOT/fuzz/label.conf")"
fi
LABEL="${LABEL:-$(hostname -s 2>/dev/null || hostname)}"
READER="$ROOT/Plank.Fuzzing.Reader.Target/bin/Release/net10.0/Plank.Fuzzing.Reader.Target"
WRITER="$ROOT/Plank.Fuzzing.Target/bin/Release/net10.0/Plank.Fuzzing.Target"

WEBHOOK="${PLANK_FUZZ_WEBHOOK:-}"
if [ -z "$WEBHOOK" ] && [ -f "$ROOT/fuzz/webhook.conf" ]; then
  WEBHOOK="$(tr -d '[:space:]' < "$ROOT/fuzz/webhook.conf")"
fi
[ -n "$WEBHOOK" ] || { echo "no webhook configured (fuzz/webhook.conf or PLANK_FUZZ_WEBHOOK)" >&2; exit 2; }

commit="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"

post() {
  # Discord caps a message at 2000 characters.
  python3 - "$WEBHOOK" "$1" <<'PY'
import json, sys, urllib.request
url, content = sys.argv[1], sys.argv[2][:1900]
# Discord's edge rejects urllib's default User-Agent with a 403.
req = urllib.request.Request(url, data=json.dumps({"content": content}).encode(),
                             headers={"Content-Type": "application/json",
                                      "User-Agent": "plank-fuzz-notifier/1.0"})
try:
    urllib.request.urlopen(req, timeout=20).read()
except Exception as e:
    print(f"webhook post failed: {e}", file=sys.stderr)
    sys.exit(1)
PY
}

# A one-off announcement, used by the installer to prove the hook works.
if [ "${1:-}" = "--hello" ]; then
  post "🟢 **$LABEL** fuzzing \`$commit\` — crash notifier armed."
  exit $?
fi

# Redeploying a fix should let previously-seen inputs report again: if the same
# input still crashes on new code, the fix did not work and that is worth
# hearing about. So the seen-list is scoped to the commit under test.
if [ -f "$STATE" ] && [ "$(head -1 "$STATE" 2>/dev/null)" != "commit:$commit" ]; then
  rm -f "$STATE"
fi
[ -f "$STATE" ] || echo "commit:$commit" > "$STATE"

# Housekeeping on the same cadence: a .NET minidump is written for every crash
# and nothing ever removes them. They are worth keeping for a native fault the
# crash input alone will not explain, but not without a bound — they reached
# several GiB on a box with 26 GiB free.
ls -t /tmp/plank-*crash*.dmp 2>/dev/null | tail -n +51 | xargs -r rm -f

[ -d "$CRASHES" ] || exit 0

# Refuse to report anything while a target binary is not loadable.
#
# This notifier outlives the fleet: deploy.sh kills afl-fuzz and Plank.Fuzzing,
# and this script's command line matches neither, so it keeps looping straight
# through a rollout. Its probes spawn the target, and a build rewrites Plank.dll
# in place (sharpfuzz instruments it after compiling), so a probe landing in that
# window dies with BadImageFormatException. Because a commit change also resets
# the seen-list, every stored input is re-tested right when that is most likely —
# and it reported two of them as fresh crashes on a tree where nothing
# reproduced.
#
# A healthy target is silent on a trivial input. If it is not, the build is
# broken or mid-write and this cycle has nothing trustworthy to say, so it exits
# without touching the seen-list and tries again in five minutes.
for bin in "$READER" "$WRITER"; do
  [ -x "$bin" ] || continue
  if probe="$("$bin" < /dev/null 2>&1)" && [ -z "$probe" ]; then
    continue
  fi
  echo "preflight failed for $(basename "$bin") — build broken or mid-write, skipping this cycle" >&2
  exit 0
done

new=()
for f in "$CRASHES"/*.bin; do
  [ -f "$f" ] || continue
  name="$(basename "$f")"
  grep -qxF "$name" "$STATE" || new+=("$f")
done
[ "${#new[@]}" -gt 0 ] || exit 0

declare -A seen_sig
lines=""
reported=0
for f in "${new[@]}"; do
  case "$(basename "$f")" in
    reader-*) bin="$READER" ;;
    writer-*) bin="$WRITER" ;;
    *) continue ;;
  esac
  [ -x "$bin" ] || continue

  out="$("$bin" < "$f" 2>&1)"
  # An input that no longer reproduces is not worth waking anyone for, but it is
  # still recorded so it is not re-checked forever.
  if [ -n "$out" ]; then
    # A loader failure says the assembly could not be read, never that this input
    # is interesting. Left out of the seen-list on purpose, so it is re-tested on
    # the next cycle rather than being written off as reported.
    case "$out" in
      *BadImageFormatException*|*FileLoadException*|*FileNotFoundException*|*TypeLoadException*|*MissingMethodException*|*MissingFieldException*)
        echo "skipping $(basename "$f"): loader failure, not an input defect" >&2
        continue
        ;;
    esac
    extype="$(printf '%s' "$out" | grep -oE 'Unhandled exception\. [A-Za-z0-9_.]+' | head -1 | sed 's/^Unhandled exception\. //')"
    frame="$(printf '%s' "$out" | grep -oE 'at Plank\.[A-Za-z0-9_.`<>+]+' | head -1 | sed 's/^at //')"
    sig="${extype:-unknown} @ ${frame:-unknown}"
    if [ -z "${seen_sig[$sig]:-}" ]; then
      seen_sig[$sig]=1
      lines="$lines"$'\n'"$sig"
    fi
    reported=$((reported + 1))
  fi
  basename "$f" >> "$STATE"
done

[ "$reported" -gt 0 ] || exit 0

post "🔴 **$LABEL** — ${reported} new crash input(s) on \`$commit\`, $(printf '%s' "${#seen_sig[@]}") signature(s):
\`\`\`
${lines# }
\`\`\`
Collect with \`./fuzz/collect-crashes.sh\` then \`./fuzz/triage.sh\`."
