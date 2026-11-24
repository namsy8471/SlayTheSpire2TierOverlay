//! A small read-only ECMA-335 metadata reader: what `GameReader` needed from Mono.Cecil.
//!
//! It parses the PE file, the metadata root, the `#~` table stream and the heaps, method signatures and IL bodies.
//! The file is only read as data; no code in it ever runs. References into other assemblies are not followed
//! (Cecil would resolve them, but the catalog only follows the game's own model types).

use std::collections::HashMap;

pub type R<T> = Result<T, String>;

// Table numbers (ECMA-335 II.22).
pub const MODULE: usize = 0x00;
pub const TYPE_REF: usize = 0x01;
pub const TYPE_DEF: usize = 0x02;
pub const FIELD: usize = 0x04;
pub const METHOD_DEF: usize = 0x06;
pub const PARAM: usize = 0x08;
pub const MEMBER_REF: usize = 0x0A;
pub const CONSTANT: usize = 0x0B;
pub const CUSTOM_ATTRIBUTE: usize = 0x0C;
pub const CLASS_LAYOUT: usize = 0x0F;
pub const TYPE_SPEC: usize = 0x1B;
pub const FIELD_RVA: usize = 0x1D;
pub const NESTED_CLASS: usize = 0x29;
pub const METHOD_SPEC: usize = 0x2B;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Col {
    U8x2,
    U16,
    U32,
    Str,
    Guid,
    Blob,
    Idx(usize),
    Coded(&'static [Option<usize>]),
}

const TYPE_DEF_OR_REF: &[Option<usize>] = &[Some(TYPE_DEF), Some(TYPE_REF), Some(TYPE_SPEC)];
const HAS_CONSTANT: &[Option<usize>] = &[Some(FIELD), Some(PARAM), Some(0x17)];
const HAS_CUSTOM_ATTRIBUTE: &[Option<usize>] = &[
    Some(METHOD_DEF), Some(FIELD), Some(TYPE_REF), Some(TYPE_DEF), Some(PARAM), Some(0x09), Some(MEMBER_REF), Some(MODULE),
    Some(0x0E), Some(0x17), Some(0x14), Some(0x11), Some(0x1A), Some(TYPE_SPEC), Some(0x20), Some(0x23), Some(0x26), Some(0x27),
    Some(0x28), Some(0x2A), Some(0x2C), Some(METHOD_SPEC),
];
const HAS_FIELD_MARSHAL: &[Option<usize>] = &[Some(FIELD), Some(PARAM)];
const HAS_DECL_SECURITY: &[Option<usize>] = &[Some(TYPE_DEF), Some(METHOD_DEF), Some(0x20)];
const MEMBER_REF_PARENT: &[Option<usize>] = &[Some(TYPE_DEF), Some(TYPE_REF), Some(0x1A), Some(METHOD_DEF), Some(TYPE_SPEC)];
const HAS_SEMANTICS: &[Option<usize>] = &[Some(0x14), Some(0x17)];
const METHOD_DEF_OR_REF: &[Option<usize>] = &[Some(METHOD_DEF), Some(MEMBER_REF)];
const MEMBER_FORWARDED: &[Option<usize>] = &[Some(FIELD), Some(METHOD_DEF)];
const IMPLEMENTATION: &[Option<usize>] = &[Some(0x26), Some(0x23), Some(0x27)];
const CUSTOM_ATTRIBUTE_TYPE: &[Option<usize>] = &[None, None, Some(METHOD_DEF), Some(MEMBER_REF), None];
const RESOLUTION_SCOPE: &[Option<usize>] = &[Some(MODULE), Some(0x1A), Some(0x23), Some(TYPE_REF)];
const TYPE_OR_METHOD_DEF: &[Option<usize>] = &[Some(TYPE_DEF), Some(METHOD_DEF)];

fn schema(table: usize) -> &'static [Col] {
    use Col::*;
    match table {
        0x00 => &[U16, Str, Guid, Guid, Guid],
        0x01 => &[Coded(RESOLUTION_SCOPE), Str, Str],
        0x02 => &[U32, Str, Str, Coded(TYPE_DEF_OR_REF), Idx(FIELD), Idx(METHOD_DEF)],
        0x03 => &[Idx(FIELD)],
        0x04 => &[U16, Str, Blob],
        0x05 => &[Idx(METHOD_DEF)],
        0x06 => &[U32, U16, U16, Str, Blob, Idx(PARAM)],
        0x07 => &[Idx(PARAM)],
        0x08 => &[U16, U16, Str],
        0x09 => &[Idx(TYPE_DEF), Coded(TYPE_DEF_OR_REF)],
        0x0A => &[Coded(MEMBER_REF_PARENT), Str, Blob],
        0x0B => &[U8x2, Coded(HAS_CONSTANT), Blob],
        0x0C => &[Coded(HAS_CUSTOM_ATTRIBUTE), Coded(CUSTOM_ATTRIBUTE_TYPE), Blob],
        0x0D => &[Coded(HAS_FIELD_MARSHAL), Blob],
        0x0E => &[U16, Coded(HAS_DECL_SECURITY), Blob],
        0x0F => &[U16, U32, Idx(TYPE_DEF)],
        0x10 => &[U32, Idx(FIELD)],
        0x11 => &[Blob],
        0x12 => &[Idx(TYPE_DEF), Idx(0x14)],
        0x13 => &[Idx(0x14)],
        0x14 => &[U16, Str, Coded(TYPE_DEF_OR_REF)],
        0x15 => &[Idx(TYPE_DEF), Idx(0x17)],
        0x16 => &[Idx(0x17)],
        0x17 => &[U16, Str, Blob],
        0x18 => &[U16, Idx(METHOD_DEF), Coded(HAS_SEMANTICS)],
        0x19 => &[Idx(TYPE_DEF), Coded(METHOD_DEF_OR_REF), Coded(METHOD_DEF_OR_REF)],
        0x1A => &[Str],
        0x1B => &[Blob],
        0x1C => &[U16, Coded(MEMBER_FORWARDED), Str, Idx(0x1A)],
        0x1D => &[U32, Idx(FIELD)],
        0x1E => &[U32, U32],
        0x1F => &[U32],
        0x20 => &[U32, U16, U16, U16, U16, U32, Blob, Str, Str],
        0x21 => &[U32],
        0x22 => &[U32, U32, U32],
        0x23 => &[U16, U16, U16, U16, U32, Blob, Str, Str, Blob],
        0x24 => &[U32, Idx(0x23)],
        0x25 => &[U32, U32, U32, Idx(0x23)],
        0x26 => &[U32, Str, Blob],
        0x27 => &[U32, U32, Str, Str, Coded(IMPLEMENTATION)],
        0x28 => &[U32, U32, Str, Coded(IMPLEMENTATION)],
        0x29 => &[Idx(TYPE_DEF), Idx(TYPE_DEF)],
        0x2A => &[U16, U16, Coded(TYPE_OR_METHOD_DEF), Str],
        0x2B => &[Coded(METHOD_DEF_OR_REF), Blob],
        0x2C => &[Idx(0x2A), Coded(TYPE_DEF_OR_REF)],
        _ => &[],
    }
}

