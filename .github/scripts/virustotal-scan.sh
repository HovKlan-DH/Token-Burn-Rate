#!/usr/bin/env bash
#
# Uploads release files to VirusTotal and reports what its antivirus engines make of them.
#
#   virustotal-scan.sh <report|gate> <file>...
#
#   report  scan and list the results. Never fails, so a release always goes ahead - the
#           results are there to read on the run's summary page.
#   gate    the same, but exits non-zero when a file's detections cross the thresholds
#           below, so a job that depends on this one (the release) does not run.
#
# Needs VT_API_KEY in the environment: the API key of a (free) VirusTotal account.
#
# The free API's limits shape the whole script. It allows 4 requests a minute and 500 a
# day, so every call is spaced VT_MIN_INTERVAL seconds apart and a pending analysis is
# polled at most once a minute. A plain upload only takes files up to 32 MB and every
# release file is around 50 MB, so each one goes through the large-file upload URL instead,
# which takes up to 650 MB.
#
# Tunable through the environment:
#   VT_MAX_MALICIOUS    gate fails when this many engines call a file malicious (default 3).
#                       New, rarely downloaded apps routinely draw one or two generic hits
#                       from machine-learning engines, which a threshold of 1 would turn
#                       into a blocked release every time.
#   VT_MAJOR_ENGINES    comma-separated engines whose malicious verdict alone fails the gate,
#                       whatever the count - the ones a large share of users actually run.
#   VT_TIMEOUT_MINUTES  how long to wait for analyses after the last upload (default 30).
#                       A file still unfinished then is reported as a warning, not a failure,
#                       so VirusTotal being slow never blocks a release.
#   VT_BUDGET_MINUTES   the most the whole script may take (default 45), retries and all.
#                       Kept below the workflow step's own timeout, so the script always ends
#                       itself - with a result - rather than being killed without one.
#   VT_MIN_INTERVAL     seconds between API calls (default 15, i.e. 4 a minute).

set -uo pipefail

mode="${1:-}"
case "$mode" in
  report|gate) shift ;;
  *) echo "usage: $0 <report|gate> <file>..." >&2; exit 2 ;;
esac
if (( $# == 0 )); then
  echo "usage: $0 <report|gate> <file>..." >&2
  exit 2
fi

api=https://www.virustotal.com/api/v3
max_malicious="${VT_MAX_MALICIOUS:-3}"
major_engines="${VT_MAJOR_ENGINES:-Microsoft,Kaspersky,ESET-NOD32,BitDefender,Avast,AVG,Symantec,Sophos}"
timeout_minutes="${VT_TIMEOUT_MINUTES:-30}"
budget_minutes="${VT_BUDGET_MINUTES:-45}"
min_interval="${VT_MIN_INTERVAL:-15}"
poll_interval=60
summary="${GITHUB_STEP_SUMMARY:-/dev/stdout}"

# No API call starts after this, whatever it is for - see VT_BUDGET_MINUTES.
hard_deadline=$(( $(date +%s) + budget_minutes * 60 ))

# A problem with VirusTotal itself rather than a finding: no key, the API down, the daily
# quota spent. It blocks the release only in gate mode - report mode must never be the
# reason a release does not go out.
give_up() {
  if [[ $mode == gate ]]; then
    echo "::error::VirusTotal: $1"
    exit 1
  fi
  echo "::warning::VirusTotal: $1 - scan abandoned, release not affected (report mode)"
  exit 0
}

[[ -n "${VT_API_KEY:-}" ]] || give_up "no VT_API_KEY secret is set"

for file in "$@"; do
  [[ -f $file ]] || give_up "file not found: $file"
done

# Waits until min_interval seconds have passed since the previous call started.
last_call=0
pace() {
  local wait=$(( last_call + min_interval - $(date +%s) ))
  if (( wait > 0 )); then sleep "$wait"; fi
  last_call=$(date +%s)
}

# One API call; the arguments after the first (a description for messages) go to curl. The
# response body lands in $resp rather than on stdout: calling this inside $( ) would run it
# in a subshell, whose update to last_call the next call would never see - and the pacing
# would silently stop working.
#
# A rate-limit answer (429) is retried after a minute, a server error or dropped connection
# after half a minute; anything else - a bad key, a rejected file - fails at once. So does
# any attempt that would start past the budget, and each call is capped at 5 minutes - an
# upload of ~50 MB takes seconds - so a hung connection cannot outlast it by much.
resp=""
vt() {
  local what=$1 attempt out code
  shift
  for attempt in 1 2 3 4 5; do
    if (( $(date +%s) >= hard_deadline )); then
      echo "$what: out of time ($budget_minutes-minute budget spent)" >&2
      return 1
    fi
    pace
    out=$(curl -sS --max-time 300 -w $'\n%{http_code}' -H "x-apikey: $VT_API_KEY" "$@") || out=$'\n000'
    code=${out##*$'\n'}
    resp=${out%$'\n'*}
    case "$code" in
      2??) return 0 ;;
      429) echo "Rate limit hit while $what, waiting a minute (attempt $attempt)..." >&2; sleep 60 ;;
      000|5??) echo "$what failed (HTTP $code), retrying (attempt $attempt)..." >&2; sleep 30 ;;
      *) echo "$what failed (HTTP $code): $resp" >&2; return 1 ;;
    esac
  done
  return 1
}

