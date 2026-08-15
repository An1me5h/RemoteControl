namespace RemoteControl;

/// Turns a decoded [Packet] into a human-readable line for the console log.
static class PacketFormat
{
    private static readonly Dictionary<int, string> KnownVk = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x10] = "Shift",
        [0x11] = "Ctrl", [0x12] = "Alt", [0x1B] = "Esc", [0x20] = "Space",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down", [0x5B] = "Win"
    };

    public static string Describe(Packet p) => p.Type switch
    {
        PacketType.Move => DescribeMove(p),
        PacketType.Scroll => $"SCROLL   {p.D} notch(es)",
        PacketType.LClick => "CLICK    left",
        PacketType.RClick => "CLICK    right",
        PacketType.MClick => "CLICK    middle",
        PacketType.LDown => "DRAG     start (left button down)",
        PacketType.LUp => "DRAG     end (left button up)",
        PacketType.Key => $"KEY      '{p.Ch}'",
        PacketType.VkDown => $"VK DOWN  {VkName(p.K)}",
        PacketType.VkUp => $"VK UP    {VkName(p.K)}",
        PacketType.VkTap => $"VK TAP   {VkName(p.K)}",
        PacketType.Text => DescribeText(p),
        PacketType.Combo => DescribeCombo(p),
        PacketType.Ping => "PING",
        PacketType.Hello => $"HELLO    {p.Name ?? p.Model} ({p.Model}, build {p.Build}, id {p.DeviceId})",
        PacketType.PairCode => "PAIRCODE (code hidden from the log)",
        _ => p.Type.ToString()
    };

    // "from this point to this angle with this much value" - magnitude + angle alongside
    // the raw dx/dy, since a vector is often easier to eyeball than two separate deltas.
    private static string DescribeMove(Packet p)
    {
        double magnitude = Math.Sqrt(p.Dx * p.Dx + p.Dy * p.Dy);
        double angleDeg = Math.Atan2(-p.Dy, p.Dx) * 180.0 / Math.PI; // screen Y grows downward
        return $"MOVE     dx={p.Dx,6:0.0} dy={p.Dy,6:0.0}   |   {magnitude,5:0.0}px @ {angleDeg,6:0.0}deg";
    }

    private static string VkName(int k) => KnownVk.TryGetValue(k, out var name) ? $"{name} (0x{k:X2})" : $"0x{k:X2}";

    private static string DescribeText(Packet p)
    {
        var text = p.Text ?? "";
        var oneLine = text.Replace("\n", "\\n");
        var preview = oneLine.Length <= 60 ? oneLine : oneLine[..60] + "...";
        return $"TEXT     \"{preview}\" ({text.Length} chars)";
    }

    private static string DescribeCombo(Packet p)
    {
        var names = (p.Keys ?? Array.Empty<int>()).Select(VkName);
        return $"COMBO    {string.Join(" + ", names)}";
    }
}
