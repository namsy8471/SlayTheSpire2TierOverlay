//! The game's RNG and seed arithmetic (`GameRng`, `RngTape`, `RngSeeds`, `XxHash64`): xoshiro256** seeded by
//! SplitMix64, and the two seed schemes (32-bit legacy hash before v0.109, xxHash64 after).

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum RngScheme {
    /// Game v0.107 and earlier (save schema <= 18).
    Legacy32,
    /// Game v0.109 and later (save schema 19+).
    Xx64,
}

pub const FIRST_XX64_SAVE_SCHEMA: i32 = 19;
pub const FIRST_XX64_VERSION: &str = "v0.109.0";
pub const DEFAULT_SCHEME: RngScheme = RngScheme::Xx64;
pub const MIGRATED_PREFIX: &str = "old";

pub fn for_save_schema(schema_version: i32) -> Option<RngScheme> {
    if schema_version <= 0 {
        None
    } else if schema_version >= FIRST_XX64_SAVE_SCHEMA {
        Some(RngScheme::Xx64)
    } else {
        Some(RngScheme::Legacy32)
    }
}

pub fn for_game_version(version: Option<&str>) -> RngScheme {
    match (parse_version(version), parse_version(Some(FIRST_XX64_VERSION))) {
        (Some(v), Some(first)) => {
            if v >= first {
                RngScheme::Xx64
            } else {
                RngScheme::Legacy32
            }
        }
        _ => DEFAULT_SCHEME,
    }
}

pub fn is_migrated(seed: &str) -> bool {
    seed.starts_with(MIGRATED_PREFIX)
}

pub fn hash(scheme: RngScheme, text: &str) -> u64 {
    match scheme {
        RngScheme::Legacy32 => legacy_hash(text) as u32 as u64,
        RngScheme::Xx64 => xxhash64(text.as_bytes(), 0),
    }
}

pub fn run_seed(scheme: RngScheme, seed: &str) -> u64 {
    match scheme {
        RngScheme::Legacy32 => legacy_hash(seed) as u32 as u64,
        RngScheme::Xx64 => match seed.strip_prefix(MIGRATED_PREFIX) {
            Some(rest) => legacy_hash(rest) as u32 as u64,
            None => xxhash64(seed.as_bytes(), 0),
        },
    }
}

pub fn named(scheme: RngScheme, seed: u64, name: &str) -> u64 {
    match scheme {
        RngScheme::Legacy32 => (seed as u32).wrapping_add(legacy_hash(name) as u32) as u64,
        RngScheme::Xx64 => seed.wrapping_add(xxhash64(name.as_bytes(), 0)),
    }
}

pub fn run_stream(scheme: RngScheme, seed: &str, name: &str) -> u64 {
    named(scheme, run_seed(scheme, seed), name)
}

pub fn player_seed(scheme: RngScheme, seed: &str, slot: i32) -> u64 {
    if scheme == RngScheme::Xx64 && is_migrated(seed) {
        return player_seed(RngScheme::Legacy32, &seed[MIGRATED_PREFIX.len()..], slot);
    }
    match scheme {
        RngScheme::Legacy32 => legacy_hash(seed).wrapping_add(slot) as u32 as u64,
        RngScheme::Xx64 => xxhash64(seed.as_bytes(), 0).wrapping_add(slot as i64 as u64),
    }
}

pub fn player_stream(scheme: RngScheme, seed: &str, slot: i32, name: &str) -> u64 {
    if scheme == RngScheme::Xx64 && is_migrated(seed) {
        return player_stream(RngScheme::Legacy32, &seed[MIGRATED_PREFIX.len()..], slot, name);
    }
    named(scheme, player_seed(scheme, seed, slot), name)
}

pub fn event_stream(scheme: RngScheme, seed: &str, slot: i32, event_id: &str, shared: bool) -> u64 {
    let entry = event_id.strip_prefix("EVENT.").unwrap_or(event_id);
    let run = run_seed(scheme, seed);
    let add = if shared { 0 } else { slot };
    match scheme {
        // uint + uint wraps at 32 bits in C#.
        RngScheme::Legacy32 => ((run as u32 as i32).wrapping_add(add) as u32).wrapping_add(legacy_hash(entry) as u32) as u64,
        RngScheme::Xx64 => ((run as i64).wrapping_add(add as i64) as u64).wrapping_add(xxhash64(entry.as_bytes(), 0)),
    }
}

