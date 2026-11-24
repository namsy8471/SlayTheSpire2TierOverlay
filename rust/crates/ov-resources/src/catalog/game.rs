//! Port of Catalog/GameReader.cs: decodes the card, relic and character models of sts2.dll from metadata and IL.

use std::collections::HashMap;

use super::il::{self, MethodRef, V};
use super::meta::{self, Metadata, SigType, Tok};
use super::slug;

/// Everything statically decoded about one card class.
#[derive(Clone, Debug, Default)]
pub struct DecodedCard {
    pub type_row: u32,
    pub id: String,
    pub card_type: Option<String>,
    pub rarity: Option<String>,
    pub target: Option<String>,
    pub cost: Option<i32>,
    pub cost_x: Option<bool>,
    pub star_cost: Option<i32>,
    pub star_cost_x: Option<bool>,
    pub gains_block: Option<bool>,
    pub max_upgrade: Option<i32>,
    pub multiplayer: Option<String>,
    pub show_in_library: Option<bool>,
    pub keywords: Option<Vec<String>>,
    pub tags: Option<Vec<String>>,
    pub unresolved: Vec<String>,
    pub dynamic: Vec<String>,
}

#[derive(Clone, Debug)]
pub struct DecodedRelic {
    pub id: String,
    pub rarity: Option<String>,
    pub has_upon_pickup_effect: Option<bool>,
    pub spawns_pets: Option<bool>,
    pub used_up_override: bool,
    pub adds_pet: Option<bool>,
    pub allowed_override: bool,
}

/// Enum value -> name, read from the enum's own metadata.
#[derive(Clone, Debug, Default)]
pub struct EnumTable {
    names: Vec<(i64, String)>,
    is_flags: bool,
}

impl EnumTable {
    fn from(md: &Metadata, ty: Option<u32>) -> Result<EnumTable, String> {
        let def = ty.ok_or("cannot resolve an enum type")?;
        let mut t = EnumTable {
            names: Vec::new(),
            is_flags: md.has_custom_attribute(Tok { table: meta::TYPE_DEF, row: def }, "FlagsAttribute"),
        };
        for f in md.fields_of(def) {
            if md.field_flags(f) & 0x10 == 0 {
                continue;
            }
            if let Some(v) = md.field_constant(f)
                && !t.names.iter().any(|(k, _)| *k == v)
            {
                t.names.push((v, md.field_name(f).to_string()));
            }
        }
        Ok(t)
    }

    pub fn name(&self, value: Option<i32>) -> Option<String> {
        let v = i64::from(value?);
        if let Some((_, n)) = self.names.iter().find(|(k, _)| *k == v) {
            return Some(n.clone());
        }
        if self.is_flags {
            let parts: Vec<&str> = self.names.iter().filter(|(k, _)| *k != 0 && (v & k) == *k).map(|(_, n)| n.as_str()).collect();
            if !parts.is_empty() {
                return Some(parts.join(", "));
            }
        }
        Some(v.to_string())
    }
}

pub struct GameReader {
    pub md: Metadata,
    by_full_name: HashMap<String, u32>,
    pub abstract_model: u32,
    pub card_model: u32,
    pub relic_model: u32,
    pub card_pool_model: u32,
    pub character_model: u32,
    pub model_db: u32,
    card_type: EnumTable,
    rarity: EnumTable,
    target: EnumTable,
    multiplayer: EnumTable,
    keyword: EnumTable,
    tag: EnumTable,
    relic_rarity: EnumTable,
}

impl GameReader {
    /// Reads the dll bytes (shared read, so it works while the game runs) and indexes its types.
    pub fn open(dll: &std::path::Path) -> Result<GameReader, String> {
        let bytes = crate::io::read_shared(dll).map_err(|e| format!("{}: {e}", dll.display()))?;
        GameReader::from_bytes(bytes)
    }

