//! Port of Glob.cs: a tiny case-insensitive glob for '/'-separated pack paths.
//! `*` = any characters but '/', `**` = any characters ("**/" also matches zero folders), `?` = one character but '/'.
//! A pattern without wildcards is a case-insensitive substring filter.

#[derive(Clone, Debug)]
enum Tok {
    Lit(char),
    Star,
    DoubleStar,
    /// "**/": nothing, or anything ending in '/'.
    AnyDirs,
    One,
}

#[derive(Clone, Debug)]
pub struct Glob {
    pattern: String,
    toks: Option<Vec<Tok>>,
}

fn fold(c: char) -> char {
    c.to_lowercase().next().unwrap_or(c)
}

impl Glob {
    pub fn new(pattern: &str) -> Glob {
        let pattern = pattern.replace('\\', "/");
        let toks = pattern.contains(['*', '?']).then(|| {
            let c: Vec<char> = pattern.chars().collect();
            let mut t = Vec::new();
            let mut i = 0;
            while i < c.len() {
                if c[i] == '*' && i + 1 < c.len() && c[i + 1] == '*' {
                    let slash = i + 2 < c.len() && c[i + 2] == '/';
                    t.push(if slash { Tok::AnyDirs } else { Tok::DoubleStar });
                    i += if slash { 3 } else { 2 };
                    continue;
                }
                t.push(match c[i] {
                    '*' => Tok::Star,
                    '?' => Tok::One,
                    ch => Tok::Lit(fold(ch)),
                });
                i += 1;
            }
            t
        });
        Glob { pattern, toks }
    }

    pub fn is_match(&self, path: &str) -> bool {
        match &self.toks {
            None => path.to_lowercase().contains(&self.pattern.to_lowercase()),
            Some(t) => {
                let p: Vec<char> = path.chars().map(fold).collect();
                matches(t, &p)
            }
        }
    }

    /// The literal folder part in front of the first wildcard ("localization/" for "localization/**/*.json").
    pub fn literal_directory_prefix(&self) -> String {
        if self.toks.is_none() {
            return String::new();
        }
        let first = self.pattern.find(['*', '?']).unwrap_or(self.pattern.len());
        match self.pattern[..first].rfind('/') {
            Some(i) => self.pattern[..=i].to_string(),
            None => String::new(),
        }
    }
}

fn matches(t: &[Tok], p: &[char]) -> bool {
    match t.first() {
        None => p.is_empty(),
        Some(Tok::Lit(c)) => p.first() == Some(c) && matches(&t[1..], &p[1..]),
        Some(Tok::One) => p.first().is_some_and(|&c| c != '/') && matches(&t[1..], &p[1..]),
        Some(Tok::Star) => {
            for i in 0..=p.len() {
                if matches(&t[1..], &p[i..]) {
                    return true;
                }
                if i < p.len() && p[i] == '/' {
                    break;
                }
            }
            false
        }
        Some(Tok::DoubleStar) => (0..=p.len()).any(|i| matches(&t[1..], &p[i..])),
        Some(Tok::AnyDirs) => {
            matches(&t[1..], p) || (0..p.len()).any(|i| p[i] == '/' && matches(&t[1..], &p[i + 1..]))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::Glob;

    #[test]
    fn globs() {
        let g = Glob::new("images/packed/card_portraits/**/*.png");
        assert!(g.is_match("images/packed/card_portraits/defect/strike.png"));
        assert!(g.is_match("Images/Packed/card_portraits/defect/beta/strike.PNG"));
        assert!(g.is_match("images/packed/card_portraits/strike.png"));
        assert!(!g.is_match("images/packed/card_portraits/defect/strike.png.import"));
        assert_eq!(g.literal_directory_prefix(), "images/packed/card_portraits/");
        let g = Glob::new("localization/kor/*.json");
        assert!(g.is_match("localization/kor/cards.json") && !g.is_match("localization/kor/x/cards.json"));
        assert!(Glob::new("portrait").is_match("a/PORTRAIT/b") && Glob::new("portrait").literal_directory_prefix().is_empty());
        assert!(Glob::new("a?c").is_match("abc") && !Glob::new("a?c").is_match("a/c"));
    }
}