/// The game's 32-bit string hash over UTF-16 code units (two interleaved djb2 lanes).
pub fn legacy_hash(s: &str) -> i32 {
    let units: Vec<u16> = s.encode_utf16().collect();
    let (mut a, mut b): (i32, i32) = (352654597, 352654597);
    let mut i = 0;
    while i < units.len() {
        a = (a.wrapping_shl(5).wrapping_add(a)) ^ units[i] as i32;
        if i == units.len() - 1 {
            break;
        }
        b = (b.wrapping_shl(5).wrapping_add(b)) ^ units[i + 1] as i32;
        i += 2;
    }
    a.wrapping_add(b.wrapping_mul(1566083941))
}

/// `v0.111.0`, `0.113`, `v0.107.1-beta` -> (major, minor, patch); None when it is not a version.
pub fn parse_version(text: Option<&str>) -> Option<(i32, i32, i32)> {
    let t = text?.trim();
    if t.is_empty() {
        return None;
    }
    let mut s = t.trim_start_matches(['v', 'V']);
    if let Some(cut) = s.find(['-', '+', ' ']) {
        s = &s[..cut];
    }
    let parts: Vec<&str> = s.split('.').collect();
    if !(2..=4).contains(&parts.len()) {
        return None;
    }
    let mut n = [0; 3];
    for (i, p) in parts.iter().enumerate() {
        // NumberStyles.None: digits only.
        if p.is_empty() || !p.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        let x: i32 = p.parse().ok()?;
        if i < 3 {
            n[i] = x;
        }
    }
    Some((n[0], n[1], n[2]))
}

const P1: u64 = 11400714785074694791;
const P2: u64 = 14029467366897019727;
const P3: u64 = 1609587929392839161;
const P4: u64 = 9650029242287828579;
const P5: u64 = 2870177450012600261;

fn round(acc: u64, input: u64) -> u64 {
    acc.wrapping_add(input.wrapping_mul(P2)).rotate_left(31).wrapping_mul(P1)
}

fn merge(acc: u64, lane: u64) -> u64 {
    (acc ^ round(0, lane)).wrapping_mul(P1).wrapping_add(P4)
}

fn u64le(d: &[u8]) -> u64 {
    u64::from_le_bytes(d[..8].try_into().unwrap())
}

/// xxHash64 of UTF-8 bytes.
pub fn xxhash64(data: &[u8], seed: u64) -> u64 {
    let len = data.len();
    let mut i = 0;
    let mut h;
    if len >= 32 {
        let (mut v1, mut v2, mut v3, mut v4) = (seed.wrapping_add(P1).wrapping_add(P2), seed.wrapping_add(P2), seed, seed.wrapping_sub(P1));
        while i + 32 <= len {
            v1 = round(v1, u64le(&data[i..]));
            v2 = round(v2, u64le(&data[i + 8..]));
            v3 = round(v3, u64le(&data[i + 16..]));
            v4 = round(v4, u64le(&data[i + 24..]));
            i += 32;
        }
        h = v1.rotate_left(1).wrapping_add(v2.rotate_left(7)).wrapping_add(v3.rotate_left(12)).wrapping_add(v4.rotate_left(18));
        h = merge(h, v1);
        h = merge(h, v2);
        h = merge(h, v3);
        h = merge(h, v4);
    } else {
        h = seed.wrapping_add(P5);
    }
    h = h.wrapping_add(len as u64);
    while i + 8 <= len {
        h = (h ^ round(0, u64le(&data[i..]))).rotate_left(27).wrapping_mul(P1).wrapping_add(P4);
        i += 8;
    }
    if i + 4 <= len {
        let w = u32::from_le_bytes(data[i..i + 4].try_into().unwrap()) as u64;
        h = (h ^ w.wrapping_mul(P1)).rotate_left(23).wrapping_mul(P2).wrapping_add(P3);
        i += 4;
    }
    while i < len {
        h = (h ^ (data[i] as u64).wrapping_mul(P5)).rotate_left(11).wrapping_mul(P1);
        i += 1;
    }
    h ^= h >> 33;
    h = h.wrapping_mul(P2);
    h ^= h >> 29;
    h = h.wrapping_mul(P3);
    h ^= h >> 32;
    h
}

/// xoshiro256** as the game uses it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct GameRng {
    s: [u64; 4],
    pub counter: i32,
}

