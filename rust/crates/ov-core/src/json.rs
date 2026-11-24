//! Null-tolerant accessors over `serde_json::Value`, matching the C# `JsonElement` helpers: a missing value or a
//! value of another kind reads as "absent". Integers must be JSON integers in range (`TryGetInt32`).

use serde_json::Value;

pub fn prop<'a>(e: Option<&'a Value>, name: &str) -> Option<&'a Value> {
    e?.as_object()?.get(name)
}

pub fn array<'a>(e: Option<&'a Value>, name: &str) -> &'a [Value] {
    prop(e, name).and_then(Value::as_array).map(Vec::as_slice).unwrap_or(&[])
}

pub fn obj<'a>(e: Option<&'a Value>, name: &str) -> Option<&'a Value> {
    prop(e, name).filter(|v| v.is_object())
}

pub fn str_<'a>(e: Option<&'a Value>, name: &str) -> Option<&'a str> {
    prop(e, name)?.as_str()
}

pub fn int(e: Option<&Value>, name: &str) -> Option<i32> {
    prop(e, name)?.as_i64().and_then(|v| i32::try_from(v).ok())
}

pub fn uint64(e: Option<&Value>, name: &str) -> Option<u64> {
    prop(e, name)?.as_u64()
}

pub fn long(e: Option<&Value>, name: &str) -> Option<i64> {
    prop(e, name)?.as_i64()
}

pub fn double(e: Option<&Value>, name: &str) -> Option<f64> {
    prop(e, name)?.as_f64()
}

pub fn is_true(e: Option<&Value>, name: &str) -> bool {
    matches!(prop(e, name), Some(Value::Bool(true)))
}

pub fn as_str(v: &Value) -> Option<&str> {
    v.as_str()
}

/// Strings of an array property, skipping non-strings.
pub fn strings(e: Option<&Value>, name: &str) -> Vec<String> {
    array(e, name).iter().filter_map(Value::as_str).map(str::to_string).collect()
}

/// UTF-8 decode, skipping a BOM. Invalid bytes become U+FFFD (as .NET's decoder does).
pub fn decode(bytes: &[u8]) -> String {
    let b = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(bytes);
    String::from_utf8_lossy(b).into_owned()
}