/// A coded or simple reference: table number plus 1-based row (row 0 = null).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct Tok {
    pub table: usize,
    pub row: u32,
}

impl Tok {
    pub fn from_token(t: u32) -> Tok {
        Tok { table: (t >> 24) as usize, row: t & 0x00FF_FFFF }
    }
    pub fn is_null(&self) -> bool {
        self.row == 0
    }
}

struct Table {
    rows: u32,
    offset: usize,
    row_size: usize,
    cols: Vec<(usize, usize)>, // (offset in row, width)
}

/// Element of a signature (II.23.2.12).
#[derive(Clone, Debug, PartialEq)]
pub enum SigType {
    /// Primitive element type (void, bool, i4, string, object ...).
    Prim(u8),
    Class(Tok),
    ValueType(Tok),
    Var(u32),
    MVar(u32),
    GenericInst { value_type: bool, base: Tok, args: Vec<SigType> },
    SzArray(Box<SigType>),
    Array(Box<SigType>),
    Ptr(Box<SigType>),
    ByRef(Box<SigType>),
    FnPtr,
    TypedByRef,
}

pub const ELEMENT_VOID: u8 = 0x01;
pub const ELEMENT_I4: u8 = 0x08;

#[derive(Clone, Debug)]
pub struct MethodSig {
    pub has_this: bool,
    pub ret: SigType,
    pub params: Vec<SigType>,
}

pub struct Metadata {
    data: Vec<u8>,
    sections: Vec<(u32, u32, u32)>, // (virtual address, virtual size, raw pointer)
    strings: (usize, usize),
    us: (usize, usize),
    blob: (usize, usize),
    tables: Vec<Table>,
    nested_in: HashMap<u32, u32>,
    nested: HashMap<u32, Vec<u32>>,
    method_owner: Vec<Option<u32>>,
}

