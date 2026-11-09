#!/bin/sh
# Usage: ./sweep.sh <fights.jsonl> <tag> <seeds> "<args set 1>" "<args set 2>" ...
# One line per setting: damage ratio by room type, death rates, encounter-demeaned spearman, speed.
in=$1; tag=$2; seeds=$3; shift 3
i=0
for a in "$@"; do
  i=$((i+1))
  ./run.sh "$in" "out/${tag}_$i" --seeds "$seeds" $a > "out/${tag}_$i.runlog" 2>&1
  cps=$(grep "combats/s" "out/${tag}_$i.runlog" | awk '{print $NF}')
  python -P summary.py "out/${tag}_$i.csv" "$a" "$cps"
done
