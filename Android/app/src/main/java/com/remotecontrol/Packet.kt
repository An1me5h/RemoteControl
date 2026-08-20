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
}