fn u16_at(d: &[u8], o: usize) -> R<u16> {
    d.get(o..o + 2).map(|b| u16::from_le_bytes([b[0], b[1]])).ok_or_else(|| format!("read past end at {o:#x}"))
}
fn u32_at(d: &[u8], o: usize) -> R<u32> {
    d.get(o..o + 4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]])).ok_or_else(|| format!("read past end at {o:#x}"))
}

impl Metadata {
    pub fn parse(data: Vec<u8>) -> R<Metadata> {
        let d = &data;
        if d.get(0..2) != Some(b"MZ") {
            return Err("not a PE file (no MZ)".into());
        }
        let pe = u32_at(d, 0x3C)? as usize;
        if d.get(pe..pe + 4) != Some(b"PE\0\0") {
            return Err("not a PE file (no PE signature)".into());
        }
        let coff = pe + 4;
        let nsec = u16_at(d, coff + 2)? as usize;
        let opt_size = u16_at(d, coff + 16)? as usize;
        let opt = coff + 20;
        let magic = u16_at(d, opt)?;
        let dirs = match magic {
            0x10B => opt + 96,
            0x20B => opt + 112,
            _ => return Err(format!("unknown optional header magic {magic:#x}")),
        };
        let mut sections = Vec::new();
        let sec0 = opt + opt_size;
        for i in 0..nsec {
            let s = sec0 + i * 40;
            sections.push((u32_at(d, s + 12)?, u32_at(d, s + 8)?.max(u32_at(d, s + 16)?), u32_at(d, s + 20)?));
        }
        let mut md = Metadata {
            data: Vec::new(),
            sections,
            strings: (0, 0),
            us: (0, 0),
            blob: (0, 0),
            tables: Vec::new(),
            nested_in: HashMap::new(),
            nested: HashMap::new(),
            method_owner: Vec::new(),
        };
        let cli_rva = u32_at(d, dirs + 14 * 8)?;
        let cli = md.rva_to_offset(cli_rva).ok_or("no CLI header")?;
        let meta = md.rva_to_offset(u32_at(d, cli + 8)?).ok_or("no metadata")?;
        if u32_at(d, meta)? != 0x424A_5342 {
            return Err("bad metadata signature".into());
        }
        let vlen = u32_at(d, meta + 12)? as usize;
        let mut p = meta + 16 + vlen;
        let nstreams = u16_at(d, p + 2)? as usize;
        p += 4;
        let mut table_stream = None;
        for _ in 0..nstreams {
            let off = u32_at(d, p)? as usize;
            let size = u32_at(d, p + 4)? as usize;
            let mut q = p + 8;
            let name_start = q;
            while d.get(q).copied().ok_or("bad stream name")? != 0 {
                q += 1;
            }
            let name = String::from_utf8_lossy(&d[name_start..q]).to_string();
            q += 1;
            p = (q + 3) & !3;
            let range = (meta + off, size);
            match name.as_str() {
                "#~" => table_stream = Some(range),
                "#-" => return Err("uncompressed metadata (#-) is not supported".into()),
                "#Strings" => md.strings = range,
                "#US" => md.us = range,
                "#Blob" => md.blob = range,
                _ => {}
            }
        }
        let (ts, _) = table_stream.ok_or("no #~ stream")?;
        let heap_sizes = d[ts + 6];
        let valid = u64::from(u32_at(d, ts + 8)?) | (u64::from(u32_at(d, ts + 12)?) << 32);
        let mut rows = [0u32; 64];
        let mut q = ts + 24;
        for (t, r) in rows.iter_mut().enumerate() {
            if valid >> t & 1 == 1 {
                *r = u32_at(d, q)?;
                q += 4;
            }
        }
        if heap_sizes & 0x40 != 0 {
            q += 4;
        }
        let str_w = if heap_sizes & 1 != 0 { 4 } else { 2 };
        let guid_w = if heap_sizes & 2 != 0 { 4 } else { 2 };
        let blob_w = if heap_sizes & 4 != 0 { 4 } else { 2 };
        let width = |c: Col| -> usize {
            match c {
                Col::U8x2 | Col::U16 => 2,
                Col::U32 => 4,
                Col::Str => str_w,
                Col::Guid => guid_w,
                Col::Blob => blob_w,
                Col::Idx(t) => {
                    if rows[t] < 0x10000 { 2 } else { 4 }
                }
                Col::Coded(tabs) => {
                    let bits = usize::BITS - (tabs.len() - 1).leading_zeros();
                    let max = tabs.iter().flatten().map(|&t| rows[t]).max().unwrap_or(0);
                    if max < (1u32 << (16 - bits)) { 2 } else { 4 }
                }
            }
        };
        for (t, &n) in rows.iter().enumerate().take(0x2D) {
            let mut cols = Vec::new();
            let mut off = 0;
            for &c in schema(t) {
                let w = width(c);
                cols.push((off, w));
                off += w;
            }
            md.tables.push(Table { rows: n, offset: q, row_size: off, cols });
            q += off * n as usize;
        }
        md.data = data;
        for r in 1..=md.rows(NESTED_CLASS) {
            let inner = md.col(NESTED_CLASS, r, 0);
            let outer = md.col(NESTED_CLASS, r, 1);
            md.nested_in.insert(inner, outer);
            md.nested.entry(outer).or_default().push(inner);
        }
        md.method_owner = vec![None; md.rows(METHOD_DEF) as usize + 1];
        for t in 1..=md.rows(TYPE_DEF) {
            for m in md.methods_of(t) {
                md.method_owner[m as usize] = Some(t);
            }
        }
        for v in md.nested.values_mut() {
            v.sort_unstable();
        }
        Ok(md)
    }

