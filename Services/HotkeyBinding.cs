namespace WindowsDictation.Services;

internal readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    internal const uint Alt = 0x0001;
    internal const uint Control = 0x0002;
    internal const uint Shift = 0x0004;
    internal const uint Windows = 0x0008;
    private const uint ModifierMask = Alt | Control | Shift | Windows;

    internal static readonly HotkeyBinding Default = new(Control | Alt, 0xDC);

    internal bool IsValid =>
        Modifiers != 0 &&
        (Modifiers & ~ModifierMask) == 0 &&
        VirtualKey is > 0 and <= 0xFF &&
        !IsModifierKey(VirtualKey);

    internal IReadOnlyList<string> KeyLabels
    {
        get
        {
            var labels = new List<string>(5);
            if ((Modifiers & Windows) != 0) labels.Add("⊞");
            if ((Modifiers & Control) != 0) labels.Add("Ctrl");
            if ((Modifiers & Alt) != 0) labels.Add("Alt");
            if ((Modifiers & Shift) != 0) labels.Add("⇧");
            labels.Add(KeyText(VirtualKey));
            return labels;
        }
    }

    internal string DisplayText => string.Join(" + ", KeyLabels);

    internal static bool IsModifierKey(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;

    private static string KeyText(uint key)
    {
        if (key is >= 0x30 and <= 0x39 || key is >= 0x41 and <= 0x5A) return ((char)key).ToString();
        if (key is >= 0x70 and <= 0x87) return $"F{key - 0x6F}";

        return key switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x60 => "Num 0",
            0x61 => "Num 1",
            0x62 => "Num 2",
            0x63 => "Num 3",
            0x64 => "Num 4",
            0x65 => "Num 5",
            0x66 => "Num 6",
            0x67 => "Num 7",
            0x68 => "Num 8",
            0x69 => "Num 9",
            0x6A => "Num *",
            0x6B => "Num +",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"VK {key:X2}",
        };
    }
}
