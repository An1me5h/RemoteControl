package com.remotecontrol

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.text.Spannable
import android.text.SpannableString
import android.text.style.RelativeSizeSpan
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
 * layout (letters, digits, modifiers, an always-visible arrow/navigation cluster, and a
 * collapsible "Symbols and Functions" section with punctuation, shifted number-row
 * symbols, and F-keys) expressed entirely in Windows virtual-key codes (see [VK]). Has NO
 * dependency on this app's network protocol or any other app
 * file - wire it up by setting [onKeyDown]/[onKeyUp]/[onKeyTap]/[onCombo]/[onCharTyped] to
 * whatever you want done with the result. Meant to be portable: this file, `view_remote_
 * keyboard.xml`, and the `key_bg`/`key_bg_active` drawables + `KeyButton`/`CharKeyButton`
 * styles it references are the only pieces a different app would need to copy over.
 *
 * A deliberate design boundary, not an oversight: the PHONE's own idea of "which modifiers
 * are active" and the PC's REAL, physical keyboard state are kept completely separate.
 * Tapping Ctrl/Alt/Shift/Win only ever flips a LOCAL flag here (see [activeModifiers]) and
 * highlights the button - nothing is sent to the PC just from that tap. The PC's actual
 * keyboard is only ever touched the instant a non-modifier key is tapped while a modifier
 * is active, and even then as ONE atomic [onCombo] (all keys down then up together, never
 * left "held" between calls) - never a real, extended VKDOWN that would leave the PC's
 * physical Ctrl/Shift/etc. genuinely pressed for as long as the phone happens to show it
 * highlighted. This matters because the earlier design (a real held VKDOWN for the whole
 * time a modifier looked "active") meant the PC's own physical keyboard was actually
 * affected for that entire window - not just misleading, but a real risk if anything else
 * was typed at the PC directly during it.
 *
 * Gestures:
 *   quick tap on a character/action key -> [onKeyTap]/[onCharTyped] with nothing active,
 *                                           or [onCombo] with the active modifiers folded in
 *   hold [holdThresholdMs] on a
 *     character/action key             -> [onKeyDown], stays "held" (a real, intentional
 *                                           sustained press - e.g. holding an arrow key to
 *                                           auto-repeat) until a later tap -> [onKeyUp].
 *                                           Deliberately NOT extended to modifiers - see above.
 *   tap on Ctrl/Alt/Shift/Win           -> toggles it in/out of [activeModifiers] - local
 *                                           only, sends nothing by itself
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

        private val MODIFIERS = setOf(SHIFT, CONTROL, ALT, LWIN)
        fun isModifier(vk: Int) = vk in MODIFIERS

        /** VK for 'A'-'Z'/'0'-'9' matches ASCII; punctuation maps to its own OEM code
         *  (standard US layout). Used both for modifier combos (activate Ctrl, tap C) and
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

    /** Engaged hold on a non-modifier key - see the class doc comment for why modifiers
     *  never go through this. The host should press [vk] and keep it down until the
     *  matching [onKeyUp]. */
    var onKeyDown: ((vk: Int) -> Unit)? = null
    /** Matching release for a previous [onKeyDown]. */
    var onKeyUp: ((vk: Int) -> Unit)? = null
    /** A single press+release of one key, nothing active. */
    var onKeyTap: ((vk: Int) -> Unit)? = null
    /** One or more keys pressed and released together ATOMICALLY (active modifiers + the
     *  key just tapped) - never a separately-held modifier plus a later tap. */
    var onCombo: ((keys: List<Int>) -> Unit)? = null
    /** A plain character typed with no modifier active - not a VK combo, just layout-
     *  independent text entry. */
    var onCharTyped: ((ch: Char) -> Unit)? = null
    /** Fired on every change while a custom-key recording is active (see [startRecording])
     *  with the keys held so far, in order. */
    var onRecordingChanged: ((keys: List<Int>) -> Unit)? = null

    var holdThresholdMs: Long = 2000L

    private val heldKeys = mutableMapOf<Int, Button>()
    private val holdHandler = Handler(Looper.getMainLooper())
    private val letterButtons = mutableListOf<Button>()

    /** Locally-tracked "active" modifiers - see the class doc comment. Never mirrors a
     *  real, extended PC-side key-down; only ever folded into the next [onCombo]. */
    private val activeModifiers = mutableSetOf<Int>()
    private val modifierButtons = mutableMapOf<Int, Button>()

    private var isRecording = false
    private val recordingKeys = mutableListOf<Int>()
    private val recordingButtons = mutableMapOf<Int, Button>()

    private val symbolsContent: LinearLayout
    private val btnToggleSymbols: Button

    init {
        LayoutInflater.from(context).inflate(R.layout.view_remote_keyboard, this, true)

        symbolsContent = findViewById(R.id.symbolsContent)
        btnToggleSymbols = findViewById(R.id.btnToggleSymbols)

        val actionKeys = mapOf(
            R.id.keyEsc to VK.ESC,
            R.id.keyTab to VK.TAB,
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
            wireHoldable(findViewById(id), vk) { dispatchTap(vk) }
        }

        val modifierKeys = mapOf(
            R.id.keyCtrl to VK.CONTROL,
            R.id.keyAlt to VK.ALT,
            R.id.keyShift to VK.SHIFT,
            R.id.keyWin to VK.LWIN
        )
        for ((id, vk) in modifierKeys) {
            wireModifier(findViewById(id), vk)
        }

        btnToggleSymbols.text = collapsibleHeaderText(expanded = false, label = "Symbols and Functions")
        btnToggleSymbols.setOnClickListener {
            val expand = symbolsContent.visibility != View.VISIBLE
            symbolsContent.visibility = if (expand) View.VISIBLE else View.GONE
            btnToggleSymbols.text = collapsibleHeaderText(expand, "Symbols and Functions")
        }

        assignCharKeys(this)
    }

    /** Releases every currently held (non-modifier) key and clears active modifiers - call
     *  from the host's onPause so backgrounding the app can never leave a real, sustained
     *  key-down stuck on the PC. Active modifiers never touched the PC in the first place
     *  (see the class doc comment), so clearing them is purely a visual reset. */
    fun releaseAllHeld() {
        for ((vk, button) in heldKeys) {
            onKeyUp?.invoke(vk)
            setActive(button, false)
        }
        heldKeys.clear()
        clearActiveModifiers()
    }

    /** Same bookkeeping as [releaseAllHeld] but sends nothing - for when there's no live
     *  connection to send a release over in the first place. */
    fun resetHeldVisuals() {
        for (button in heldKeys.values) setActive(button, false)
        heldKeys.clear()
        clearActiveModifiers()
    }

    private fun clearActiveModifiers() {
        for (vk in activeModifiers) modifierButtons[vk]?.let { setActive(it, false) }
        activeModifiers.clear()
        relabelLetters()
    }

    /** Starts capturing a custom-key combo: the normal hold gesture on any key (including
     *  modifiers, via [wireModifier]'s own recording branch) adds it to the combo instead
     *  of its usual live effect. */
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

    /** Builds the label for a collapsible section's toggle button: an arrow glyph, sized up
     *  relative to the rest of the text since a plain "▸"/"▾" at normal text size reads as
     *  barely more than a period, followed by [label]. */
    private fun collapsibleHeaderText(expanded: Boolean, label: String): SpannableString {
        val arrow = if (expanded) "▾" else "▸"
        val full = "$arrow $label"
        val spannable = SpannableString(full)
        spannable.setSpan(RelativeSizeSpan(1.8f), 0, arrow.length, Spannable.SPAN_EXCLUSIVE_EXCLUSIVE)
        return spannable
    }

    private fun setActive(button: Button, active: Boolean) {
        button.setBackgroundResource(if (active) R.drawable.key_bg_active else R.drawable.key_bg)
        button.setTextColor(context.getColor(if (active) R.color.bg else R.color.text_primary))
    }

    /** A non-character action key was tapped (not held) - fold in whatever modifiers are
     *  currently active as one atomic combo, or send a plain tap if none are. */
    private fun dispatchTap(vk: Int) {
        if (activeModifiers.isEmpty()) {
            onKeyTap?.invoke(vk)
        } else {
            onCombo?.invoke(activeModifiers.toList() + vk)
        }
    }

    /** Ctrl/Alt/Shift/Win: a plain tap toggles LOCAL "active" state only - see the class
     *  doc comment for why this never sends anything to the PC by itself. During custom-key
     *  RECORDING, though, these still need the same hold-to-capture gesture every other key
     *  uses (a combo needs to know exactly which modifiers to bake in), which is why this
     *  keeps its own small hold-timer rather than reusing [wireHoldable] wholesale - the
     *  live (non-recording) tap behavior is fundamentally different from every other key's. */
    private fun wireModifier(button: Button, vk: Int) {
        modifierButtons[vk] = button
        var holdEngagedThisPress = false
        val holdRunnable = Runnable {
            if (!isRecording) return@Runnable
            holdEngagedThisPress = true
            button.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
            if (vk !in recordingKeys) {
                recordingKeys.add(vk)
                recordingButtons[vk] = button
                setActive(button, true)
                onRecordingChanged?.invoke(recordingKeys.toList())
            }
        }

        button.setOnTouchListener { _, event ->
            if (isRecording) {
                when (event.actionMasked) {
                    MotionEvent.ACTION_DOWN -> {
                        if (vk !in recordingKeys) {
                            holdEngagedThisPress = false
                            holdHandler.postDelayed(holdRunnable, holdThresholdMs)
                        }
                    }
                    MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> holdHandler.removeCallbacks(holdRunnable)
                }
            }
            false
        }

        button.setOnClickListener {
            if (isRecording) {
                if (holdEngagedThisPress) {
                    holdEngagedThisPress = false
                } else if (vk in recordingKeys) {
                    recordingKeys.remove(vk)
                    recordingButtons.remove(vk)
                    setActive(button, false)
                    onRecordingChanged?.invoke(recordingKeys.toList())
                }
                return@setOnClickListener
            }
            if (vk in activeModifiers) {
                activeModifiers.remove(vk)
                setActive(button, false)
            } else {
                activeModifiers.add(vk)
                setActive(button, true)
            }
            if (vk == VK.SHIFT) relabelLetters()
        }
    }

    /** Letter keys show uppercase while Shift is active - purely a visual cue (the char
     *  captured in each button's own closure at wiring time stays lowercase forever; the
     *  actual case sent is decided by [sendChar]/[dispatchTap] at send time, not by
     *  whatever the button currently displays). */
    private fun relabelLetters() {
        val upper = VK.SHIFT in activeModifiers
        for (button in letterButtons) {
            val tag = button.tag as String
            button.text = if (upper) tag.uppercase() else tag
        }
    }

    /** Wires [button] so a quick tap (release before [holdThresholdMs]) fires [onQuickTap],
     *  while holding it down instead engages hold mode: sends [vk] down and leaves it held -
     *  even after the finger lifts - until a later tap on the same button releases it. Only
     *  used for non-modifier keys - see the class doc comment for why modifiers ([wireModifier])
     *  deliberately don't get this treatment live, only while recording.
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
     *  punctuation [VK.forChar] knows), plain-tap-only for anything that somehow doesn't. */
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
        if (activeModifiers.isNotEmpty()) {
            val vk = VK.forChar(ch)
            if (vk != null) {
                // Includes Shift+letter for a capital - one atomic combo, so the PC's own
                // OS-level key translation produces the correct uppercase character exactly
                // like a physical keyboard would, with nothing left held afterward.
                onCombo?.invoke(activeModifiers.toList() + vk)
                return
            }
        }
        onCharTyped?.invoke(ch)
    }
}