    pub fn rva_to_offset(&self, rva: u32) -> Option<usize> {
        self.sections
            .iter()
            .find(|&&(va, size, _)| rva >= va && rva < va + size)
            .map(|&(va, _, raw)| (rva - va + raw) as usize)
    }

    pub fn rows(&self, table: usize) -> u32 {
        self.tables.get(table).map_or(0, |t| t.rows)
    }

    /// Raw value of column `c` of a 1-based row.
    pub fn col(&self, table: usize, row: u32, c: usize) -> u32 {
        let t = &self.tables[table];
        let (off, w) = t.cols[c];
        let at = t.offset + (row as usize - 1) * t.row_size + off;
        if w == 2 { u32::from(u16::from_le_bytes([self.data[at], self.data[at + 1]])) } else { u32::from_le_bytes(self.data[at..at + 4].try_into().unwrap()) }
    }

    fn coded(&self, table: usize, row: u32, c: usize) -> Tok {
        let Col::Coded(tabs) = schema(table)[c] else { unreachable!() };
        let bits = usize::BITS - (tabs.len() - 1).leading_zeros();
        let v = self.col(table, row, c);
        let tag = (v & ((1 << bits) - 1)) as usize;
        Tok { table: tabs.get(tag).copied().flatten().unwrap_or(usize::MAX), row: v >> bits }
    }

    pub fn string(&self, index: u32) -> &str {
        let start = self.strings.0 + index as usize;
        let end = self.data[start..].iter().position(|&b| b == 0).map_or(self.data.len(), |p| start + p);
        std::str::from_utf8(&self.data[start..end]).unwrap_or("")
    }

    pub fn str_col(&self, table: usize, row: u32, c: usize) -> &str {
        self.string(self.col(table, row, c))
    }

    /// Blob heap entry (length-prefixed).
    pub fn blob(&self, index: u32) -> &[u8] {
        let mut p = self.blob.0 + index as usize;
        let len = read_compressed(&self.data, &mut p).unwrap_or(0) as usize;
        self.data.get(p..p + len).unwrap_or(&[])
    }

    /// User string (#US) as text.
    pub fn user_string(&self, index: u32) -> String {
        let mut p = self.us.0 + index as usize;
        let len = read_compressed(&self.data, &mut p).unwrap_or(0) as usize;
        let bytes = self.data.get(p..p + len.saturating_sub(1)).unwrap_or(&[]);
        let units: Vec<u16> = bytes.as_chunks::<2>().0.iter().map(|c| u16::from_le_bytes([c[0], c[1]])).collect();
        String::from_utf16_lossy(&units)
    }

    // ---- types -----------------------------------------------------------------------------------

    pub fn type_name(&self, row: u32) -> &str {
        self.str_col(TYPE_DEF, row, 1)
    }

    pub fn type_flags(&self, row: u32) -> u32 {
        self.col(TYPE_DEF, row, 0)
    }

    pub fn declaring_type(&self, row: u32) -> Option<u32> {
        self.nested_in.get(&row).copied()
    }

