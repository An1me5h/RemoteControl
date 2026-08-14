using System.Text.Json;

namespace RemoteControl;

// System.Linq is pulled in via ImplicitUsings for Select/ToArray below.

enum PacketType
{
    Move, Scroll, LClick, RClick, MClick, LDown, LUp,
    Key, VkDown, VkUp, VkTap, Text, Combo, Ping, Unknown
}

readonly record struct Packet(
    PacketType Type,
    double Dx = 0,
    double Dy = 0,
    int D = 0,
    char Ch = '\0',
    int K = 0,
    string? Text = null,
    int[]? Keys = null);

static class PacketCodec
{
    public static Packet? Decode(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var tProp)) return null;

            var type = tProp.GetString() switch
            {
                "MOVE" => PacketType.Move,
                "SCROLL" => PacketType.Scroll,
                "LCLICK" => PacketType.LClick,
                "RCLICK" => PacketType.RClick,
                "MCLICK" => PacketType.MClick,
                "LDOWN" => PacketType.LDown,
                "LUP" => PacketType.LUp,
                "KEY" => PacketType.Key,
                "VKDOWN" => PacketType.VkDown,
                "VKUP" => PacketType.VkUp,
                "VKTAP" => PacketType.VkTap,
                "TEXT" => PacketType.Text,
                "COMBO" => PacketType.Combo,
                "PING" => PacketType.Ping,
                _ => PacketType.Unknown
            };
            if (type == PacketType.Unknown) return null;

            double dx = root.TryGetProperty("dx", out var dxP) ? dxP.GetDouble() : 0;
            double dy = root.TryGetProperty("dy", out var dyP) ? dyP.GetDouble() : 0;
            int d = root.TryGetProperty("d", out var dP) ? dP.GetInt32() : 0;
            int k = root.TryGetProperty("k", out var kP) ? kP.GetInt32() : 0;

            char ch = '\0';
            if (root.TryGetProperty("ch", out var chP))
            {
                var s = chP.GetString();
                if (!string.IsNullOrEmpty(s)) ch = s[0];
            }

            string? text = root.TryGetProperty("text", out var textP) ? textP.GetString() : null;

            int[]? keys = null;
            if (root.TryGetProperty("keys", out var keysP) && keysP.ValueKind == JsonValueKind.Array)
            {
                keys = keysP.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            }

            return new Packet(type, dx, dy, d, ch, k, text, keys);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
