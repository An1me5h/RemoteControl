package com.remotecontrol

import android.content.SharedPreferences
import org.json.JSONArray
import org.json.JSONObject

/** A user-defined macro button: a label plus an ordered list of VK codes to press together
 *  as one [Packet.Combo] (e.g. "New Tab" -> Ctrl+T -> [VK.CONTROL, 'T'.code]). */
data class CustomKey(val label: String, val keys: List<Int>)

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