    /// Cecil's FullName: "Namespace.Name", nested types "Outer/Inner".
    pub fn type_full_name(&self, row: u32) -> String {
        match self.declaring_type(row) {
            Some(outer) => format!("{}/{}", self.type_full_name(outer), self.type_name(row)),
            None => {
                let ns = self.str_col(TYPE_DEF, row, 2);
                if ns.is_empty() { self.type_name(row).to_string() } else { format!("{ns}.{}", self.type_name(row)) }
            }
        }
    }

    pub fn type_ref_full_name(&self, row: u32) -> String {
        let scope = self.coded(TYPE_REF, row, 0);
        let name = self.str_col(TYPE_REF, row, 1);
        if scope.table == TYPE_REF && !scope.is_null() {
            return format!("{}/{name}", self.type_ref_full_name(scope.row));
        }
        let ns = self.str_col(TYPE_REF, row, 2);
        if ns.is_empty() { name.to_string() } else { format!("{ns}.{name}") }
    }

    /// Full name of any TypeDefOrRef token, generic instances as "Base<Arg,Arg>".
    pub fn tok_full_name(&self, t: Tok) -> String {
        match t.table {
            TYPE_DEF => self.type_full_name(t.row),
            TYPE_REF => self.type_ref_full_name(t.row),
            TYPE_SPEC => self.type_spec(t.row).map(|s| self.sig_full_name(&s)).unwrap_or_default(),
            _ => String::new(),
        }
    }

    /// Short name (Cecil's TypeReference.Name; a generic instance's element name, e.g. "HashSet`1").
    pub fn tok_name(&self, t: Tok) -> String {
        match t.table {
            TYPE_DEF => self.type_name(t.row).to_string(),
            TYPE_REF => self.str_col(TYPE_REF, t.row, 1).to_string(),
            TYPE_SPEC => match self.type_spec(t.row) {
                Ok(SigType::GenericInst { base, .. }) => self.tok_name(base),
                _ => String::new(),
            },
            _ => String::new(),
        }
    }

    pub fn sig_full_name(&self, s: &SigType) -> String {
        match s {
            SigType::Prim(p) => format!("#{p:02x}"),
            SigType::Class(t) | SigType::ValueType(t) => self.tok_full_name(*t),
            SigType::Var(n) => format!("!{n}"),
            SigType::MVar(n) => format!("!!{n}"),
            SigType::GenericInst { base, args, .. } => {
                format!("{}<{}>", self.tok_full_name(*base), args.iter().map(|a| self.sig_full_name(a)).collect::<Vec<_>>().join(","))
            }
            SigType::SzArray(e) => format!("{}[]", self.sig_full_name(e)),
            SigType::Array(e) => format!("{}[,]", self.sig_full_name(e)),
            SigType::Ptr(e) => format!("{}*", self.sig_full_name(e)),
            SigType::ByRef(e) => format!("{}&", self.sig_full_name(e)),
            SigType::FnPtr => "method*".into(),
            SigType::TypedByRef => "typedref".into(),
        }
    }

    pub fn type_spec(&self, row: u32) -> R<SigType> {
        let blob = self.blob(self.col(TYPE_SPEC, row, 0));
        let mut p = 0;
        parse_type(blob, &mut p)
    }

    /// The TypeDef a token stands for (a generic instance resolves to its generic type); references into other
    /// assemblies give None.
    pub fn resolve_type(&self, t: Tok) -> Option<u32> {
        match t.table {
            TYPE_DEF if !t.is_null() => Some(t.row),
            TYPE_SPEC => match self.type_spec(t.row).ok()? {
                SigType::GenericInst { base, .. } | SigType::Class(base) | SigType::ValueType(base) => self.resolve_type(base),
                _ => None,
            },
            _ => None,
        }
    }

    pub fn resolve_sig_type(&self, s: &SigType) -> Option<u32> {
        match s {
            SigType::Class(t) | SigType::ValueType(t) | SigType::GenericInst { base: t, .. } => self.resolve_type(*t),
            _ => None,
        }
    }

    pub fn base_type(&self, row: u32) -> Option<Tok> {
        let t = self.coded(TYPE_DEF, row, 3);
        (!t.is_null()).then_some(t)
    }

    fn list_range(&self, table: usize, row: u32, c: usize, target: usize) -> std::ops::Range<u32> {
        let start = self.col(table, row, c);
        let end = if row < self.rows(table) { self.col(table, row + 1, c) } else { self.rows(target) + 1 };
        start..end.max(start)
    }