    pub fn from_bytes(bytes: Vec<u8>) -> Result<GameReader, String> {
        let md = Metadata::parse(bytes)?;
        let mut by_full_name = HashMap::new();
        for t in md.all_types() {
            by_full_name.entry(md.type_full_name(t)).or_insert(t);
        }
        let find = |name: &str| -> Result<u32, String> {
            by_full_name.get(name).copied().ok_or_else(|| format!("Type {name} not found in assembly - game update renamed it?"))
        };
        let abstract_model = find("MegaCrit.Sts2.Core.Models.AbstractModel")?;
        let card_model = find("MegaCrit.Sts2.Core.Models.CardModel")?;
        let relic_model = find("MegaCrit.Sts2.Core.Models.RelicModel")?;
        let card_pool_model = find("MegaCrit.Sts2.Core.Models.CardPoolModel")?;
        let character_model = find("MegaCrit.Sts2.Core.Models.CharacterModel")?;
        let model_db = find("MegaCrit.Sts2.Core.Models.ModelDb")?;
        let ret = |ty: u32, getter: &str| -> Result<SigType, String> {
            let m = getter_of(&md, ty, getter).ok_or_else(|| format!("{getter} not found"))?;
            Ok(md.method_sig(m)?.ret)
        };
        let enum_of = |s: &SigType| EnumTable::from(&md, md.resolve_sig_type(s));
        let generic_arg = |s: SigType| -> Result<SigType, String> {
            match s {
                SigType::GenericInst { mut args, .. } if !args.is_empty() => Ok(args.swap_remove(0)),
                _ => Err("expected a generic return type".into()),
            }
        };
        let card_type = enum_of(&ret(card_model, "get_Type")?)?;
        let rarity = enum_of(&ret(card_model, "get_Rarity")?)?;
        let target = enum_of(&ret(card_model, "get_TargetType")?)?;
        let multiplayer = enum_of(&ret(card_model, "get_MultiplayerConstraint")?)?;
        let keyword = enum_of(&generic_arg(ret(card_model, "get_CanonicalKeywords")?)?)?;
        let tag = enum_of(&generic_arg(ret(card_model, "get_CanonicalTags")?)?)?;
        let relic_rarity = enum_of(&ret(relic_model, "get_Rarity")?)?;
        Ok(GameReader {
            md,
            by_full_name,
            abstract_model,
            card_model,
            relic_model,
            card_pool_model,
            character_model,
            model_db,
            card_type,
            rarity,
            target,
            multiplayer,
            keyword,
            tag,
            relic_rarity,
        })
    }

    pub fn find_type(&self, full_name: &str) -> Option<u32> {
        self.by_full_name.get(full_name).copied()
    }

    pub fn full_name(&self, ty: u32) -> String {
        self.md.type_full_name(ty)
    }

    /// Base classes, nearest first; stops at a base outside this assembly.
    pub fn base_chain(&self, ty: u32) -> Vec<u32> {
        let mut out = Vec::new();
        let mut b = self.md.base_type(ty);
        while let Some(t) = b {
            let Some(def) = self.md.resolve_type(t) else { break };
            if out.contains(&def) {
                break;
            }
            out.push(def);
            b = self.md.base_type(def);
        }
        out
    }

    fn inherits_from(&self, ty: u32, base: u32) -> bool {
        self.base_chain(ty).contains(&base)
    }

    /// All concrete (non-abstract, non-interface) subclasses of a model base class, in GetTypes order.
    pub fn concrete_subclasses_of(&self, base: u32) -> Vec<u32> {
        self.md
            .all_types()
            .into_iter()
            .filter(|&t| {
                let f = self.md.type_flags(t);
                f & 0x20 == 0 && f & 0x80 == 0 && self.inherits_from(t, base)
            })
            .collect()
    }

    /// ModelDb.GetId(type): category = slug of the class right under AbstractModel, entry = slug of the class name.
    pub fn model_id_of(&self, ty: u32) -> String {
        let mut category = ty;
        for b in self.base_chain(ty) {
            if b == self.abstract_model {
                break;
            }
            category = b;
        }
        format!("{}.{}", slug::slugify_category(self.md.type_name(category)), slug::slugify(self.md.type_name(ty)))
    }

    pub fn type_name(&self, ty: u32) -> &str {
        self.md.type_name(ty)
    }

    pub fn find_method(&self, ty: u32, name: &str) -> Option<u32> {
        self.md.methods_of(ty).find(|&m| self.md.method_name(m) == name)
    }

