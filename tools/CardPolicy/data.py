"""Loading pick-export output (screens.jsonl) into padded numpy arrays for choice models.

Layout per screen: column 0 = Skip, columns 1..K = offered cards in offer order (K <= MAX_CARDS), the rest padding.
Players come from the corpus runs_index.csv (player_key, sample); folds are by player so no player's runs sit on both
sides of a split (docs/research/LEARNED_CARD_POLICY_PLAN.md section 4.4).
"""
import csv
import json
import random

import numpy as np

MAX_CARDS = 5  # screens with more options are merged reward screens the run file cannot split


def load_index(path):
    with open(path, encoding='utf-8') as f:
        return {r['runId']: r for r in csv.DictReader(f)}


def load_screens(path, index, contexts=None):
    """Screens with at most MAX_CARDS options whose run is in the index (optionally only some contexts)."""
    screens = []
    with open(path, encoding='utf-8') as f:
        for line in f:
            s = json.loads(line)
            if len(s['options']) > MAX_CARDS or s['run_id'] not in index:
                continue
            if contexts and s['context'] not in contexts:
                continue
            meta = index[s['run_id']]
            s['player'] = meta['player_key']
            s['sample'] = meta['sample']
            screens.append(s)
    return screens


def player_folds(screens, k=5, seed=0):
    """Fold number per screen: players shuffled with a fixed seed and dealt round-robin into k folds."""
    players = sorted({s['player'] for s in screens})
    random.Random(seed).shuffle(players)
    fold_of = {p: i % k for i, p in enumerate(players)}
    return np.array([fold_of[s['player']] for s in screens])


class Arrays:
    """Padded per-option arrays. `cards` holds a vocabulary index (-1 for Skip and padding)."""

    def __init__(self, screens, vocab=None):
        n = len(screens)
        width = MAX_CARDS + 1
        self.vocab = vocab if vocab is not None else {c: i for i, c in enumerate(sorted({o['card'] for s in screens for o in s['options']}))}
        self.mask = np.zeros((n, width), bool)
        self.total = np.zeros((n, width))          # engine total as shipped (skip column: skip total)
        self.base_adjust = np.zeros((n, width))    # pack baseAdjust inside each card's total
        self.cards = np.full((n, width), -1, int)
        self.act = np.zeros(n, int)
        self.y = np.zeros(n, int)                  # 0 = skip, j = offered card j-1
        self.engine = np.zeros(n, int)             # engine's recommendation, same coding
        self.win = np.zeros(n, bool)
        self.recent = np.zeros(n, bool)            # natural sample (not an added win)
        for i, s in enumerate(screens):
            self.mask[i, 0] = True
            self.total[i, 0] = s['skip_total']
            for j, o in enumerate(s['options'], start=1):
                self.mask[i, j] = True
                self.total[i, j] = o['total']
                self.base_adjust[i, j] = o.get('base_adjust', 0.0)
                self.cards[i, j] = self.vocab.get(o['card'], -1)
            self.act[i] = min(max(s['act'], 1), 3) - 1
            self.y[i] = s['pick'] + 1
            self.engine[i] = s['engine_best'] + 1
            self.win[i] = s['win']
            self.recent[i] = s['sample'] == 'recent'

    def subset(self, rows):
        out = object.__new__(Arrays)
        out.vocab = self.vocab
        for name in ('mask', 'total', 'base_adjust', 'cards', 'act', 'y', 'engine', 'win', 'recent'):
            setattr(out, name, getattr(self, name)[rows])
        return out