    pub fn methods_of(&self, ty: u32) -> std::ops::Range<u32> {
        self.list_range(TYPE_DEF, ty, 5, METHOD_DEF)
    }

    pub fn fields_of(&self, ty: u32) -> std::ops::Range<u32> {
        self.list_range(TYPE_DEF, ty, 4, FIELD)
    }

    /// Cecil's ModuleDefinition.GetTypes(): top-level types in table order, each followed by its nested types.
    pub fn all_types(&self) -> Vec<u32> {
        fn add(md: &Metadata, row: u32, out: &mut Vec<u32>) {
            out.push(row);
            if let Some(inner) = md.nested.get(&row) {
                for &i in inner {
                    add(md, i, out);
                }
            }
        }
        let mut out = Vec::new();
        for r in 1..=self.rows(TYPE_DEF) {
            if !self.nested_in.contains_key(&r) {
                add(self, r, &mut out);
            }
        }
        out
    }

    pub fn has_custom_attribute(&self, target: Tok, attribute_name: &str) -> bool {
        (1..=self.rows(CUSTOM_ATTRIBUTE)).any(|r| {
            self.coded(CUSTOM_ATTRIBUTE, r, 0) == target && {
                let ctor = self.coded(CUSTOM_ATTRIBUTE, r, 1);
                let owner = match ctor.table {
                    METHOD_DEF => self.method_owner(ctor.row).map(|t| Tok { table: TYPE_DEF, row: t }),
                    MEMBER_REF => Some(self.coded(MEMBER_REF, ctor.row, 0)),
                    _ => None,
                };
                owner.is_some_and(|o| self.tok_name(o) == attribute_name)
            }
        })
    }

    // ---- fields ----------------------------------------------------------------------------------

    pub fn field_flags(&self, row: u32) -> u16 {
        self.col(FIELD, row, 0) as u16
    }

    pub fn field_name(&self, row: u32) -> &str {
        self.str_col(FIELD, row, 1)
    }

    /// The constant of a field as an integer (enum members), if it has one.
    pub fn field_constant(&self, row: u32) -> Option<i64> {
        let target = Tok { table: FIELD, row };
        let r = (1..=self.rows(CONSTANT)).find(|&r| self.coded(CONSTANT, r, 1) == target)?;
        let ty = (self.col(CONSTANT, r, 0) & 0xFF) as u8;
        let b = self.blob(self.col(CONSTANT, r, 2));
        let le = |n: usize| -> Option<u64> { b.get(..n).map(|s| s.iter().rev().fold(0u64, |a, &x| a << 8 | u64::from(x))) };
        Some(match ty {
            0x02 | 0x05 => le(1)? as i64,
            0x04 => le(1)? as u8 as i8 as i64,
            0x03 | 0x07 => le(2)? as i64,
            0x06 => le(2)? as u16 as i16 as i64,
            0x08 => le(4)? as u32 as i32 as i64,
            0x09 => le(4)? as i64,
            0x0A | 0x0B => le(8)? as i64,
            _ => return None,
        })
    }

    /// FieldDefinition.InitialValue: the RVA data of a field, as long as its type's size.
    pub fn field_initial_value(&self, row: u32) -> Option<Vec<u8>> {
        let r = (1..=self.rows(FIELD_RVA)).find(|&r| self.col(FIELD_RVA, r, 1) == row)?;
        let start = self.rva_to_offset(self.col(FIELD_RVA, r, 0))?;
        let sig = self.blob(self.col(FIELD, row, 2));
        let mut p = 1; // FIELD calling convention 0x06
        let ty = parse_type(sig, &mut p).ok()?;
        let size = match ty {
            SigType::Prim(0x02 | 0x04 | 0x05) => 1,
            SigType::Prim(0x03 | 0x06 | 0x07) => 2,
            SigType::Prim(0x08 | 0x09 | 0x0C) => 4,
            SigType::Prim(0x0A | 0x0B | 0x0D) => 8,
            SigType::ValueType(t) => {
                let def = self.resolve_type(t)?;
                let l = (1..=self.rows(CLASS_LAYOUT)).find(|&l| self.col(CLASS_LAYOUT, l, 2) == def)?;
                self.col(CLASS_LAYOUT, l, 1) as usize
            }
            _ => return None,
        };
        self.data.get(start..start + size).map(<[u8]>::to_vec)
    }

    // ---- methods ---------------------------------------------------------------------------------

