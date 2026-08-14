using System.Runtime.InteropServices;

namespace RemoteControl;

enum MouseButtonKind { Left, Right, Middle }

/// Injects mouse/keyboard events via user32 SendInput.
static class InputInjector
{
    public static void Dispatch(Packet p)
    {
        switch (p.Type)
        {
            case PacketType.Move: MoveRelative(p.Dx, p.Dy); break;
            case PacketType.Scroll: Scroll(p.D); break;
            case PacketType.LClick: Click(MouseButtonKind.Left); break;
            case PacketType.RClick: Click(MouseButtonKind.Right); break;
            case PacketType.MClick: Click(MouseButtonKind.Middle); break;
            case PacketType.LDown: ButtonState(MouseButtonKind.Left, true); break;
            case PacketType.LUp: ButtonState(MouseButtonKind.Left, false); break;
            case PacketType.Key: if (p.Ch != '\0') SendChar(p.Ch); break;
            case PacketType.VkDown: KeyState((ushort)p.K, true); break;
            case PacketType.VkUp: KeyState((ushort)p.K, false); break;
            case PacketType.VkTap: KeyTap((ushort)p.K); break;
            case PacketType.Text: if (p.Text != null) SendText(p.Text); break;
            case PacketType.Combo: if (p.Keys != null) Combo(p.Keys); break;
        }
    }

    // All keys down (in order), then all keys up (reverse order), as ONE SendInput call -
    // this is what makes it a real combo (e.g. Ctrl+T) instead of a held modifier plus a
    // separately-dispatched tap: every key's state change lands in the same OS input batch,
    // so there's no window where Windows could see them as anything but pressed together.
    public static void Combo(int[] vks)
    {
        var inputs = new INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++)
            inputs[i] = KeyInput((ushort)vks[i], 0);
        for (int i = 0; i < vks.Length; i++)
            inputs[vks.Length + i] = KeyInput((ushort)vks[vks.Length - 1 - i], KEYEVENTF_KEYUP);

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private const ushort VK_RETURN = 0x0D;

    // Whole block at once instead of one KEY packet per character over the network -
    // the phone's own on-screen keyboard already covers single keystrokes; this is for
    // typing on the phone's real keyboard and sending the finished text like a paste.
    // Goes through the same per-char KEYEVENTF_UNICODE path as SendChar (not a clipboard
    // Ctrl+V) so it keeps working in fields that block paste, e.g. some password boxes.
    public static void SendText(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (char c in normalized)
        {
            if (c == '\n') KeyTap(VK_RETURN);
            else SendChar(c);
        }
    }

    // Relative gesture in, absolute SendInput out - bypasses the pointer-acceleration
    // curve so network timing jitter doesn't distort the delta-to-pixels mapping.
    public static void MoveRelative(double dx, double dy)
    {
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        var cur = System.Windows.Forms.Cursor.Position;

        double targetX = Math.Clamp(cur.X + dx, vs.Left, vs.Right - 1);
        double targetY = Math.Clamp(cur.Y + dy, vs.Top, vs.Bottom - 1);

        int normX = (int)((targetX - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1));
        int normY = (int)((targetY - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1));

        var input = new INPUT
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void Scroll(int notches)
    {
        var input = MouseInput(MOUSEEVENTF_WHEEL, notches * 120);
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void Click(MouseButtonKind button)
    {
        var (down, up) = FlagsFor(button);
        var inputs = new[] { MouseInput(down), MouseInput(up) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void ButtonState(MouseButtonKind button, bool down)
    {
        var (downFlag, upFlag) = FlagsFor(button);
        SendInput(1, new[] { MouseInput(down ? downFlag : upFlag) }, Marshal.SizeOf<INPUT>());
    }

    public static void SendChar(char c)
    {
        var down = KeyInput(0, KEYEVENTF_UNICODE, c);
        var up = KeyInput(0, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, c);
        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    public static void KeyState(ushort vk, bool down)
        => SendInput(1, new[] { KeyInput(vk, down ? 0u : KEYEVENTF_KEYUP) }, Marshal.SizeOf<INPUT>());

    public static void KeyTap(ushort vk)
    {
        var inputs = new[] { KeyInput(vk, 0), KeyInput(vk, KEYEVENTF_KEYUP) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static (uint down, uint up) FlagsFor(MouseButtonKind button) => button switch
    {
        MouseButtonKind.Left => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        MouseButtonKind.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        MouseButtonKind.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    private static INPUT MouseInput(uint flags, int mouseData = 0) => new()
    {
        type = InputMouse,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData } }
    };

    private static INPUT KeyInput(ushort vk, uint flags, char scan = '\0') => new()
    {
        type = InputKeyboard,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } }
    };

    // --- Win32 interop ---

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
