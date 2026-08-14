package com.remotecontrol

import android.app.AlertDialog
import android.content.SharedPreferences
import android.os.Bundle
import android.view.View
import android.view.ViewGroup
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.Spinner
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity

class MainActivity : AppCompatActivity() {

    private lateinit var prefs: SharedPreferences
    private lateinit var conn: ConnectionManager

    private lateinit var statusDot: View
    private lateinit var statusText: TextView
    private lateinit var latencyText: TextView
    private lateinit var tabControl: Button
    private lateinit var tabConfig: Button
    private lateinit var controlPanel: View
    private lateinit var configPanel: View
    private lateinit var trackpadView: TrackpadView
    private lateinit var keyboardPanel: ScrollView
    private lateinit var btnKeys: Button
    private lateinit var textPanel: View
    private lateinit var etTextInput: EditText
    private lateinit var btnSendText: Button
    private lateinit var btnText: Button
    private lateinit var customKeysContainer: LinearLayout
    private lateinit var btnAddCustomKey: Button
    private lateinit var etHost: EditText
    private lateinit var etPort: EditText
    private lateinit var sensitivityLabel: TextView
    private lateinit var sensitivitySeek: SeekBar
    private lateinit var btnConnect: Button
    private lateinit var logText: TextView

    private val heldModifiers = mutableMapOf(
        Packet.VK.CONTROL to false,
        Packet.VK.ALT to false,
        Packet.VK.SHIFT to false,
        Packet.VK.LWIN to false
    )

    private val customKeys = mutableListOf<CustomKey>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        prefs = getSharedPreferences("remotecontrol", MODE_PRIVATE)
        conn = ConnectionManager()