    pub fn method_name(&self, row: u32) -> &str {
        self.str_col(METHOD_DEF, row, 3)
    }

    pub fn method_flags(&self, row: u32) -> u16 {
        self.col(METHOD_DEF, row, 2) as u16
    }

    pub fn method_sig(&self, row: u32) -> R<MethodSig> {
        parse_method_sig(self.blob(self.col(METHOD_DEF, row, 4)))
    }

    pub fn method_owner(&self, row: u32) -> Option<u32> {
        self.method_owner.get(row as usize).copied().flatten()
    }

    /// Parameter names in order (sequence 1..n; the return value row 0 is skipped).
    pub fn param_names(&self, method: u32) -> Vec<String> {
        let n = self.method_sig(method).map(|s| s.params.len()).unwrap_or(0);
        let mut names = vec![String::new(); n];
        for p in self.list_range(METHOD_DEF, method, 5, PARAM) {
            let seq = self.col(PARAM, p, 1) as usize;
            if seq >= 1 && seq <= n {
                names[seq - 1] = self.str_col(PARAM, p, 2).to_string();
            }
        }
        names
    }

    /// IL bytes of a method body; None when it has none (abstract, extern).
    pub fn method_body(&self, row: u32) -> Option<&[u8]> {
        let rva = self.col(METHOD_DEF, row, 0);
        if rva == 0 {
            return None;
        }
        let p = self.rva_to_offset(rva)?;
        let h = *self.data.get(p)?;
        match h & 3 {
            2 => self.data.get(p + 1..p + 1 + (h >> 2) as usize),
            3 => {
                let flags = u16_at(&self.data, p).ok()?;
                let header = ((flags >> 12) * 4) as usize;
                let size = u32_at(&self.data, p + 4).ok()? as usize;
                self.data.get(p + header..p + header + size)
            }
            _ => None,
        }
    }

    pub fn member_ref_parent(&self, row: u32) -> Tok {
        self.coded(MEMBER_REF, row, 0)
    }

    pub fn method_spec(&self, row: u32) -> (Tok, Vec<SigType>) {
        let m = self.coded(METHOD_SPEC, row, 0);
        let b = self.blob(self.col(METHOD_SPEC, row, 1));
        let mut p = 1; // GENERICINST 0x0A
        let mut args = Vec::new();
        if let Some(n) = read_compressed(b, &mut p) {
            for _ in 0..n {
                match parse_type(b, &mut p) {
                    Ok(t) => args.push(t),
                    Err(_) => break,
                }
            }
        }
        (m, args)
    }
}

// ---- signatures ----------------------------------------------------------------------------------

pub fn read_compressed(d: &[u8], p: &mut usize) -> Option<u32> {
    let b0 = *d.get(*p)?;
    if b0 & 0x80 == 0 {
        *p += 1;
        Some(u32::from(b0))
    } else if b0 & 0xC0 == 0x80 {
        let v = (u32::from(b0 & 0x3F) << 8) | u32::from(*d.get(*p + 1)?);
        *p += 2;
        Some(v)
    } else {
        let s = d.get(*p..*p + 4)?;
        *p += 4;
        Some((u32::from(s[0] & 0x1F) << 24) | (u32::from(s[1]) << 16) | (u32::from(s[2]) << 8) | u32::from(s[3]))
    }
}

fn type_def_or_ref(d: &[u8], p: &mut usize) -> R<Tok> {
    let v = read_compressed(d, p).ok_or("bad signature")?;
    let table = [TYPE_DEF, TYPE_REF, TYPE_SPEC].get((v & 3) as usize).copied().ok_or("bad TypeDefOrRef")?;
    Ok(Tok { table, row: v >> 2 })
}

