# Profiling helpers

How the speed work in `../README.md` was measured. None of these read fight data.

- **VTune** (Intel oneAPI): `vtune -collect hotspots -knob sampling-mode=sw -r out/vt -- dotnet bin/Release/net9.0/SimFuzz.dll <fights.jsonl> out/vt.csv --threads 4 ...`, then
  `vtune -report top-down -r out/vt -column "CPU Time:Total,CPU Time:Self,Module" -format csv -csv-delimiter tab -report-output out/vt_td.tsv`.
  - `python vt_categories.py out/vt_td.tsv` splits self time inside `CombatRunner.RunOne` into
    non-overlapping buckets (listener plumbing by kind, hook dispatch loops, clone, map generation,
    eval, other engine work). Use this, not inclusive time: "card execution 44%" turned out to be
    mostly hook plumbing once self time was attributed.
  - `python vt_incl.py out/vt_td.tsv [name-filter]` lists inclusive time per function (same tsv
    without the Self/Module columns also works).
- **Allocations**: `dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x1:5 -o out/alloc.nettrace -- dotnet bin/Release/net9.0/SimFuzz.dll ...`
  then `dotnet run -c Release --project allocrep -- out/alloc.nettrace`: sampled bytes by type and by
  the first simulator frame on the stack, plus GC counts by generation and reason.
- Every SimFuzz run also prints `gc: allocated=..GB pause=..s gen0= gen1= gen2=`.
- Relative timing on a busy machine: alternate the old and new binary on the same small set and
  compare process CPU time, not wall time.
