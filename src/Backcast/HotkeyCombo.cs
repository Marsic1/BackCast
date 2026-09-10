
namespace Backcast;

/// <summary>
/// WebStage-style hotkey combo: modifiers + key, e.g. Ctrl+Shift+R.
/// Serialized as "Ctrl+Shift+R"; single keys ("F9") stay valid.
/// Supports the Win modifier like WebStage (MOD_WIN).
/// </summary>
public readonly record struct HotkeyCombo(Keys Modifiers, Keys Key)
{
    public bool IsSet => Key != Keys.None;

    // the full modifier set this app understands (Win included)
    internal const Keys ModMask = Keys.Control | Keys.Shift | Keys.Alt;

    public override string ToString()
    {
        if (!IsSet) return "";
        List<string> parts = new();
        if (Modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public static HotkeyCombo Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return None;
        Keys mods = Keys.None, key = Keys.None;
        foreach (var part in s.Split('+'))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Keys.Control; break;
                case "shift": mods |= Keys.Shift; break;
                case "alt": mods |= Keys.Alt; break;
                default:
                    if (Enum.TryParse(part.Trim(), ignoreCase: true, out Keys k) &&
                        !k.HasFlag(Keys.Control) && !k.HasFlag(Keys.Shift) && !k.HasFlag(Keys.Alt))
                        key = k;
                    break;
            }
        }
        return new HotkeyCombo(mods, key);
    }

    public static HotkeyCombo None => default;

    public bool Matches(Keys modifiers, Keys key) =>
        IsSet && key == Key &&
        (modifiers & ModMask) == Modifiers;
}
