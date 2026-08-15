package com.remotecontrol

import android.content.Context
import android.os.Build
import android.provider.Settings

/** Who this phone install claims to be, for the PC's pairing handshake ([Packet.Hello]).
 *  `id` is Android's ANDROID_ID - stable across app reinstalls on the same device/OS
 *  install, resets on factory reset. Not a hardware serial (Android doesn't expose one to
 *  apps without special permissions), but it's exactly the "recognize this device again"
 *  identity the pairing system actually needs. */
object DeviceIdentity {
    fun id(context: Context): String =
        Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID)
            ?: "unknown-${Build.MODEL}"

    val model: String get() = Build.MODEL
    val buildNumber: String get() = Build.DISPLAY
}