fn split_mix64(x: &mut u64) -> u64 {
    *x = x.wrapping_add(11400714819323198485);
    let mut z = *x;
    z = (z ^ (z >> 30)).wrapping_mul(13787848793156543929);
    z = (z ^ (z >> 27)).wrapping_mul(10723151780598845931);
    z ^ (z >> 31)
}

impl GameRng {
    pub fn new(seed: u64) -> Self {
        let mut x = seed;
        let s = [split_mix64(&mut x), split_mix64(&mut x), split_mix64(&mut x), split_mix64(&mut x)];
        Self { s, counter: 0 }
    }

    pub fn named(scheme: RngScheme, seed: u64, name: &str) -> Self {
        Self::new(named(scheme, seed, name))
    }

    pub fn state(&self) -> (u64, u64, u64, u64) {
        (self.s[0], self.s[1], self.s[2], self.s[3])
    }

    pub fn next_u64(&mut self) -> u64 {
        self.counter += 1;
        let s = &mut self.s;
        let result = s[1].wrapping_mul(5).rotate_left(7).wrapping_mul(9);
        let t = s[1] << 17;
        s[2] ^= s[0];
        s[3] ^= s[1];
        s[1] ^= s[2];
        s[0] ^= s[3];
        s[2] ^= t;
        s[3] = s[3].rotate_left(45);
        result
    }

    pub fn to_double(raw: u64) -> f64 {
        (raw >> 11) as f64 * 1.1102230246251565E-16
    }

    pub fn to_float(raw: u64) -> f32 {
        Self::to_double(raw) as f32
    }

    pub fn to_int(raw: u64, max_exclusive: i32) -> i32 {
        (Self::to_double(raw) * max_exclusive as f64) as i32
    }

    pub fn next_double(&mut self) -> f64 {
        Self::to_double(self.next_u64())
    }

    pub fn next_float(&mut self) -> f32 {
        Self::to_float(self.next_u64())
    }

    pub fn next_int(&mut self, max_exclusive: i32) -> i32 {
        Self::to_int(self.next_u64(), max_exclusive)
    }

    /// Fisher-Yates from the back. From v0.113 a shuffle uses a child stream seeded by one parent draw.
    pub fn shuffle<T>(&mut self, list: &mut [T], use_child_stream: bool) {
        let mut child;
        let stream = if use_child_stream {
            child = GameRng::new(self.next_u64());
            &mut child
        } else {
            self
        };
        let mut n = list.len();
        while n > 1 {
            n -= 1;
            let k = stream.next_int(n as i32 + 1) as usize;
            list.swap(n, k);
        }
    }
}

/// Draws of one stream by counter, generated on demand.
pub struct RngTape {
    rng: GameRng,
    values: Vec<u64>,
}

impl RngTape {
    pub fn new(rng: GameRng) -> Self {
        Self { rng, values: Vec::new() }
    }

    pub fn at(&mut self, counter: usize) -> u64 {
        while self.values.len() <= counter {
            self.values.push(self.rng.next_u64());
        }
        self.values[counter]
    }

    /// First counter in [from, to) whose float equals `value`, or -1.
    pub fn find_float(&mut self, value: f32, from: i32, to: i32) -> i32 {
        for c in from.max(0)..to {
            if GameRng::to_float(self.at(c as usize)) == value {
                return c;
            }
        }
        -1
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn xxhash64_known_values() {
        // Reference values of the xxHash64 specification.
        assert_eq!(xxhash64(b"", 0), 0xEF46DB3751D8E999);
        assert_eq!(xxhash64(b"a", 0), 0xD24EC4F1A98C6E5B);
        assert_eq!(xxhash64(b"abc", 0), 0x44BC2CF5AD770999);
        assert_eq!(xxhash64(b"Nobody inspects the spammish repetition", 0), 0xFBCEA83C8A378BF1);
    }

    #[test]
    fn parse_version_forms() {
        assert_eq!(parse_version(Some("v0.111.0")), Some((0, 111, 0)));
        assert_eq!(parse_version(Some("0.113")), Some((0, 113, 0)));
        assert_eq!(parse_version(Some("v0.107.1-beta")), Some((0, 107, 1)));
        assert_eq!(parse_version(Some("x")), None);
        assert_eq!(for_game_version(Some("v0.107.1")), RngScheme::Legacy32);
    }
}
