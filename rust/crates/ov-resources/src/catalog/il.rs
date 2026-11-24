//! Port of Catalog/IlEvaluator.cs: a tiny symbolic IL interpreter for straight-line code.
//!
//! It "runs" a method body on a fake evaluation stack. It knows integer constants, strings, simple collections
//! (arrays, HashSet, List ...) and "Unknown". That is enough for constant getters, collection initialisers and
//! constructor calls like `: base(1, CardType.Attack, ...)`. A branch or any other unsupported opcode ends the
//! evaluation with a failure.

use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

use super::meta::{self, Metadata, SigType, Tok};

/// A value on the fake stack.
#[derive(Clone, Debug)]
pub enum V {
    Unknown,
    This,
    Null,
    Int(i32),
    /// A byte element of an InitializeArray blob (C# boxes it as `byte`, which is not an `int`).
    Byte(u8),
    Str(String),
    Coll(Rc<RefCell<Vec<V>>>),
    /// An ldtoken operand.
    Token(Tok),
}

impl V {
    pub fn as_int(&self) -> Option<i32> {
        if let V::Int(v) = self { Some(*v) } else { None }
    }
}

/// Result of evaluating a method body.
#[derive(Clone, Debug)]
pub struct EvalResult {
    pub ok: bool,
    pub value: Option<V>,
    pub failure: Option<String>,
}

impl EvalResult {
    fn fail(why: String) -> Self {
        EvalResult { ok: false, value: None, failure: Some(why) }
    }
}

/// What a call instruction targets (Cecil's MethodReference).
#[derive(Clone, Debug)]
pub struct MethodRef {
    pub tok: Tok,
    pub name: String,
    /// The declaring type as a TypeDefOrRef token.
    pub declaring: Tok,
    pub sig: meta::MethodSig,
    pub generic_args: Vec<SigType>,
}

impl Metadata {
    pub fn method_ref(&self, tok: Tok) -> Option<MethodRef> {
        match tok.table {
            meta::METHOD_DEF => Some(MethodRef {
                tok,
                name: self.method_name(tok.row).to_string(),
                declaring: Tok { table: meta::TYPE_DEF, row: self.method_owner(tok.row)? },
                sig: self.method_sig(tok.row).ok()?,
                generic_args: Vec::new(),
            }),
            meta::MEMBER_REF => {
                let sig = meta::parse_method_sig(self.blob(self.col(meta::MEMBER_REF, tok.row, 2))).ok()?;
                let mut parent = self.member_ref_parent(tok.row);
                if parent.table == meta::METHOD_DEF {
                    parent = Tok { table: meta::TYPE_DEF, row: self.method_owner(parent.row)? };
                }
                Some(MethodRef { tok, name: self.str_col(meta::MEMBER_REF, tok.row, 1).to_string(), declaring: parent, sig, generic_args: Vec::new() })
            }
            meta::METHOD_SPEC => {
                let (m, args) = self.method_spec(tok.row);
                let mut r = self.method_ref(m)?;
                r.tok = tok;
                r.generic_args = args;
                Some(r)
            }
            _ => None,
        }
    }

    /// MethodReference.Resolve(): the MethodDef row, if the method lives in this module.
    pub fn resolve_method(&self, r: &MethodRef) -> Option<u32> {
        match r.tok.table {
            meta::METHOD_DEF => Some(r.tok.row),
            meta::METHOD_SPEC => {
                let (m, _) = self.method_spec(r.tok.row);
                self.resolve_method(&self.method_ref(m)?)
            }
            _ => {
                let ty = self.resolve_type(r.declaring)?;
                self.methods_of(ty).find(|&m| {
                    self.method_name(m) == r.name
                        && self.method_sig(m).is_ok_and(|s| s.has_this == r.sig.has_this && s.params == r.sig.params && s.ret == r.sig.ret)
                })
            }
        }
    }
}

/// Hook called for every call/callvirt with the target and the popped arguments (args[0] is the receiver of an
/// instance method). Returning true stops the evaluation there.
pub type CallHook<'a> = &'a mut dyn FnMut(&MethodRef, &[V]) -> bool;

