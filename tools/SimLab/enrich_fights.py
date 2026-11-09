"""Adds run context the simulator needs to a fights JSONL, read from the ranker .run files.

Usage: python -P enrich_fights.py <runs_dir> <in.jsonl> <out.jsonl>

Per fight (node_index = index into the run's flattened map_point_history, all acts in order; the
row's encounter must equal that floor's room model_id, else the row is left unchanged):
  potions      potions the player used in this fight (player_stats.potion_used)
  relic_state  run-long relic state the simulator cannot know from the relic list alone:
               GIRYA            times lifted at earlier rest sites (LIFT)
               PUMPKIN_CANDLE   combats left lit: 5 when obtained + 5 per KINDLE, -1 per combat
               VENERABLE_TEA_SET / FAKE_VENERABLE_TEA_SET  1 if a rest site was visited since the
                                last combat (the relic arms on entering a rest site)
Single-player runs only (player_stats[0]).
"""
import json, os, sys

COMBAT = {"monster", "elite", "boss"}


def room_type(pt):
    rooms = pt.get("rooms") or []
    return rooms[0].get("room_type") if rooms else pt.get("map_point_type")


def context(pts, idx):
    lifts = 0
    candle = None
    rest_since_combat = False
    for i in range(idx):
        pt = pts[i]
        ps = pt["player_stats"][0]
        if candle is None and "RELIC.PUMPKIN_CANDLE" in json.dumps(ps):
            candle = 5
        for ch in ps.get("rest_site_choices") or []:
            if ch == "LIFT":
                lifts += 1
            elif ch == "KINDLE" and candle is not None:
                candle += 5
        rt = room_type(pt)
        if rt in COMBAT:
            rest_since_combat = False
            if candle is not None:
                candle = max(0, candle - 1)
        elif rt == "rest_site":
            rest_since_combat = True
    # a candle obtained on this very node (combat reward) is not lit for this fight
    return {"GIRYA": lifts, "PUMPKIN_CANDLE": candle or 0,
            "VENERABLE_TEA_SET": int(rest_since_combat), "FAKE_VENERABLE_TEA_SET": int(rest_since_combat)}


runs_dir, src, dst = sys.argv[1:4]
cache = {}
ok = bad = 0
with open(dst, "w", encoding="utf-8") as out:
    for line in open(src, encoding="utf-8"):
        r = json.loads(line)
        rid = r["run_id"]
        if rid not in cache:
            p = os.path.join(runs_dir, rid + ".run")
            cache.clear()
            cache[rid] = [pt for act in json.load(open(p, encoding="utf-8"))["map_point_history"] for pt in act] if os.path.exists(p) else None
        pts = cache[rid]
        pt = pts[r["node_index"]] if pts is not None and r["node_index"] < len(pts) else None
        if pt and pt.get("rooms") and pt["rooms"][0].get("model_id") == r["encounter"]:
            r["potions"] = list(pt["player_stats"][0].get("potion_used") or [])
            state = context(pts, r["node_index"])
            r["relic_state"] = {k: v for k, v in state.items() if "RELIC." + k in r["relics"]}
            ok += 1
        else:
            bad += 1
        out.write(json.dumps(r) + "\n")
print(f"matched {ok}, unmatched {bad}")