    /// Every `ModelDb.{method_name}<T>()` call in a method body, in order, duplicates kept.
    pub fn model_db_generic_calls(&self, method: Option<u32>, method_name: &str) -> Vec<u32> {
        let mut out = Vec::new();
        let Some(body) = method.and_then(|m| self.md.method_body(m)) else { return out };
        let Ok(code) = meta::decode_il(body) else { return out };
        for ins in code {
            if ins.op != 0x28 && ins.op != 0x6F {
                continue;
            }
            let tok = Tok::from_token(ins.operand as u32);
            if tok.table != meta::METHOD_SPEC {
                continue;
            }
            let Some(r) = self.md.method_ref(tok) else { continue };
            if r.name == method_name
                && self.md.tok_full_name(r.declaring) == self.full_name(self.model_db)
                && let Some(t) = r.generic_args.first().and_then(|a| self.md.resolve_sig_type(a))
            {
                out.push(t);
            }
        }
        out
    }

    /// A constant int getter on the type or the nearest base that defines it; None when not constant.
    pub fn evaluate_int_getter(&self, ty: u32, getter: &str) -> Option<i32> {
        for t in std::iter::once(ty).chain(self.base_chain(ty)) {
            let Some(g) = getter_of(&self.md, t, getter) else { continue };
            let r = il::evaluate(&self.md, g, None, None);
            return if r.ok { r.value.and_then(|v| v.as_int()) } else { None };
        }
        None
    }

    pub fn decode_relic(&self, relic: u32) -> DecodedRelic {
        let b = |g: &str| self.evaluate_int_getter(relic, g).map(|v| v != 0);
        let overrides = |method: &str| {
            std::iter::once(relic)
                .chain(self.base_chain(relic))
                .take_while(|&t| t != self.relic_model)
                .any(|t| self.md.methods_of(t).any(|m| self.md.method_name(m) == method && self.md.method_flags(m) & 0x0400 == 0))
        };
        DecodedRelic {
            id: self.model_id_of(relic),
            rarity: self.relic_rarity.name(self.evaluate_int_getter(relic, "get_Rarity")),
            has_upon_pickup_effect: b("get_HasUponPickupEffect"),
            spawns_pets: b("get_SpawnsPets"),
            used_up_override: overrides("get_IsUsedUp"),
            adds_pet: b("get_AddsPet"),
            allowed_override: overrides("IsAllowed"),
        }
    }

    pub fn decode_card(&self, card: u32) -> DecodedCard {
        let mut d = DecodedCard { type_row: card, id: self.model_id_of(card), ..Default::default() };
        let ctor = self.decode_base_ctor_args(card);
        let c = ctor.as_ref();
        d.card_type = self.card_type.name(self.resolve_int(&mut d, card, "type", "get_Type", c, Some("type")));
        d.rarity = self.rarity.name(self.resolve_int(&mut d, card, "rarity", "get_Rarity", c, Some("rarity")));
        d.target = self.target.name(self.resolve_int(&mut d, card, "target", "get_TargetType", c, Some("targetType")));
        d.cost = self.resolve_int(&mut d, card, "cost", "get_CanonicalEnergyCost", c, Some("canonicalEnergyCost"));
        d.cost_x = self.resolve_int(&mut d, card, "costX", "get_HasEnergyCostX", None, None).map(|v| v != 0);
        d.star_cost = self.resolve_int(&mut d, card, "starCost", "get_CanonicalStarCost", None, None);
        d.star_cost_x = self.resolve_int(&mut d, card, "starCostX", "get_HasStarCostX", None, None).map(|v| v != 0);
        d.gains_block = self.resolve_int(&mut d, card, "gainsBlock", "get_GainsBlock", None, None).map(|v| v != 0);
        d.max_upgrade = self.resolve_int(&mut d, card, "maxUpgrade", "get_MaxUpgradeLevel", None, None);
        d.multiplayer = self.multiplayer.name(self.resolve_int(&mut d, card, "multiplayer", "get_MultiplayerConstraint", None, None));
        match &ctor {
            None => d.unresolved.push("showInLibrary".into()),
            Some(args) => {
                d.show_in_library = Some(match args.iter().find(|(k, _)| k == "shouldShowInCardLibrary").map(|(_, v)| v) {
                    Some(V::Int(s)) => *s != 0,
                    _ => true,
                })
            }
        }
        d.keywords = self.resolve_enum_list(&mut d, card, "keywords", "get_CanonicalKeywords", &self.keyword);
        d.tags = self.resolve_enum_list(&mut d, card, "tags", "get_CanonicalTags", &self.tag);
        d
    }

    /// An override of a getter in the card or any base class below CardModel.
    fn find_override(&self, card: u32, getter: &str) -> Option<u32> {
        for t in std::iter::once(card).chain(self.base_chain(card)) {
            if t == self.card_model {
                return None;
            }
            if let Some(m) = getter_of(&self.md, t, getter) {
                return Some(m);
            }
        }
        None
    }

