#!/bin/bash
# Regression suite for the headless engine + Brain (run from anywhere; ~10 min, all cores).
#   1. replay fidelity: every recorded snapshot must be reproduced exactly
#   2. snapshot restore: the future after a restore must equal the first run
#   3. smoke test: every enemy in the game (and every bounty battle event) starts, reaches the player's turn and finishes a greedy battle
#   4. Brain self-play on recorded battles (win / hp / rounds)
#   5. loadout study reuse (same save again; units moved) through the service
# SKIP_SMOKE=1 skips step 3.
set -u
cd "$(dirname "$0")/.."
JOBS=${JOBS:-$(nproc)}
dotnet build tools/Replay -c Release -v q -nologo >/dev/null && \
dotnet build tools/SnapTest -c Release -v q -nologo >/dev/null && \
dotnet build tools/Brain -c Release -v q -nologo >/dev/null || { echo "BUILD FAILED"; exit 1; }
# real battles recorded in the game (with embedded save files)
recs="20261018_185417_995_b0s0 20261018_185614_915_b0s1 20261018_185754_920_b0s2
20261022_193849_142_b0s0 20261022_194039_165_b0s1 20261022_194207_754_b0s2 20261022_220547_103_b0s3
20261022_220720_890_b1s0 20261022_221022_146_b1s1 20261022_221241_624_b1s2 20261022_221444_519_b1s3
20261022_221726_425_b2s0 20261022_221853_967_b2s1 20261022_222034_583_b2s2
20261023_014755_134_b0s0"
# those that reach round 2 (restore is tested from there)
restore="20261018_185417_995_b0s0 20261018_185614_915_b0s1 20261018_185754_920_b0s2 20261022_193849_142_b0s0
20261022_220547_103_b0s3 20261022_221022_146_b1s1 20261022_222034_583_b2s2 20261023_014755_134_b0s0"
# ships: 护盾号 (2026-10-18), 空间号 (2026-10-22), 多彩号 (2026-10-23, paint)
selfplay="20261018_185417_995_b0s0 20261018_185614_915_b0s1 20261018_185754_920_b0s2 20261022_221022_146_b1s1 20261022_222034_583_b2s2 20261023_014755_134_b0s0"
tmp=$(mktemp -d)
fail=0

echo "== replay"
for r in $recs; do
  (dotnet tools/Replay/bin/Release/net10.0/Replay.dll data/recordings/$r.jsonl 2>&1 | grep "Identical snapshots" > "$tmp/$r.replay") &
  while [ "$(jobs -r | wc -l)" -ge $((JOBS / 2)) ]; do sleep 0.2; done
done
wait
for r in $recs; do
  line=$(cat "$tmp/$r.replay")
  echo "  $r: $line"
  n=$(echo "$line" | sed 's/.*: \([0-9]*\)\/\([0-9]*\)/\1 \2/'); set -- $n
  [ -n "${1:-}" ] && [ "$1" = "${2:-}" ] || fail=1
done

echo "== snapshot restore"
for r in $restore; do
  (dotnet tools/SnapTest/bin/Release/net10.0/SnapTest.dll data/recordings/$r.jsonl --turn 2 2>&1 | grep -E "^PASS|^FAIL" > "$tmp/$r.snap") &
  while [ "$(jobs -r | wc -l)" -ge $((JOBS / 2)) ]; do sleep 0.2; done
done
wait
for r in $restore; do
  res=$(cat "$tmp/$r.snap")
  echo "  $r: $res"
  [ "$res" = "PASS" ] || fail=1
done

if [ "${SKIP_SMOKE:-0}" != 1 ]; then
  echo "== smoke test (every enemy)"
  res=$(dotnet tools/Brain/bin/Release/net10.0/Brain.dll data/recordings/20261022_222034_583_b2s2.jsonl --smoke --mergecheck --jobs $JOBS 2>&1 | tail -4)
  echo "$res" | sed 's/^/  /'
  echo "$res" | grep -q "^SMOKE OK" || fail=1
  echo "== smoke test (every bounty battle event; 空间号 and 多彩号 saves)"
  for r in 20261022_222034_583_b2s2 20261023_014755_134_b0s0; do
    res=$(dotnet tools/Brain/bin/Release/net10.0/Brain.dll data/recordings/$r.jsonl --smoke-events --mergecheck --jobs $JOBS 2>&1 | tail -3)
    echo "$res" | sed "s/^/  $r: /"
    echo "$res" | grep -q "^SMOKE OK" || fail=1
  done
fi

echo "== brain self-play"
for r in $selfplay; do
  res=$(dotnet tools/Brain/bin/Release/net10.0/Brain.dll data/recordings/$r.jsonl --play --jobs $JOBS 2>&1 | grep -E "RESULT|^total" | tr '\n' ' ')
  echo "  $r: $res"
  echo "$res" | grep -q VICTORY || fail=1
done
echo "== loadout study reuse"
scripts/test_loadout_cache.sh data/loadout/e102_a.jsonl data/loadout/e102_b.jsonl data/loadout/colorful_e203.jsonl | sed "s/^/  /" | grep -E "OK|FAIL|^  [0-9.]+s"
[ ${PIPESTATUS[0]} = 0 ] || fail=1
rm -rf "$tmp"
[ $fail = 0 ] && echo "ALL PASS" || echo "SOMETHING FAILED"
exit $fail
