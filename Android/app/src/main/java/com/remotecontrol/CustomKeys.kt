package com.remotecontrol

import android.content.SharedPreferences
import org.json.JSONArray
import org.json.JSONObject

/** A user-defined macro button: a label plus an ordered list of VK codes to press together
 *  as one [Packet.Combo] (e.g. "New Tab" -> Ctrl+T -> [VK.CONTROL, 'T'.code]). */
data class CustomKey(val label: String, val keys: List<Int>)

/** The "then press" choices offered in the add-custom-key dialog's spinner - name shown
 *  to the user, paired with the VK code actually sent. */
object KeyCatalog {
    val entries: List<Pair<String, Int>> = buildList {
        add("Enter" to Packet.VK.ENTER)
        add("Tab" to Packet.VK.TAB)
        add("Esc" to Packet.VK.ESC)
        add("Space" to Packet.VK.SPACE)
        add("Backspace" to Packet.VK.BACK)
        add("Delete" to Packet.VK.DELETE)
        add("Up" to Packet.VK.UP)
        add("Down" to Packet.VK.DOWN)
        add("Left" to Packet.VK.LEFT)
        add("Right" to Packet.VK.RIGHT)
        for (c in 'A'..'Z') add(c.toString() to c.code)
        for (c in '0'..'9') add(c.toString() to c.code)
    }
}

/** Persists the user's custom keys as a single JSON array under one SharedPreferences key. */
object CustomKeyStore {
    private const val PREF_KEY = "custom_keys"

    fun load(prefs: SharedPreferences): List<CustomKey> {
        val raw = prefs.getString(PREF_KEY, null) ?: return emptyList()
        return try {
            val array = JSONArray(raw)
            (0 until array.length()).map { i ->
                val obj = array.getJSONObject(i)
                val keysArray = obj.getJSONArray("keys")
                val keys = (0 until keysArray.length()).map { keysArray.getInt(it) }
                CustomKey(obj.getString("label"), keys)
            }
        } catch (e: Exception) {
            emptyList()
        }
    }

    fun save(prefs: SharedPreferences, customKeys: List<CustomKey>) {
        val array = JSONArray()
        for (key in customKeys) {
            val obj = JSONObject()
            obj.put("label", key.label)
            obj.put("keys", JSONArray(key.keys))
            array.put(obj)
        }
        prefs.edit().putString(PREF_KEY, array.toString()).apply()
    }
}
