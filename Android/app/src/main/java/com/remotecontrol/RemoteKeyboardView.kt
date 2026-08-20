package com.remotecontrol

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.AttributeSet
import android.view.HapticFeedbackConstants
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView

/**
 * Self-contained on-screen "remote keyboard" for driving a Windows PC - a full QWERTY
 * layout (letters, digits with shifted symbols, punctuation, F-keys, modifiers, CapsLock,
 * and an Fn-held navigation cluster) expressed entirely in Windows virtual-key codes (see
 * [VK]). Has NO dependency on this app's network protocol or any other app file - wire it
 * up by setting [onKeyDown]/[onKeyUp]/[onKeyTap]/[onCharTyped] to whatever you want done
 * with the result. Meant to be portable: this file, `view_remote_keyboard.xml`, and the
 * `key_bg`/`key_bg_active` drawables + `KeyButton`/`CharKeyButton` styles it references are
 * the only pieces a different app would need to copy over.
 *
 * Gestures, uniform across every key:
 *   quick tap              -> [onKeyTap] for a non-character key, or [onCharTyped] for a
 *                              plain character key with nothing currently held
 *   hold [holdThresholdMs] -> [onKeyDown], stays "held" (even after lifting) until a LATER
 *                              tap on the same key releases it -> [onKeyUp]. This is also
 *                              how modifiers combo with another key: hold Ctrl, tap C.
 *   CapsLock               -> a real toggle (not hold-mode) - flips on/off on each tap,
 *                              highlights while on, and re-labels every letter key
 *                              uppercase/lowercase to match. [onCharTyped] delivers the
 *                              already-case-corrected letter, since character delivery is
 *                              typically layout-independent unicode injection on the
 *                              receiving end, which bypasses whatever real keyboard state
 *                              (including CapsLock) it's actually in.
 *   Fn held down           -> reveals the navigation cluster (arrows, Home/End, PageUp/
 *                              PageDown) for as long as it's held, same as a real keyboard.
 */
class RemoteKeyboardView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : ScrollView(context, attrs) {

    /** Standard Windows virtual-key codes - not specific to any wire protocol, just what
     *  the receiving end's own input-injection API (e.g. SendInput) expects. */
    object VK {
        const val BACK = 0x08
        const val TAB = 0x09
        const val ENTER = 0x0D
        const val SHIFT = 0x10
        const val CONTROL = 0x11
        const val ALT = 0x12
        const val CAPS_LOCK = 0x14
        const val ESC = 0x1B
        const val SPACE = 0x20
        const val PAGE_UP = 0x21
        const val PAGE_DOWN = 0x22
        const val END = 0x23
        const val HOME = 0x24
        const val LEFT = 0x25
        const val UP = 0x26
        const val RIGHT = 0x27
        const val DOWN = 0x28
        const val PRINT_SCREEN = 0x2C
        const val DELETE = 0x2E
        const val LWIN = 0x5B
        const val F1 = 0x70
        const val F2 = 0x71
        const val F3 = 0x72
        const val F4 = 0x73
        const val F5 = 0x74
        const val F6 = 0x75
        const val F7 = 0x76
        const val F8 = 0x77
        const val F9 = 0x78
        const val F10 = 0x79
        const val F11 = 0x7A
        const val F12 = 0x7B
        const val OEM_MINUS = 0xBD    // - _
        const val OEM_PLUS = 0xBB     // = +
        const val OEM_COMMA = 0xBC    // , <
        const val OEM_PERIOD = 0xBE   // . >
        const val OEM_1 = 0xBA        // ; :
        const val OEM_2 = 0xBF        // / ?
        const val OEM_3 = 0xC0        // ` ~
        const val OEM_4 = 0xDB        // [ {
        const val OEM_5 = 0xDC        // \ |
        const val OEM_6 = 0xDD        // ] }
        const val OEM_7 = 0xDE        // ' "

