package com.remotecontrol

import org.json.JSONArray
import org.json.JSONObject

/** Mirrors the packet shapes RemoteControl.exe's PacketCodec understands. */
sealed class Packet {
    abstract fun encode(): String

    data class Move(val dx: Float, val dy: Float) : Packet() {
        override fun encode() = """{"t":"MOVE","dx":$dx,"dy":$dy}"""
    }

    data class Scroll(val d: Int) : Packet() {
        override fun encode() = """{"t":"SCROLL","d":$d}"""
    }

    object LeftClick : Packet() { override fun encode() = """{"t":"LCLICK"}""" }
    object RightClick : Packet() { override fun encode() = """{"t":"RCLICK"}""" }
    object MiddleClick : Packet() { override fun encode() = """{"t":"MCLICK"}""" }
    object LeftDown : Packet() { override fun encode() = """{"t":"LDOWN"}""" }
    object LeftUp : Packet() { override fun encode() = """{"t":"LUP"}""" }
    object Ping : Packet() { override fun encode() = """{"t":"PING"}""" }

    data class Key(val ch: Char) : Packet() {
        override fun encode(): String =
            JSONObject().put("t", "KEY").put("ch", ch.toString()).toString()
    }

    data class VkDown(val k: Int) : Packet() { override fun encode() = """{"t":"VKDOWN","k":$k}""" }
    data class VkUp(val k: Int) : Packet() { override fun encode() = """{"t":"VKUP","k":$k}""" }
    data class VkTap(val k: Int) : Packet() { override fun encode() = """{"t":"VKTAP","k":$k}""" }

    /** A whole block of text typed on the phone's real keyboard, sent at once on Send
     *  rather than one KEY packet per character. */
    data class Text(val text: String) : Packet() {
        override fun encode(): String =
            JSONObject().put("t", "TEXT").put("text", text).toString()
    }

    /** A pasted/picked image (JPEG-encoded, base64) sent to the PC's own clipboard - the
     *  same idea as [Text], but the destination is the clipboard instead of the keyboard.
     *  See ClipboardHelper.SetImage on the Windows side. */
    data class Image(val base64Jpeg: String) : Packet() {
        override fun encode(): String =
            JSONObject().put("t", "IMAGE").put("data", base64Jpeg).toString()
    }

    /** Multiple VK codes pressed together as one real combo (e.g. Ctrl+T) - the PC presses
     *  all of them down, then all up, in a single input batch, not a held-modifier-plus-
     *  separate-tap sequence. Used by custom keys ([CustomKey]) and could be sent directly
     *  for a one-off combo too. */
    data class Combo(val keys: List<Int>) : Packet() {
        override fun encode(): String =
            JSONObject().put("t", "COMBO").put("keys", JSONArray(keys)).toString()
    }

    /** First thing sent on every connection, before anything else - see PairingCoordinator
     *  on the Windows side. Identifies this phone install so the PC can recognize it on
     *  future connections without re-pairing. */
    data class Hello(val deviceId: String, val model: String, val build: String, val name: String) : Packet() {
        override fun encode(): String = JSONObject()
            .put("t", "HELLO")
            .put("deviceId", deviceId)
            .put("model", model)
            .put("build", build)
            .put("name", name)
            .toString()
    }

    /** Answers a PAIRREQUIRED from the PC with the code the user typed in. */
    data class PairCode(val code: String) : Packet() {
        override fun encode(): String = JSONObject().put("t", "PAIRCODE").put("code", code).toString()
    }

    /** Windows virtual-key codes used by the special-keys row and modifier combos. */
    object VK {
        const val BACK = 0x08
        const val TAB = 0x09
        const val ENTER = 0x0D
        const val SHIFT = 0x10
        const val CONTROL = 0x11
        const val ALT = 0x12
        const val ESC = 0x1B
        const val SPACE = 0x20
        const val LEFT = 0x25
        const val UP = 0x26
        const val RIGHT = 0x27
        const val DOWN = 0x28
        const val DELETE = 0x2E
        const val LWIN = 0x5B
        const val OEM_COMMA = 0xBC
        const val OEM_PERIOD = 0xBE

        /** VK codes for 'A'-'Z' / '0'-'9' match their ASCII codes; comma/period map to their
         *  own OEM codes. Used for modifier combos (e.g. holding Ctrl then tapping 'c') where
         *  a real VK press is needed instead of a layout-independent unicode KEY event, and
         *  for hold-mode (see [MainActivity.attachHoldable]), which only makes sense for a key
         *  with a real VK code - holding a synthetic unicode char down has no OS meaning.
         *  Returns null for anything else (other punctuation isn't mapped). */
        fun forChar(c: Char): Int? {
            val upper = c.uppercaseChar()
            return when {
                upper in 'A'..'Z' || upper in '0'..'9' -> upper.code
                c == ',' -> OEM_COMMA
                c == '.' -> OEM_PERIOD
                else -> null
            }
        }
    }
}
