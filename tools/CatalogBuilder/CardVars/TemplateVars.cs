namespace CatalogBuilder.CardVars;

/// <summary>
/// Finds the variable names a SmartFormat template refers to, e.g.
/// "Deal {Damage:diff()} damage{InCombat: ({CalculatedDamage:diff()})|}." -> Damage, InCombat, CalculatedDamage.
/// Every '{' starts a placeholder (nested ones too); its selector runs to the first ':' or '}'.
/// An empty selector ("{:diff()}") means "the current value" of the enclosing placeholder and is skipped.
/// "Foo.Bar" counts as Foo (the top-level variable).
/// </summary>
internal static class TemplateVars
{
    public static IEnumerable<string> Referenced(string template)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') continue;
            int end = template.IndexOfAny([':', '}', '{'], i + 1);
            if (end < 0) break;
            string selector = template[(i + 1)..end].Trim();
            int dot = selector.IndexOf('.');
            if (dot >= 0) selector = selector[..dot];
            if (selector.Length > 0 && seen.Add(selector)) yield return selector;
        }
    }
}