        /** VK for 'A'-'Z'/'0'-'9' matches ASCII; punctuation maps to its own OEM code
         *  (standard US layout). Used both for modifier combos (hold Ctrl, tap C) and
         *  hold-mode itself, which only makes sense for a key with a real VK - holding a
         *  synthetic unicode character down has no OS-level meaning to hold. Returns null
         *  for anything not on a standard US keyboard. */
        fun forChar(c: Char): Int? {
            val upper = c.uppercaseChar()
            return when {
                upper in 'A'..'Z' || upper in '0'..'9' -> upper.code
                c == ',' -> OEM_COMMA
                c == '.' -> OEM_PERIOD
                c == '-' -> OEM_MINUS
                c == '=' -> OEM_PLUS
                c == ';' -> OEM_1
                c == '\'' -> OEM_7
                c == '/' -> OEM_2
                c == '`' -> OEM_3
                c == '[' -> OEM_4
                c == '\\' -> OEM_5
                c == ']' -> OEM_6
                else -> null
            }
        }
    }

    /** Engaged hold or a modifier going down - the host should press [vk] and keep it down
     *  until the matching [onKeyUp]. */
    var onKeyDown: ((vk: Int) -> Unit)? = null
    /** Matching release for a previous [onKeyDown]. */
    var onKeyUp: ((vk: Int) -> Unit)? = null
    /** A single press+release: either a non-character action key (Esc, Enter, F-keys, ...),
     *  or a character key tapped while a modifier is currently held - the host needs the
     *  real VK there, not a layout-independent character, for the combo to mean anything. */
    var onKeyTap: ((vk: Int) -> Unit)? = null
    /** A plain character typed with nothing held - not a VK combo, just layout-independent
     *  text entry. Already case-corrected for the CapsLock toggle. */
    var onCharTyped: ((ch: Char) -> Unit)? = null
    /** Fired on every change while a custom-key recording is active (see [startRecording])
     *  with the keys held so far, in order. */
    var onRecordingChanged: ((keys: List<Int>) -> Unit)? = null

    var holdThresholdMs: Long = 2000L

    private val heldKeys = mutableMapOf<Int, Button>()
    private val holdHandler = Handler(Looper.getMainLooper())
    private var isCapsLockOn = false
    private val letterButtons = mutableListOf<Button>()

    private var isRecording = false
    private val recordingKeys = mutableListOf<Int>()
    private val recordingButtons = mutableMapOf<Int, Button>()

    private val fnNavContainer: LinearLayout
    private val keyCapsLock: Button

    init {
        LayoutInflater.from(context).inflate(R.layout.view_remote_keyboard, this, true)

        fnNavContainer = findViewById(R.id.fnNavContainer)
        keyCapsLock = findViewById(R.id.keyCapsLock)
        val keyFn = findViewById<Button>(R.id.keyFn)

        val actionKeys = mapOf(
            R.id.keyEsc to VK.ESC,
            R.id.keyTab to VK.TAB,
            R.id.keyCtrl to VK.CONTROL,
            R.id.keyAlt to VK.ALT,
            R.id.keyShift to VK.SHIFT,
            R.id.keyWin to VK.LWIN,
            R.id.keyBack to VK.BACK,
            R.id.keyEnter to VK.ENTER,
            R.id.keySpace to VK.SPACE,
            R.id.keyLeft to VK.LEFT,
            R.id.keyUp to VK.UP,
            R.id.keyDown to VK.DOWN,
            R.id.keyRight to VK.RIGHT,
            R.id.keyHome to VK.HOME,
            R.id.keyEnd to VK.END,
            R.id.keyPageUp to VK.PAGE_UP,
            R.id.keyPageDown to VK.PAGE_DOWN,
            R.id.keyPrintScreen to VK.PRINT_SCREEN,
            R.id.keyDelete to VK.DELETE,
            R.id.keyF1 to VK.F1, R.id.keyF2 to VK.F2, R.id.keyF3 to VK.F3,
            R.id.keyF4 to VK.F4, R.id.keyF5 to VK.F5, R.id.keyF6 to VK.F6,
            R.id.keyF7 to VK.F7, R.id.keyF8 to VK.F8, R.id.keyF9 to VK.F9,
            R.id.keyF10 to VK.F10, R.id.keyF11 to VK.F11, R.id.keyF12 to VK.F12
        )
        for ((id, vk) in actionKeys) {
            wireHoldable(findViewById(id), vk) { onKeyTap?.invoke(vk) }
        }

        // CapsLock is a real toggle, not hold-mode - holding it down doesn't mean anything
        // on a real keyboard either.
        keyCapsLock.setOnClickListener {
            isCapsLockOn = !isCapsLockOn
            setActive(keyCapsLock, isCapsLockOn)
            for (button in letterButtons) {
                val tag = button.tag as String
                button.text = if (isCapsLockOn) tag.uppercase() else tag
            }
            onKeyTap?.invoke(VK.CAPS_LOCK)
        }

        // Not hold-mode either (attachHoldable's engage-after-threshold doesn't apply) -
        // Fn reveals the nav cluster for exactly as long as it's physically held, same as
        // a real keyboard's Fn key.
        keyFn.setOnTouchListener { _, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> fnNavContainer.visibility = View.VISIBLE
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> fnNavContainer.visibility = View.GONE
            }
            true
        }