/// Evaluates a method body. `args`: values for ldarg.N; None = arg0 is `this` (instance methods) and the rest Unknown.
pub fn evaluate(md: &Metadata, method: u32, args: Option<&[V]>, mut on_call: Option<CallHook<'_>>) -> EvalResult {
    let Some(body) = md.method_body(method) else { return EvalResult::fail("no body".into()) };
    let code = match meta::decode_il(body) {
        Ok(c) => c,
        Err(e) => return EvalResult::fail(e),
    };
    let has_this = md.method_sig(method).map(|s| s.has_this).unwrap_or(false);
    let arg = |i: usize| -> V {
        match args {
            Some(a) => a.get(i).cloned().unwrap_or(V::Unknown),
            None => {
                if has_this && i == 0 { V::This } else { V::Unknown }
            }
        }
    };
    let mut stack: Vec<V> = Vec::new();
    let mut locals: HashMap<i64, V> = HashMap::new();
    let pop = |s: &mut Vec<V>| s.pop().unwrap_or(V::Unknown);

    for ins in code {
        match ins.op {
            0x00 => {}
            0x15 => stack.push(V::Int(-1)),
            0x16..=0x1E => stack.push(V::Int(i32::from(ins.op - 0x16))),
            0x1F => stack.push(V::Int(i32::from(ins.operand as u8 as i8))),
            0x20 => stack.push(V::Int(ins.operand as u32 as i32)),
            0x21..=0x23 => stack.push(V::Unknown),
            0x72 => stack.push(V::Str(md.user_string((ins.operand as u32) & 0x00FF_FFFF))),
            0x14 => stack.push(V::Null),
            0xD0 => stack.push(V::Token(Tok::from_token(ins.operand as u32))),

            0x02..=0x05 => stack.push(arg(usize::from(ins.op - 0x02))),
            0x0E | 0xFE09 => stack.push(arg(ins.operand as usize)),
            0x0A..=0x0D => {
                let v = pop(&mut stack);
                locals.insert(i64::from(ins.op - 0x0A), v);
            }
            0x13 | 0xFE0E => {
                let v = pop(&mut stack);
                locals.insert(ins.operand, v);
            }
            0x06..=0x09 => stack.push(locals.get(&i64::from(ins.op - 0x06)).cloned().unwrap_or(V::Unknown)),
            0x11 | 0xFE0C => stack.push(locals.get(&ins.operand).cloned().unwrap_or(V::Unknown)),

            0x25 => {
                let top = pop(&mut stack);
                stack.push(top.clone());
                stack.push(top);
            }
            0x26 => {
                pop(&mut stack);
            }
            0x67 | 0x68 | 0x69 | 0xD2 | 0xD1 | 0x6D | 0x6A | 0x6E => {}

            0x7B => {
                pop(&mut stack);
                stack.push(V::Unknown);
            }
            0x7E => stack.push(V::Unknown),
            0x7D => {
                pop(&mut stack);
                pop(&mut stack);
            }
            0x80 => {
                pop(&mut stack);
            }
            0x7F => stack.push(V::Unknown),
            0x7C => {
                pop(&mut stack);
                stack.push(V::Unknown);
            }
            0xFE06 => stack.push(V::Unknown),
            0xFE07 => {
                pop(&mut stack);
                stack.push(V::Unknown);
            }
            0x8C | 0xA5 | 0x74 | 0x75 => {}

            0x8D => {
                let count = pop(&mut stack);
                match count {
                    V::Int(n) => stack.push(V::Coll(Rc::new(RefCell::new(vec![V::Null; n.max(0) as usize])))),
                    _ => stack.push(V::Unknown),
                }
            }
            0x9B..=0x9F | 0xA2 | 0xA4 => {
                let value = pop(&mut stack);
                let index = pop(&mut stack);
                let array = pop(&mut stack);
                if let (V::Coll(c), V::Int(i)) = (&array, &index) {
                    let mut c = c.borrow_mut();
                    if *i >= 0 && (*i as usize) < c.len() {
                        c[*i as usize] = value;
                    }
                }
            }

            0x28 | 0x6F | 0x73 => {
                let Some(target) = md.method_ref(Tok::from_token(ins.operand as u32)) else {
                    return EvalResult::fail(format!("unreadable call target at IL_{:04x}", ins.offset));
                };
                let is_newobj = ins.op == 0x73;
                let argc = target.sig.params.len() + usize::from(target.sig.has_this && !is_newobj);
                let mut call_args = vec![V::Unknown; argc];
                for i in (0..argc).rev() {
                    call_args[i] = pop(&mut stack);
                }
                if !is_newobj
                    && let Some(hook) = on_call.as_mut()
                    && hook(&target, &call_args)
                {
                    return EvalResult { ok: true, value: None, failure: None };
                }
                let (result, pushes) = simulate_call(md, &target, &call_args, is_newobj);
                if pushes {
                    stack.push(result);
                }
            }

            0x2A => return EvalResult { ok: true, value: Some(stack.pop().unwrap_or(V::Null)), failure: None },

            op => return EvalResult::fail(format!("unsupported opcode {op:#x} at IL_{:04x}", ins.offset)),
        }
    }
    EvalResult::fail("fell off end of method".into())
}