    fn resolve_int(
        &self,
        d: &mut DecodedCard,
        card: u32,
        field: &str,
        getter: &str,
        ctor: Option<&Vec<(String, V)>>,
        ctor_param: Option<&str>,
    ) -> Option<i32> {
        let ctor_value =
            ctor_param.and_then(|p| ctor.and_then(|c| c.iter().find(|(k, _)| k == p).map(|(_, v)| v.clone()))).and_then(|v| v.as_int());
        if let Some(g) = self.find_override(card, getter) {
            let r = il::evaluate(&self.md, g, None, None);
            if r.ok
                && let Some(v) = r.value.as_ref().and_then(V::as_int)
            {
                return Some(v);
            }
            if let Some(cv) = ctor_value {
                d.dynamic.push(field.into());
                return Some(cv);
            }
            d.unresolved.push(field.into());
            return None;
        }
        if ctor_param.is_some() {
            if ctor_value.is_some() {
                return ctor_value;
            }
            d.unresolved.push(field.into());
            return None;
        }
        let base = getter_of(&self.md, self.card_model, getter).map(|g| il::evaluate(&self.md, g, None, None));
        if let Some(r) = base
            && r.ok
            && let Some(v) = r.value.as_ref().and_then(V::as_int)
        {
            return Some(v);
        }
        d.unresolved.push(field.into());
        None
    }

    fn resolve_enum_list(&self, d: &mut DecodedCard, card: u32, field: &str, getter: &str, table: &EnumTable) -> Option<Vec<String>> {
        let g = self.find_override(card, getter).or_else(|| getter_of(&self.md, self.card_model, getter));
        if let Some(g) = g {
            let r = il::evaluate(&self.md, g, None, None);
            if r.ok
                && let Some(V::Coll(c)) = &r.value
            {
                let items = c.borrow();
                if items.iter().all(|i| matches!(i, V::Int(_))) {
                    return Some(items.iter().map(|i| table.name(i.as_int()).unwrap_or_default()).collect());
                }
            }
        }
        d.unresolved.push(field.into());
        None
    }

    /// Runs the parameterless constructor up to its base constructor call and returns that call's arguments by
    /// parameter name, following intermediate base classes up to CardModel.
    fn decode_base_ctor_args(&self, card: u32) -> Option<Vec<(String, V)>> {
        let ctor = self.md.methods_of(card).find(|&m| {
            let f = self.md.method_flags(m);
            self.md.method_name(m) == ".ctor"
                && f & 0x0800 != 0
                && f & 0x1000 != 0
                && f & 0x0010 == 0
                && self.md.method_sig(m).is_ok_and(|s| s.params.is_empty())
        })?;
        self.capture_base_call(card, ctor, None)
    }

    fn capture_base_call(&self, owner: u32, ctor: u32, args: Option<&[V]>) -> Option<Vec<(String, V)>> {
        let mut captured: Option<Vec<(String, V)>> = None;
        let base_name = self.md.base_type(owner).map(|b| self.md.tok_full_name(b));
        let mut hook = |method: &MethodRef, call_args: &[V]| -> bool {
            let is_base_ctor = method.name == ".ctor"
                && matches!(call_args.first(), Some(V::This))
                && Some(self.md.tok_full_name(method.declaring)) == base_name;
            if !is_base_ctor {
                return false;
            }
            let Some(base_ctor) = self.md.resolve_method(method) else { return true };
            let Some(base_owner) = self.md.method_owner(base_ctor) else { return true };
            if base_owner == self.card_model {
                captured = Some(
                    self.md.param_names(base_ctor).into_iter().enumerate().map(|(i, n)| (n, call_args.get(i + 1).cloned().unwrap_or(V::Unknown))).collect(),
                );
            } else {
                captured = self.capture_base_call(base_owner, base_ctor, Some(call_args));
            }
            true
        };
        il::evaluate(&self.md, ctor, args, Some(&mut hook));
        captured
    }
}

/// The first parameterless method of that name.
pub fn getter_of(md: &Metadata, ty: u32, name: &str) -> Option<u32> {
    md.methods_of(ty).find(|&m| md.method_name(m) == name && md.method_sig(m).is_ok_and(|s| s.params.is_empty()))
}
