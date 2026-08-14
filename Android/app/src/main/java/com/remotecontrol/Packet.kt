package com.remotecontrol

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

        /** VK codes for 'A'-'Z' / '0'-'9' match their ASCII codes - used for modifier combos
         *  (e.g. holding Ctrl then tapping 'c') where a real VK press is needed instead of
         *  a layout-independent unicode KEY event. Returns null for anything else. */
        fun forChar(c: Char): Int? {
            val upper = c.uppercaseChar()
            return if (upper in 'A'..'Z' || upper in '0'..'9') upper.code else null
        }
    }
}