        bindViews()
        setupTabs()
        setupTrackpad()
        setupClickButtons()
        setupPanelToggles()
        setupKeyboard()
        setupCustomKeys()
        setupTextPanel()
        setupConfig()
        setupConnection()
    }

    private fun bindViews() {
        statusDot = findViewById(R.id.statusDot)
        statusText = findViewById(R.id.statusText)
        latencyText = findViewById(R.id.latencyText)
        tabControl = findViewById(R.id.tabControl)
        tabConfig = findViewById(R.id.tabConfig)
        controlPanel = findViewById(R.id.controlPanel)
        configPanel = findViewById(R.id.configPanel)
        trackpadView = findViewById(R.id.trackpadView)
        keyboardPanel = findViewById(R.id.keyboardPanel)
        btnKeys = findViewById(R.id.btnKeys)
        textPanel = findViewById(R.id.textPanel)
        etTextInput = findViewById(R.id.etTextInput)
        btnSendText = findViewById(R.id.btnSendText)
        btnText = findViewById(R.id.btnText)
        customKeysContainer = findViewById(R.id.customKeysContainer)
        btnAddCustomKey = findViewById(R.id.btnAddCustomKey)
        etHost = findViewById(R.id.etHost)
        etPort = findViewById(R.id.etPort)
        sensitivityLabel = findViewById(R.id.sensitivityLabel)
        sensitivitySeek = findViewById(R.id.sensitivitySeek)
        btnConnect = findViewById(R.id.btnConnect)
        logText = findViewById(R.id.logText)
    }

    private fun setupTabs() {
        tabControl.setOnClickListener { selectTab(control = true) }
        tabConfig.setOnClickListener { selectTab(control = false) }
        selectTab(control = true)
    }

    private fun selectTab(control: Boolean) {
        controlPanel.visibility = if (control) View.VISIBLE else View.GONE
        configPanel.visibility = if (control) View.GONE else View.VISIBLE
        tabControl.setTextColor(resources.getColor(if (control) R.color.accent else R.color.text_secondary, theme))
        tabConfig.setTextColor(resources.getColor(if (control) R.color.text_secondary else R.color.accent, theme))
    }

    private fun setupTrackpad() {
        trackpadView.onPacket = { conn.send(it) }
    }

    private fun setupClickButtons() {
        findViewById<Button>(R.id.btnLeft).setOnClickListener { conn.send(Packet.LeftClick) }
        findViewById<Button>(R.id.btnRight).setOnClickListener { conn.send(Packet.RightClick) }
        findViewById<Button>(R.id.btnMiddle).setOnClickListener { conn.send(Packet.MiddleClick) }
    }

    /** The centre panel is always exactly one of: trackpad, on-screen keyboard, or the
     *  type-and-send text box - they never coexist, same reasoning as the original
     *  trackpad/keyboard swap (no room for more than one at a usable size on a phone). */
    private enum class CenterPanel { PAD, KEYS, TEXT }

    private fun setupPanelToggles() {
        btnKeys.setOnClickListener {
            showPanel(if (currentPanel == CenterPanel.KEYS) CenterPanel.PAD else CenterPanel.KEYS)
        }
        btnText.setOnClickListener {
            showPanel(if (currentPanel == CenterPanel.TEXT) CenterPanel.PAD else CenterPanel.TEXT)
        }
    }

    private var currentPanel = CenterPanel.PAD

    private fun showPanel(panel: CenterPanel) {
        currentPanel = panel
        trackpadView.visibility = if (panel == CenterPanel.PAD) View.VISIBLE else View.GONE
        keyboardPanel.visibility = if (panel == CenterPanel.KEYS) View.VISIBLE else View.GONE
        textPanel.visibility = if (panel == CenterPanel.TEXT) View.VISIBLE else View.GONE
        btnKeys.setBackgroundResource(if (panel == CenterPanel.KEYS) R.drawable.key_bg_active else R.drawable.key_bg)
        btnText.setBackgroundResource(if (panel == CenterPanel.TEXT) R.drawable.key_bg_active else R.drawable.key_bg)
    }

    private fun setupTextPanel() {
        btnSendText.setOnClickListener {
            val text = etTextInput.text.toString()
            if (text.isNotEmpty()) {
                conn.send(Packet.Text(text))
                etTextInput.text.clear()
            }
        }
    }

    private fun setupKeyboard() {
        val specialTaps = mapOf(
            R.id.keyEsc to Packet.VK.ESC,
            R.id.keyTab to Packet.VK.TAB,
            R.id.keyBack to Packet.VK.BACK,
            R.id.keyEnter to Packet.VK.ENTER,
            R.id.keySpace to Packet.VK.SPACE,
            R.id.keyLeft to Packet.VK.LEFT,
            R.id.keyUp to Packet.VK.UP,
            R.id.keyDown to Packet.VK.DOWN,
            R.id.keyRight to Packet.VK.RIGHT
        )
        for ((id, vk) in specialTaps) {
            findViewById<Button>(id).setOnClickListener { conn.send(Packet.VkTap(vk)) }
        }

        val modifierButtons = mapOf(
            R.id.keyCtrl to Packet.VK.CONTROL,
            R.id.keyAlt to Packet.VK.ALT,
            R.id.keyShift to Packet.VK.SHIFT,
            R.id.keyWin to Packet.VK.LWIN
        )
        for ((id, vk) in modifierButtons) {
            val button = findViewById<Button>(id)
            button.setOnClickListener { toggleModifier(vk, button) }
        }

        assignCharKeys(findViewById(R.id.keyboardPanel))
    }

    private fun toggleModifier(vk: Int, button: Button) {
        val nowHeld = !(heldModifiers[vk] ?: false)
        heldModifiers[vk] = nowHeld
        conn.send(if (nowHeld) Packet.VkDown(vk) else Packet.VkUp(vk))
        button.setBackgroundResource(if (nowHeld) R.drawable.key_bg_active else R.drawable.key_bg)
    }

    /** Reads the char each key carries in android:tag and wires its click. If a modifier is
     *  currently held, letters/digits go through a real VK tap so the combo (e.g. Ctrl+C)
     *  reaches Windows; everything else always falls back to a plain unicode KEY send. */
    private fun assignCharKeys(root: ViewGroup) {
        for (i in 0 until root.childCount) {
            val child = root.getChildAt(i)
            if (child is ViewGroup) {
                assignCharKeys(child)
            } else if (child is Button) {
                val tag = child.tag as? String
                if (tag != null && tag.length == 1) {
                    val ch = tag[0]
                    child.setOnClickListener { sendChar(ch) }
                }
            }
        }
    }

    private fun sendChar(ch: Char) {
        val anyModifierHeld = heldModifiers.values.any { it }
        val vk = if (anyModifierHeld) Packet.VK.forChar(ch) else null
        if (vk != null) {
            conn.send(Packet.VkTap(vk))
        } else {
            conn.send(Packet.Key(ch))
        }
    }

    private fun setupCustomKeys() {
        customKeys.addAll(CustomKeyStore.load(prefs))
        renderCustomKeys()
        btnAddCustomKey.setOnClickListener { showAddCustomKeyDialog() }
    }

    /** Rebuilds customKeysContainer from [customKeys], two buttons per row. Tap sends the
     *  combo; long-press offers to delete - the only way to remove one, since there's no
     *  edit mode, just add/delete.
     *
     *  Buttons here are built in code, not inflated from XML, so the `KeyButton` style's
     *  `layout_*` attributes (width/height/weight/margin) don't apply automatically - a
     *  style only supplies View-level attributes (background, textColor, ...) to a
     *  programmatically constructed view; LayoutParams still have to be set explicitly. */
    private fun renderCustomKeys() {
        customKeysContainer.removeAllViews()
        var row: LinearLayout? = null
        customKeys.forEachIndexed { i, key ->
            if (i % 2 == 0) {
                row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
                customKeysContainer.addView(row)
            }
            val margin = dp(2)
            val button = Button(this, null, 0, R.style.KeyButton).apply {
                text = key.label
                layoutParams = LinearLayout.LayoutParams(0, dp(44), 1f).apply {
                    setMargins(margin, margin, margin, margin)
                }
                setOnClickListener { conn.send(Packet.Combo(key.keys)) }
                setOnLongClickListener { confirmDeleteCustomKey(key); true }
            }
            row?.addView(button)
        }
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    private fun confirmDeleteCustomKey(key: CustomKey) {
        AlertDialog.Builder(this)
            .setTitle("Delete \"${key.label}\"?")
            .setPositiveButton("Delete") { _, _ ->
                customKeys.remove(key)
                CustomKeyStore.save(prefs, customKeys)
                renderCustomKeys()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun showAddCustomKeyDialog() {
        val view = layoutInflater.inflate(R.layout.dialog_custom_key, null)
        val etLabel = view.findViewById<EditText>(R.id.etCustomKeyLabel)
        val cbCtrl = view.findViewById<CheckBox>(R.id.cbCustomCtrl)
        val cbAlt = view.findViewById<CheckBox>(R.id.cbCustomAlt)
        val cbShift = view.findViewById<CheckBox>(R.id.cbCustomShift)
        val cbWin = view.findViewById<CheckBox>(R.id.cbCustomWin)
        val spinner = view.findViewById<Spinner>(R.id.spinnerCustomKey)
        spinner.adapter = ArrayAdapter(
            this, android.R.layout.simple_spinner_dropdown_item, KeyCatalog.entries.map { it.first }
        )

        AlertDialog.Builder(this)
            .setTitle("Custom key")
            .setView(view)
            .setPositiveButton("Save") { _, _ ->
                val modifiers = listOfNotNull(
                    Packet.VK.CONTROL.takeIf { cbCtrl.isChecked },
                    Packet.VK.ALT.takeIf { cbAlt.isChecked },
                    Packet.VK.SHIFT.takeIf { cbShift.isChecked },
                    Packet.VK.LWIN.takeIf { cbWin.isChecked }
                )
                val (keyName, keyVk) = KeyCatalog.entries[spinner.selectedItemPosition]
                val keys = modifiers + keyVk
                val label = etLabel.text.toString().trim().ifEmpty {
                    (modifiers.map { vkLabel(it) } + keyName).joinToString("+")
                }
                customKeys.add(CustomKey(label, keys))
                CustomKeyStore.save(prefs, customKeys)
                renderCustomKeys()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun vkLabel(vk: Int) = when (vk) {
        Packet.VK.CONTROL -> "Ctrl"
        Packet.VK.ALT -> "Alt"
        Packet.VK.SHIFT -> "Shift"
        Packet.VK.LWIN -> "Win"
        else -> "0x%02X".format(vk)
    }

    private fun setupConfig() {
        etHost.setText(prefs.getString("host", ""))
        etPort.setText(prefs.getInt("port", ConnectionManager.DEFAULT_PORT).toString())

        val savedSensitivity = prefs.getFloat("sensitivity", 1.4f)
        trackpadView.sensitivity = savedSensitivity
        sensitivitySeek.progress = (((savedSensitivity - 0.2f) / 0.1f).toInt()).coerceIn(0, sensitivitySeek.max)
        sensitivityLabel.text = "Sensitivity: %.1f".format(savedSensitivity)

        sensitivitySeek.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(seekBar: SeekBar, progress: Int, fromUser: Boolean) {
                val value = 0.2f + progress * 0.1f
                trackpadView.sensitivity = value
                sensitivityLabel.text = "Sensitivity: %.1f".format(value)
                prefs.edit().putFloat("sensitivity", value).apply()
            }
            override fun onStartTrackingTouch(seekBar: SeekBar) {}
            override fun onStopTrackingTouch(seekBar: SeekBar) {}
        })
    }

    private fun setupConnection() {
        conn.onStateChanged = { state ->
            val (drawable, text) = when (state) {
                ConnectionManager.State.DISCONNECTED -> R.drawable.dot_red to "Disconnected"
                ConnectionManager.State.CONNECTING -> R.drawable.dot_yellow to "Connecting..."
                ConnectionManager.State.CONNECTED -> R.drawable.dot_green to "Connected to ${conn.currentHost}"
            }
            statusDot.setBackgroundResource(drawable)
            statusText.text = text
            btnConnect.text = if (state == ConnectionManager.State.DISCONNECTED) "Connect" else "Disconnect"
            if (state != ConnectionManager.State.CONNECTED) latencyText.visibility = View.GONE
        }
        conn.onLog = { msg -> logText.text = msg }
        conn.onLatency = { rtt ->
            latencyText.visibility = View.VISIBLE
            latencyText.text = "${rtt}ms"
        }

        btnConnect.setOnClickListener {
            if (btnConnect.text == "Connect") {
                val host = etHost.text.toString().trim().ifEmpty { null }
                val port = etPort.text.toString().toIntOrNull() ?: ConnectionManager.DEFAULT_PORT
                prefs.edit().putString("host", host ?: "").putInt("port", port).apply()
                conn.connect(host, port)
            } else {
                conn.disconnect()
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        conn.disconnect()
    }
}
