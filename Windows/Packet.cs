using System.Text.Json;

namespace RemoteControl;

// System.Linq is pulled in via ImplicitUsings for Select/ToArray below.

enum PacketType
{
    Move, Scroll, LClick, RClick, MClick, LDown, LUp,
    Key, VkDown, VkUp, VkTap, Text, Combo, Ping,
    Hello, PairCode, Unknown
}

readonly record struct Packet(
    PacketType Type,
    double Dx = 0,
    double Dy = 0,
    int D = 0,
    char Ch = '\0',
    int K = 0,
    string? Text = null,
    int[]? Keys = null,
    string? DeviceId = null,
    string? Model = null,
    string? Build = null,
    string? Name = null,
    string? Code = null);

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
                "HELLO" => PacketType.Hello,
                "PAIRCODE" => PacketType.PairCode,
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

            string? deviceId = root.TryGetProperty("deviceId", out var idP) ? idP.GetString() : null;
            string? model = root.TryGetProperty("model", out var modelP) ? modelP.GetString() : null;
            string? build = root.TryGetProperty("build", out var buildP) ? buildP.GetString() : null;
            string? name = root.TryGetProperty("name", out var nameP) ? nameP.GetString() : null;
            string? code = root.TryGetProperty("code", out var codeP) ? codeP.GetString() : null;

            return new Packet(type, dx, dy, d, ch, k, text, keys, deviceId, model, build, name, code);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