        assignCharKeys(this)
    }

    /** Releases every currently held key (real VKUP for each, since the receiving end has
     *  no other way to find out) - call this from the host's onPause so backgrounding the
     *  app can never leave something stuck down remotely. */
    fun releaseAllHeld() {
        for ((vk, button) in heldKeys) {
            onKeyUp?.invoke(vk)
            setActive(button, false)
        }
        heldKeys.clear()
    }

    /** Same bookkeeping as [releaseAllHeld] but sends nothing - for when there's no live
     *  connection to send a release over in the first place (e.g. the connection already
     *  dropped), only the button highlights need resetting. */
    fun resetHeldVisuals() {
        for (button in heldKeys.values) setActive(button, false)
        heldKeys.clear()
    }

    /** Starts capturing a custom-key combo: the normal hold gesture on any key adds it to
     *  the combo (via [onRecordingChanged]) instead of firing [onKeyDown]/[onKeyTap]/
     *  [onCharTyped] - nothing reaches the host's real callbacks while recording. */
    fun startRecording() {
        isRecording = true
        recordingKeys.clear()
        recordingButtons.clear()
    }

    /** Ends recording without returning anything useful - un-highlights whatever was
     *  recorded so far. Use [finishRecording] instead if the in-progress combo should be
     *  kept. */
    fun cancelRecording() {
        isRecording = false
        for (button in recordingButtons.values) setActive(button, false)
        recordingKeys.clear()
        recordingButtons.clear()
    }

    /** Ends recording and returns whatever combo was built, in order (possibly empty). */
    fun finishRecording(): List<Int> {
        val result = recordingKeys.toList()
        cancelRecording()
        return result
    }

    private fun setActive(button: Button, active: Boolean) {
        button.setBackgroundResource(if (active) R.drawable.key_bg_active else R.drawable.key_bg)
        button.setTextColor(context.getColor(if (active) R.color.bg else R.color.text_primary))
    }

    /** Wires [button] so a quick tap (release before [holdThresholdMs]) fires [onQuickTap],
     *  while holding it down instead engages hold mode: sends [vk] down and leaves it held -
     *  even after the finger lifts - until a later tap on the same button releases it.
     *
     *  The touch listener never consumes events (always returns false), so the normal click
     *  listener still fires on release regardless of how long the touch lasted -
     *  `holdEngagedThisPress` is what tells the click handler whether this release is a
     *  plain tap, or the lift-off of a press that already engaged hold mode (in which case
     *  the key stays down; that release shouldn't also act as a release-tap). */
    private fun wireHoldable(button: Button, vk: Int, onQuickTap: () -> Unit) {
        var holdEngagedThisPress = false
        val holdRunnable = Runnable {
            holdEngagedThisPress = true
            button.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
            if (isRecording) {
                if (vk !in recordingKeys) {
                    recordingKeys.add(vk)
                    recordingButtons[vk] = button
                    setActive(button, true)
                    onRecordingChanged?.invoke(recordingKeys.toList())
                }
            } else {
                heldKeys[vk] = button
                onKeyDown?.invoke(vk)
                setActive(button, true)
            }
        }

        button.setOnTouchListener { _, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    if (vk !in heldKeys && vk !in recordingKeys) {
                        holdEngagedThisPress = false
                        holdHandler.postDelayed(holdRunnable, holdThresholdMs)
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    holdHandler.removeCallbacks(holdRunnable)
                }
            }
            false
        }

        button.setOnClickListener {
            if (isRecording) {
                if (holdEngagedThisPress) {
                    holdEngagedThisPress = false
                } else if (vk in recordingKeys) {
                    // A plain tap on an already-recorded key removes it - undoes a mis-hold
                    // without leaving recording mode. A tap on a key NOT yet recorded does
                    // nothing (only a deliberate hold adds one).
                    recordingKeys.remove(vk)
                    recordingButtons.remove(vk)
                    setActive(button, false)
                    onRecordingChanged?.invoke(recordingKeys.toList())
                }
                return@setOnClickListener
            }
            if (holdEngagedThisPress) {
                holdEngagedThisPress = false
            } else if (vk in heldKeys) {
                heldKeys.remove(vk)
                onKeyUp?.invoke(vk)
                setActive(button, false)
            } else {
                onQuickTap()
            }
        }
    }

    /** Reads the single char each key carries in android:tag and wires it - hold-capable
     *  via [wireHoldable] for anything with a VK mapping (letters, digits, all the
     *  punctuation [VK.forChar] knows), plain-tap-only for anything that somehow doesn't
     *  (there's nothing like that in the shipped layout right now, but a host could add a
     *  key `VK.forChar` doesn't cover). If a modifier is currently held, letters/digits/
     *  punctuation go through a real VK tap so the combo (e.g. Ctrl+S) actually reaches the
     *  OS; everything else falls back to a plain unicode character. */
    private fun assignCharKeys(root: ViewGroup) {
        for (i in 0 until root.childCount) {
            val child = root.getChildAt(i)
            if (child is ViewGroup) {
                assignCharKeys(child)
            } else if (child is Button) {
                val tag = child.tag as? String
                if (tag != null && tag.length == 1) {
                    val ch = tag[0]
                    if (ch.isLetter()) letterButtons.add(child)
                    val vk = VK.forChar(ch)
                    if (vk != null) {
                        wireHoldable(child, vk) { sendChar(ch) }
                    } else {
                        child.setOnClickListener {
                            if (isRecording) return@setOnClickListener
                            sendChar(ch)
                        }
                    }
                }
            }
        }
    }

    private fun sendChar(ch: Char) {
        if (isRecording) return // wireHoldable's hold path already handles recording for
                                 // keys with a VK; a character with no VK just has nothing
                                 // meaningful to record, so a plain tap here is a no-op.
        val anyModifierHeld = VK.CONTROL in heldKeys || VK.ALT in heldKeys ||
            VK.SHIFT in heldKeys || VK.LWIN in heldKeys
        val vk = if (anyModifierHeld) VK.forChar(ch) else null
        if (vk != null) {
            onKeyTap?.invoke(vk)
        } else {
            // Unicode injection on the receiving end typically bypasses its real keyboard
            // state entirely, including whatever CapsLock state it's actually in - so this
            // view's OWN isCapsLockOn decides the case sent, not a hope that the receiving
            // end's real state happens to match what was last toggled.
            val effective = if (isCapsLockOn && ch.isLetter()) ch.uppercaseChar() else ch
            onCharTyped?.invoke(effective)
        }
    }
}
