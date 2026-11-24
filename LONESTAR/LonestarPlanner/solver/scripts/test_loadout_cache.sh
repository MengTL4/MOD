#!/bin/bash
# Loadout study reuse through the service:
#   1. the same save asked twice (files differing only in the play timer): answered at once;
#   2. the units moved to the recommended layout (the game's save edited by make_moved_save.py: B's
#      recommendation must only move units already on the ship): answered from the study, "already the best";
#   3. the units moved back to the original layout: answered from the study, the recommendation again.
# usage: scripts/test_loadout_cache.sh <pre-battle A.jsonl> <A again (later save)> <pre-battle B.jsonl>
#   (regress.sh: data/loadout/e102_a, e102_b = two in-game saves a minute apart, colorful_e203 = 多彩号 vs 203)
set -u
cd "$(dirname "$0")/.."
B=tools/Brain/bin/Release/net10.0/Brain.dll
PORT=47399
OUT=out/cachetest
mkdir -p $OUT
dotnet $B --serve --port $PORT > $OUT/service.log 2>&1 &
SVC=$!
trap 'kill $SVC 2>/dev/null' EXIT
for i in $(seq 1 50); do grep -q "Brain service on" $OUT/service.log 2>/dev/null && break; sleep 0.2; done

ask() { # file -> reply line; prints "<seconds>s <summary>"
  local s=$(date +%s.%N)
  dotnet $B "$1" --ask-loadout --port $PORT > $OUT/reply.json
  local e=$(python -c "print(f'{$(date +%s.%N)-$s:.1f}')")
  python - "$OUT/reply.json" "$e" <<'PY'
import json, sys
r = json.loads(open(sys.argv[1], encoding='utf-8').read())
o, c = r.get('Outcome') or {}, r.get('CurrentOutcome') or {}
print(f"{sys.argv[2]}s ok={r['Ok']} isCurrent={r['IsCurrent']} best={o.get('Kind')}/r{o.get('Round')}/hp{o.get('Hp')} "
      f"current={c.get('Kind')}/r{c.get('Round')}/hp{c.get('Hp')}{' (at least)' if c.get('AtLeast') else ''} "
      f"entries={len(r['Entries'])} evaluated={r['Evaluated']} {r.get('Error') or ''}")
PY
}
fails=0
check() { # label, condition-description, grep pattern in the service log, expected count
  local n=$(grep -c "$3" $OUT/service.log)
  if [ "$n" -ge "$4" ]; then echo "  OK   $1"; else echo "  FAIL $1 ($2)"; fails=$((fails+1)); fi
}

echo "== A: full study"; ask "$1"
echo "== A again (later save of the same dock)"; ask "$2"
check "same save answered from the study" "no reuse" "same save and battle as the last study" 1

echo "== B: full study"; ask "$3"
cp $OUT/reply.json $OUT/replyB.json
python scripts/make_moved_save.py "$3" $OUT/replyB.json $OUT/moved_best.jsonl || exit 1
echo "== B with the recommended layout applied"; ask $OUT/moved_best.jsonl
check "moved units answered from the study" "no reuse" "units moved: answered from there" 1
python - $OUT/reply.json <<'PY' || fails=$((fails+1))
import json, sys
r = json.loads(open(sys.argv[1], encoding='utf-8').read())
assert r['IsCurrent'], "after applying the recommendation the current layout should be the best"
print("  OK   recommended layout now reported as current")
PY

echo "== B moved back to its original layout"; ask "$3"
check "moved back answered from the study" "no reuse" "units moved: answered from there" 2
python - $OUT/reply.json $OUT/replyB.json <<'PY' || fails=$((fails+1))
import json, sys
r = json.loads(open(sys.argv[1], encoding='utf-8').read())
b = json.loads(open(sys.argv[2], encoding='utf-8').read())
assert r['IsCurrent'] == b['IsCurrent'] and r['Outcome'] == b['Outcome'], "moved back: should answer as the first study did"
print("  OK   moved back: same answer as the first study")
PY

echo
[ $fails -eq 0 ] && echo "CACHE OK" || echo "CACHE FAILED ($fails)"
exit $fails