pub fn parse_type(d: &[u8], p: &mut usize) -> R<SigType> {
    let e = *d.get(*p).ok_or("signature ends early")?;
    *p += 1;
    Ok(match e {
        0x1F | 0x20 => {
            type_def_or_ref(d, p)?;
            return parse_type(d, p);
        }
        0x45 | 0x41 => return parse_type(d, p),
        0x01..=0x0E | 0x18 | 0x19 | 0x1C => SigType::Prim(e),
        0x0F => SigType::Ptr(Box::new(parse_type(d, p)?)),
        0x10 => SigType::ByRef(Box::new(parse_type(d, p)?)),
        0x11 => SigType::ValueType(type_def_or_ref(d, p)?),
        0x12 => SigType::Class(type_def_or_ref(d, p)?),
        0x13 => SigType::Var(read_compressed(d, p).ok_or("bad var")?),
        0x1E => SigType::MVar(read_compressed(d, p).ok_or("bad mvar")?),
        0x14 => {
            let elem = parse_type(d, p)?;
            let _rank = read_compressed(d, p);
            let n = read_compressed(d, p).unwrap_or(0);
            for _ in 0..n {
                read_compressed(d, p);
            }
            let n = read_compressed(d, p).unwrap_or(0);
            for _ in 0..n {
                read_compressed(d, p);
            }
            SigType::Array(Box::new(elem))
        }
        0x15 => {
            let kind = *d.get(*p).ok_or("bad genericinst")?;
            *p += 1;
            let base = type_def_or_ref(d, p)?;
            let n = read_compressed(d, p).ok_or("bad genericinst")?;
            let mut args = Vec::new();
            for _ in 0..n {
                args.push(parse_type(d, p)?);
            }
            SigType::GenericInst { value_type: kind == 0x11, base, args }
        }
        0x16 => SigType::TypedByRef,
        0x1B => {
            parse_method_sig_at(d, p)?;
            SigType::FnPtr
        }
        0x1D => SigType::SzArray(Box::new(parse_type(d, p)?)),
        _ => return Err(format!("unknown element type {e:#x}")),
    })
}

fn parse_method_sig_at(d: &[u8], p: &mut usize) -> R<MethodSig> {
    let flags = *d.get(*p).ok_or("empty method signature")?;
    *p += 1;
    if flags & 0x10 != 0 {
        read_compressed(d, p);
    }
    let n = read_compressed(d, p).ok_or("bad param count")?;
    let ret = parse_type(d, p)?;
    let mut params = Vec::new();
    for _ in 0..n {
        params.push(parse_type(d, p)?);
    }
    Ok(MethodSig { has_this: flags & 0x20 != 0, ret, params })
}

pub fn parse_method_sig(d: &[u8]) -> R<MethodSig> {
    let mut p = 0;
    parse_method_sig_at(d, &mut p)
}

// ---- IL ------------------------------------------------------------------------------------------

/// One decoded IL instruction: opcode (two-byte opcodes as 0xFE00 | second byte), offset and raw operand.
#[derive(Clone, Copy, Debug)]
pub struct Instr {
    pub op: u16,
    pub offset: usize,
    pub operand: i64,
}

/// Operand size of an opcode (ECMA-335 III); `None` for `switch`, which has a variable-size operand.
fn operand_size(op: u16) -> Option<usize> {
    Some(match op {
        0x0E..=0x13 | 0x1F | 0x2B..=0x37 | 0xDE => 1,
        0x20 | 0x22 | 0x27 | 0x28 | 0x29 | 0x38..=0x44 | 0x6F..=0x75 | 0x79 | 0x7B..=0x81 | 0x8C | 0x8D | 0x8F | 0xA3..=0xA5 | 0xC2
        | 0xC6 | 0xD0 | 0xDD => 4,
        0x21 | 0x23 => 8,
        0x45 => return None,
        0xFE06 | 0xFE07 | 0xFE15 | 0xFE16 | 0xFE1C => 4,
        0xFE09..=0xFE0E => 2,
        0xFE12 | 0xFE19 => 1,
        _ => 0,
    })
}

pub fn decode_il(code: &[u8]) -> R<Vec<Instr>> {
    let mut out = Vec::new();
    let mut p = 0;
    while p < code.len() {
        let offset = p;
        let mut op = u16::from(code[p]);
        p += 1;
        if op == 0xFE {
            op = 0xFE00 | u16::from(*code.get(p).ok_or("IL ends inside an opcode")?);
            p += 1;
        }
        let operand = match operand_size(op) {
            None => {
                let n = u32_at(code, p)? as usize;
                p += 4 + 4 * n;
                n as i64
            }
            Some(0) => 0,
            Some(n) => {
                let b = code.get(p..p + n).ok_or("IL ends inside an operand")?;
                p += n;
                match n {
                    1 => i64::from(b[0]),
                    2 => i64::from(u16::from_le_bytes([b[0], b[1]])),
                    4 => i64::from(u32::from_le_bytes(b.try_into().unwrap())),
                    _ => i64::from_le_bytes(b.try_into().unwrap()),
                }
            }
        };
        out.push(Instr { op, offset, operand });
    }
    Ok(out)
}
