//! Port of Catalog/Slug.cs: the game's ModelId naming (StringHelper.Slugify, ModelId.SlugifyCategory).
//!
//! The C# code uses the regex `([A-Za-z0-9]|\G(?!^))([A-Z])` replaced by `$1_$2`. Rust's regex crate has no `\G`,
//! so the same left-to-right scan is written out by hand.

fn camel_case(text: &str) -> String {
    let c: Vec<char> = text.chars().collect();
    let mut out = String::with_capacity(text.len() + 8);
    let mut p = 0;
    let mut last_end: Option<usize> = None;
    while p < c.len() {
        // Alternative 1: a letter/digit followed by an uppercase letter.
        if c[p].is_ascii_alphanumeric() && p + 1 < c.len() && c[p + 1].is_ascii_uppercase() {
            out.push(c[p]);
            out.push('_');
            out.push(c[p + 1]);
            p += 2;
            last_end = Some(p);
            continue;
        }
        // Alternative 2: right where the previous match ended (not at the start), an uppercase letter.
        if p != 0 && last_end == Some(p) && c[p].is_ascii_uppercase() {
            out.push('_');
            out.push(c[p]);
            p += 1;
            last_end = Some(p);
            continue;
        }
        out.push(c[p]);
        p += 1;
    }
    out
}

fn upper_invariant(s: &str) -> String {
    s.chars()
        .map(|ch| {
            let mut u = ch.to_uppercase();
            match (u.next(), u.next()) {
                (Some(x), None) => x,
                _ => ch,
            }
        })
        .collect()
}

pub fn slugify(text: &str) -> String {
    let snake = camel_case(text.trim());
    let upper = upper_invariant(&snake);
    // \s+ -> "_"
    let mut joined = String::with_capacity(upper.len());
    let mut in_space = false;
    for ch in upper.chars() {
        if ch.is_whitespace() {
            if !in_space {
                joined.push('_');
            }
            in_space = true;
        } else {
            joined.push(ch);
            in_space = false;
        }
    }
    joined.chars().filter(|ch| ch.is_ascii_uppercase() || ch.is_ascii_digit() || *ch == '_').collect()
}

pub fn slugify_category(type_name: &str) -> String {
    let text = slugify(type_name);
    match text.strip_suffix("_MODEL") {
        Some(s) => s.to_string(),
        None => text,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matches_the_game_regex() {
        // Expected values printed by the C# Slug.Slugify.
        for (input, want) in [
            ("StrikeDefect", "STRIKE_DEFECT"),
            ("ABCDef", "A_BC_DEF"),
            ("HTTPServer2Go", "H_TT_P_SERVER2_GO"),
            ("  Two Words  ", "TWO_WORDS"),
            ("Card`1", "CARD1"),
            ("aBC", "A_B_C"),
            ("ABC", "A_B_C"),
            ("X1Y2Z", "X1_Y2_Z"),
            ("DefectAAAAb", "DEFECT_AA_A_AB"),
        ] {
            assert_eq!(slugify(input), want, "{input}");
        }
        assert_eq!(slugify_category("CardPoolModel"), "CARD_POOL");
    }
}