/// Gives a few well-known BCL calls a meaning; everything else returns Unknown.
fn simulate_call(md: &Metadata, target: &MethodRef, args: &[V], is_newobj: bool) -> (V, bool) {
    let type_name = md.tok_name(target.declaring);
    let pushes = is_newobj || target.sig.ret != SigType::Prim(meta::ELEMENT_VOID);

    if target.name == "InitializeArray" && type_name == "RuntimeHelpers" {
        if let (Some(V::Coll(arr)), Some(V::Token(t))) = (args.first(), args.get(1))
            && t.table == meta::FIELD
            && let Some(blob) = md.field_initial_value(t.row)
        {
            let mut arr = arr.borrow_mut();
            let n = arr.len();
            let elem = blob.len().checked_div(n).unwrap_or(0);
            if elem > 0 {
                for (i, slot) in arr.iter_mut().enumerate() {
                    *slot = match elem {
                        1 => V::Byte(blob[i]),
                        2 => V::Int(i32::from(i16::from_le_bytes([blob[i * 2], blob[i * 2 + 1]]))),
                        4 => V::Int(i32::from_le_bytes(blob[i * 4..i * 4 + 4].try_into().unwrap())),
                        _ => V::Unknown,
                    };
                }
            }
        }
        return (V::Null, pushes);
    }

    if target.name == "Empty" && type_name == "Array" {
        return (V::Coll(Rc::default()), pushes);
    }

    if is_newobj && is_collection_type(&type_name) {
        let coll: Rc<RefCell<Vec<V>>> = Rc::default();
        if args.len() == 1 {
            let p0 = &target.sig.params[0];
            let param_is_element = matches!(p0, SigType::Var(_) | SigType::MVar(_));
            if let V::Coll(src) = &args[0] {
                let items = src.borrow().clone();
                coll.borrow_mut().extend(items);
            } else if param_is_element {
                coll.borrow_mut().push(args[0].clone());
            } else if *p0 != SigType::Prim(meta::ELEMENT_I4) {
                return (V::Unknown, pushes);
            }
        } else if args.len() > 1 {
            return (V::Unknown, pushes);
        }
        return (V::Coll(coll), pushes);
    }

    if !is_newobj
        && target.name == "Add"
        && args.len() == 2
        && let V::Coll(c) = &args[0]
    {
        c.borrow_mut().push(args[1].clone());
        return (V::Int(1), pushes);
    }

    (V::Unknown, pushes)
}

fn is_collection_type(name: &str) -> bool {
    name.starts_with("HashSet`")
        || name.starts_with("List`")
        || name.contains("ReadOnlyArray`")
        || name.contains("ReadOnlySingleElementList`")
        || name.contains("ReadOnlyList`")
}
