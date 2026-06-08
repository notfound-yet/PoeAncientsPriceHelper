using SharpHook.Data;

namespace PoeAncientsPriceHelper;

// Pure (no-WPF, no-hook) helpers for the configurable Start/Stop hotkey, kept separate so the
// parsing/display/reserved logic is unit-testable without standing up the window or global hook.
//
// The binding is stored, captured, and matched as a single SharpHook KeyCode — the same value the
// hook reports — so there is no WPF-Key ↔ KeyCode mapping to maintain.
internal static class HotkeyBinding
{
    public const KeyCode Default = KeyCode.VcF5;

    // Keys the app already uses for fixed actions (F3 debug, F4 calibrate, Esc/Ctrl dismiss). Binding
    // Start/Stop to one of these would make a single press fire two actions, so capture rejects them.
    // Esc additionally doubles as "cancel capture".
    public static readonly IReadOnlyList<KeyCode> Reserved =
    [
        KeyCode.VcF3, KeyCode.VcF4, KeyCode.VcEscape,
        KeyCode.VcLeftControl, KeyCode.VcRightControl,
    ];

    public static bool IsReserved(KeyCode key) => Reserved.Contains(key);

    // config.json round-trip: store the enum name ("VcF5") for a human-readable, int-churn-proof file.
    public static string ToStorage(KeyCode key) => key.ToString();

    public static KeyCode Parse(string? stored) =>
        Enum.TryParse<KeyCode>(stored, ignoreCase: false, out var key) && Enum.IsDefined(key)
            ? key
            : Default;

    // Friendly label for the UI: SharpHook names are "Vc"-prefixed (VcF5, VcA, Vc1) — strip it.
    public static string Display(KeyCode key)
    {
        var name = key.ToString();
        return name.StartsWith("Vc", StringComparison.Ordinal) ? name[2..] : name;
    }
}
