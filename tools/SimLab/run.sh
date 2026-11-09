#!/bin/sh
# Usage: ./run.sh <fights.jsonl> <out_prefix> [extra SimFuzz args...]
# Runs every simulatable v0.111 fight in the file as $PROCS processes x $THREADS threads (default 4 x 6),
# merges to <out_prefix>.csv. Use fewer while the game is running on this PC.
cd "$(dirname "$0")"
in=$1; outp=$2; shift 2
PROCS=${PROCS:-4}; THREADS=${THREADS:-6}; BIN=${BIN:-bin/Release/net9.0/SimFuzz.dll}
start=$(date +%s)
i=0
while [ $i -lt $PROCS ]; do
  dotnet "$BIN" "$in" "$outp.s$i.csv" --act1cap 1000000 --totalcap 1000000 \
    --threads $THREADS --shard $i/$PROCS --report "$outp.s$i.report.txt" "$@" > "$outp.s$i.log" 2>&1 &
  i=$((i+1))
done
wait
end=$(date +%s)
head -1 "$outp.s0.csv" > "$outp.csv"
i=0
while [ $i -lt $PROCS ]; do tail -n +2 "$outp.s$i.csv" >> "$outp.csv"; rm -f "$outp.s$i.csv"; i=$((i+1)); done
grep -h "combats/s" "$outp".s*.log | sed 's/.*combats\/s=\([0-9.]*\).*/\1/' | awk '{s+=$1} END {print "combats/s (sum of shards) =", s}'
echo "wall_seconds=$((end-start))"