names=()
shas=()
ids=()
results=()

for file in "$@"; do
  name=$(basename "$file")
  sha=$(sha256sum "$file" | cut -d' ' -f1)

  vt "requesting an upload URL for $name" "$api/files/upload_url" \
    || give_up "could not get an upload URL for $name"
  url=$(jq -r '.data // empty' <<<"$resp")
  [[ -n $url ]] || give_up "no upload URL in the answer for $name"

  vt "uploading $name" -F "file=@$file" "$url" \
    || give_up "could not upload $name"
  id=$(jq -r '.data.id // empty' <<<"$resp")
  [[ -n $id ]] || give_up "no analysis id in the answer for $name"

  echo "Uploaded $name ($(( $(stat -c %s "$file") / 1048576 )) MB, sha256 $sha)"
  names+=("$name")
  shas+=("$sha")
  ids+=("$id")
  results+=("")
done

# Polls every unfinished analysis in turn until all are done or the time is up. Polling
# stops two minutes short of the budget, so a last round's rate-limit retries still end
# inside it and the files that did not finish are reported as unfinished rather than as a
# failed call.
deadline=$(( $(date +%s) + timeout_minutes * 60 ))
if (( deadline > hard_deadline - 120 )); then deadline=$(( hard_deadline - 120 )); fi
pending=("${!ids[@]}")

while (( ${#pending[@]} > 0 )) && (( $(date +%s) < deadline )); do
  round_start=$(date +%s)
  still=()

  for i in "${pending[@]}"; do
    if (( $(date +%s) >= deadline )); then still+=("$i"); continue; fi

    vt "checking ${names[i]}" "$api/analyses/${ids[i]}" \
      || give_up "could not read the analysis of ${names[i]}"

    if [[ $(jq -r '.data.attributes.status' <<<"$resp") == completed ]]; then
      results[i]=$resp
      echo "Analysis of ${names[i]} finished"
    else
      still+=("$i")
    fi
  done

  pending=("${still[@]}")

  wait=$(( round_start + poll_interval - $(date +%s) ))
  if (( ${#pending[@]} > 0 && wait > 0 )); then sleep "$wait"; fi
done

# Verdict per file. Everything is written to the run's summary page, and every file with any
# detection also gets an annotation, so a hit is visible without opening the summary.
blocked=0

{
  echo "### VirusTotal scan ($mode mode)"
  echo
  echo "Gate fails at $max_malicious or more malicious verdicts, or any malicious verdict from: ${major_engines//,/, }."
  echo
  echo "| File | Result | Malicious | Suspicious | Engines | Report |"
  echo "|---|---|---|---|---|---|"
} >>"$summary"

details=""

for i in "${!ids[@]}"; do
  name=${names[i]}
  link="https://www.virustotal.com/gui/file/${shas[i]}"
  r=${results[i]}

  if [[ -z $r ]]; then
    echo "::warning::VirusTotal: analysis of $name did not finish in time - check the report later: $link"
    echo "| $name | not finished | - | - | - | [report]($link) |" >>"$summary"
    continue
  fi

  malicious=$(jq '.data.attributes.stats.malicious // 0' <<<"$r")
  suspicious=$(jq '.data.attributes.stats.suspicious // 0' <<<"$r")
  engines=$(jq '[.data.attributes.stats[]] | add // 0' <<<"$r")
  major_hits=$(jq -r --arg majors "$major_engines" '
    ($majors | split(",")) as $m
    | [.data.attributes.results | to_entries[]
       | select(.value.category == "malicious" and (.key as $k | any($m[]; . == $k)))
       | .key] | join(", ")' <<<"$r")
  flagged=$(jq -r '
    .data.attributes.results | to_entries[]
    | select(.value.category == "malicious" or .value.category == "suspicious")
    | "- \(.key): \(.value.result // "no name") (\(.value.category))"' <<<"$r")

  if (( malicious >= max_malicious )) || [[ -n $major_hits ]]; then
    result="**blocks the gate**"
    blocked=1
    reason="$malicious malicious"
    [[ -n $major_hits ]] && reason+=", including $major_hits"
    level=$([[ $mode == gate ]] && echo error || echo warning)
    echo "::$level::VirusTotal: $name - $reason - $link"
  elif (( malicious > 0 || suspicious > 0 )); then
    result="below threshold"
    echo "::warning::VirusTotal: $name - $malicious malicious, $suspicious suspicious (below the gate's threshold) - $link"
  else
    result="clean"
  fi

  echo "| $name | $result | $malicious | $suspicious | $engines | [report]($link) |" >>"$summary"
  echo "$name: $result ($malicious malicious, $suspicious suspicious, $engines engines)"
  [[ -n $flagged ]] && details+=$'\n'"**$name**"$'\n'"$flagged"$'\n'
done

if [[ -n $details ]]; then
  { echo; echo "#### Engines that flagged a file"; echo "$details"; } >>"$summary"
fi

if (( blocked )) && [[ $mode == gate ]]; then
  echo "::error::VirusTotal: detections over the threshold - the release is stopped. Check the reports; if it is a false positive, re-run the release with VirusTotal set to 'report'."
  exit 1
fi

exit 0
