package com.remotecontrol

import android.content.SharedPreferences
import org.json.JSONArray
import org.json.JSONObject

/** A saved connection target: which PC/TV/Pi/etc. to control, so switching between
 *  multiple devices on the same network doesn't mean retyping an IP every time. */
data class SavedDevice(val name: String, val host: String, val port: Int, val deviceType: String)

/** The device-type choices offered when saving a device, each with a small icon glyph
 *  shown next to it in the saved-devices list. "Other" covers anything not listed - a
 *  fridge, a smart display, whatever else ends up running the Windows side. */
object DeviceTypeCatalog {
    val types = listOf("TV", "Laptop", "Desktop / PC", "Monitor / Screen", "Raspberry Pi", "Smart Fridge", "Tailscale / Remote", "Other")

    fun icon(type: String): String = when (type) {
        "TV" -> "📺"
        "Laptop" -> "💻"
        "Desktop / PC" -> "🖥️"
        "Monitor / Screen" -> "🖼️"
        "Raspberry Pi" -> "🍓"
        "Smart Fridge" -> "🧊"
        "Tailscale / Remote" -> "🌐"
        else -> "📦"
    }
}

/** Persists the user's saved devices as a single JSON array under one SharedPreferences key. */
object SavedDeviceStore {
    private const val PREF_KEY = "saved_devices"

    fun load(prefs: SharedPreferences): List<SavedDevice> {
        val raw = prefs.getString(PREF_KEY, null) ?: return emptyList()
        return try {
            val array = JSONArray(raw)
            (0 until array.length()).map { i ->
                val obj = array.getJSONObject(i)
                SavedDevice(
                    obj.getString("name"),
                    obj.getString("host"),
                    obj.getInt("port"),
                    obj.optString("deviceType", "Other")
                )
            }
        } catch (e: Exception) {
            emptyList()
        }
    }

    fun save(prefs: SharedPreferences, devices: List<SavedDevice>) {
        val array = JSONArray()
        for (d in devices) {
            val obj = JSONObject()
            obj.put("name", d.name)
            obj.put("host", d.host)
            obj.put("port", d.port)
            obj.put("deviceType", d.deviceType)
            array.put(obj)
        }
        prefs.edit().putString(PREF_KEY, array.toString()).apply()
    }
}
