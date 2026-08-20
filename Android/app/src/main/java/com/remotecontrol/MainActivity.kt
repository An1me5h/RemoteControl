package com.remotecontrol

import android.app.AlertDialog
import android.content.Intent
import android.content.SharedPreferences
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Bundle
import android.util.Base64
import android.view.View
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.SeekBar
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import com.google.zxing.integration.android.IntentIntegrator
import java.io.ByteArrayOutputStream

class MainActivity : AppCompatActivity() {

    private lateinit var prefs: SharedPreferences
    private lateinit var conn: ConnectionManager
    private lateinit var screen: ScreenClient

    private lateinit var statusDot: View
    private lateinit var statusText: TextView
    private lateinit var latencyText: TextView
    private lateinit var tabControl: Button
    private lateinit var tabConfig: Button
    private lateinit var controlPanel: View
    private lateinit var configPanel: View
    private lateinit var trackpadView: TrackpadView
    private lateinit var screenImage: ZoomableImageView
    private lateinit var screenStatus: TextView
    private lateinit var zoomBadge: TextView
    private lateinit var btnQuality: Button
    private lateinit var btnScreenOff: Button
    private lateinit var keyboardPanel: LinearLayout
    private lateinit var remoteKeyboard: RemoteKeyboardView
    private lateinit var btnKeys: Button
    private lateinit var textPanel: View
    private lateinit var etTextInput: EditText
    private lateinit var btnSendText: Button
    private lateinit var btnText: Button
    private lateinit var imagePreviewRow: View
    private lateinit var ivPastedImage: ImageView
    private lateinit var btnSendImage: Button
    private lateinit var btnClearImage: Button
    private lateinit var btnPickImage: Button

    // Set by pickImageLauncher, consumed (and cleared) by sendPendingImage - the Uris, not
    // decoded bytes, are what's held onto between pick and send, so several large images
    // aren't sitting decoded in memory the whole time the user's still looking at them.
    private val pendingImageUris = mutableListOf<Uri>()

    // Must be registered unconditionally before the Activity reaches STARTED - a class-
    // level property initializer (not something called lazily from inside a click
    // handler) is the correct place for this, per registerForActivityResult's contract.
    // Android's system Photo Picker, supporting multiple selection - clipboard-paste was
    // tried first but turned out too unreliable across different source apps (some
    // populated the clip's declared MIME type, some the ContentResolver's, not
    // consistently either), and doesn't have a multi-select concept anyway. GetMultipleContents
    // (not the newer PickMultipleVisualMedia) for broad AndroidX-version compatibility.
    private val pickImageLauncher = registerForActivityResult(ActivityResultContracts.GetMultipleContents()) { uris ->
        if (uris.isNotEmpty()) stagePendingImages(uris)
    }
    private lateinit var customKeysContainer: LinearLayout
    private lateinit var btnToggleCustomKeys: Button
    private lateinit var customKeysContent: LinearLayout
    private lateinit var btnAddCustomKey: Button
    private lateinit var screenPreviewFrame: FrameLayout
    private lateinit var recordingInstructionBar: TextView
    private lateinit var bottomNavRow: LinearLayout
    private lateinit var recordingButtonBar: LinearLayout
    private lateinit var btnCancelRecording: Button
    private lateinit var btnSaveRecording: Button
    private lateinit var savedDevicesContainer: LinearLayout
    private lateinit var btnSaveDevice: Button
    private lateinit var btnScanQr: Button
    private lateinit var etHost: EditText
    private lateinit var etPort: EditText
    private lateinit var sensitivityLabel: TextView
    private lateinit var sensitivitySeek: SeekBar
    private lateinit var holdThresholdLabel: TextView
    private lateinit var holdThresholdSeek: SeekBar
    private lateinit var btnConnect: Button
    private lateinit var logText: TextView


    private val customKeys = mutableListOf<CustomKey>()
    private val savedDevices = mutableListOf<SavedDevice>()

    private var pairingDialog: AlertDialog? = null

    /** Set by [handlePairIntent] when a QR scan supplied a code up front - [setupConnection]'s
     *  `onPairingRequired` submits it automatically instead of showing the type-it-in dialog. */
    private var pendingQrCode: String? = null
    private var lastPcLabel: String = "this PC"

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        prefs = getSharedPreferences("remotecontrol", MODE_PRIVATE)
        conn = ConnectionManager(applicationContext)
        screen = ScreenClient()

        bindViews()
        setupScreen() // must precede setupTabs() - selecting CONTROL touches ScreenClient
        setupTabs()
        setupTrackpad()
        setupClickButtons()
        setupPanelToggles()
        setupKeyboard()
        setupCustomKeys()
        setupTextPanel()
        setupSavedDevices()
        setupQrScan()
        setupConfig()
        setupConnection()
        handlePairIntent(intent)
    }

    /** singleTop (see AndroidManifest) means a second QR scan while the app is already open
     *  re-delivers here instead of spawning a new Activity instance. */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handlePairIntent(intent)
    }

    /** External camera apps opening `remotecontrol://pair?..` as a deep link land here (see
     *  AndroidManifest's intent-filter) - kept as a bonus path alongside [setupQrScan]'s
     *  in-app scanner below, in case a given phone's camera app does offer to open it. Not
     *  the primary path: relying on an external camera app recognizing a custom URI scheme
     *  turned out unreliable in practice - see ForClaudeUseOnly.md. */
    private fun handlePairIntent(intent: Intent?) = handlePairUri(intent?.data)

    /** Wires the "Scan QR to Connect" button to ZXing's embedded scanner. This is the
     *  reliable path (unlike the external-camera-app deep link above): scanning happens
     *  inside this same Activity/ConnectionManager instance, so there's no risk of a second
     *  app instance/task spinning up a competing connection attempt. */
    private fun setupQrScan() {
        btnScanQr.setOnClickListener {
            IntentIntegrator(this)
                .setPrompt("Scan the PC's pairing QR code")
                .setBeepEnabled(false)
                .setOrientationLocked(false)
                .initiateScan()
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        val result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data)
        val contents = result?.contents ?: return
        handlePairUri(Uri.parse(contents))
    }

    /** Fills the host/port fields exactly like picking a saved device would, then connects
     *  immediately - the scanned code is stashed in [pendingQrCode] so `onPairingRequired`
     *  (below, in [setupConnection]) can submit it without making the user re-type what they
     *  just scanned. Shared by both scan paths above; silently does nothing for a URI that
     *  isn't one of our own pairing links (e.g. someone scans an unrelated QR by mistake). */
    private fun handlePairUri(uri: Uri?) {
        if (uri == null || uri.scheme != "remotecontrol" || uri.host != "pair") return

        val host = uri.getQueryParameter("host")?.takeIf { it.isNotBlank() } ?: return
        val port = uri.getQueryParameter("port")?.toIntOrNull() ?: ConnectionManager.DEFAULT_PORT
        pendingQrCode = uri.getQueryParameter("code")

        etHost.setText(host)
        etPort.setText(port.toString())
        prefs.edit().putString("host", host).putInt("port", port).apply()

        conn.disconnect()
        conn.connect(host, port)
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
        screenImage = findViewById(R.id.screenImage)
        screenStatus = findViewById(R.id.screenStatus)
        zoomBadge = findViewById(R.id.zoomBadge)
        btnQuality = findViewById(R.id.btnQuality)
        btnScreenOff = findViewById(R.id.btnScreenOff)
        keyboardPanel = findViewById(R.id.keyboardPanel)
        remoteKeyboard = findViewById(R.id.remoteKeyboard)
        btnKeys = findViewById(R.id.btnKeys)
        textPanel = findViewById(R.id.textPanel)
        etTextInput = findViewById(R.id.etTextInput)
        btnSendText = findViewById(R.id.btnSendText)
        btnText = findViewById(R.id.btnText)
        imagePreviewRow = findViewById(R.id.imagePreviewRow)
        ivPastedImage = findViewById(R.id.ivPastedImage)
        btnSendImage = findViewById(R.id.btnSendImage)
        btnClearImage = findViewById(R.id.btnClearImage)
        btnPickImage = findViewById(R.id.btnPickImage)
        customKeysContainer = findViewById(R.id.customKeysContainer)
        btnToggleCustomKeys = findViewById(R.id.btnToggleCustomKeys)
        customKeysContent = findViewById(R.id.customKeysContent)
        btnAddCustomKey = findViewById(R.id.btnAddCustomKey)
        screenPreviewFrame = findViewById(R.id.screenPreviewFrame)
        recordingInstructionBar = findViewById(R.id.recordingInstructionBar)
        bottomNavRow = findViewById(R.id.bottomNavRow)
        recordingButtonBar = findViewById(R.id.recordingButtonBar)
        btnCancelRecording = findViewById(R.id.btnCancelRecording)
        btnSaveRecording = findViewById(R.id.btnSaveRecording)
        savedDevicesContainer = findViewById(R.id.savedDevicesContainer)
        btnSaveDevice = findViewById(R.id.btnSaveDevice)
        btnScanQr = findViewById(R.id.btnScanQr)
        etHost = findViewById(R.id.etHost)
        etPort = findViewById(R.id.etPort)
        sensitivityLabel = findViewById(R.id.sensitivityLabel)
        sensitivitySeek = findViewById(R.id.sensitivitySeek)
        holdThresholdLabel = findViewById(R.id.holdThresholdLabel)
        holdThresholdSeek = findViewById(R.id.holdThresholdSeek)
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

        // Streaming is real Wi-Fi/battery cost on both ends, so it only runs while CONTROL
        // is actually the visible tab.
        if (control) startScreenStream() else screen.stop()
    }

    // ── Screen mirroring ─────────────────────────────────────────────────────────

    private fun setupScreen() {
        screen.onFrame = { bitmap ->
            screenImage.setImageBitmap(bitmap)
            screenStatus.visibility = View.GONE
        }

        screenImage.onZoomChanged = { zoom ->
            zoomBadge.text = "%.1fx".format(zoom)
            zoomBadge.visibility = if (zoom > 1.01f) View.VISIBLE else View.GONE
        }

        btnQuality.text = ScreenClient.QUALITY_NAMES[qualityLevel()]
        btnQuality.setOnClickListener {
            setQualityLevel((qualityLevel() + 1) % ScreenClient.QUALITY_NAMES.size)
        }

        // Jumps straight to OFF in one tap instead of cycling through LOW/MED/HIGH/MAX
        // first - same underlying action as cycling all the way around to OFF, just
        // without the intermediate taps.
        btnScreenOff.setOnClickListener { setQualityLevel(ScreenClient.OFF_QUALITY) }

        screen.onStateChange = { state ->
            when (state) {
                ScreenClient.State.STREAMING -> screenStatus.visibility = View.GONE
                ScreenClient.State.CONNECTING -> {
                    screenStatus.text = "Connecting to screen..."
                    screenStatus.visibility = View.VISIBLE
                }
                ScreenClient.State.STOPPED -> {
                    screenStatus.text = "Screen stream disconnected"
                    screenStatus.visibility = View.VISIBLE
                }
            }
        }
    }

    /** Index into ScreenClient.QUALITY_NAMES; cycled by the on-screen quality button. */
    private fun qualityLevel(): Int =
        prefs.getInt("quality", ScreenClient.DEFAULT_QUALITY).coerceIn(0, ScreenClient.QUALITY_NAMES.size - 1)

    /** Shared by btnQuality's cycle-one-step handler and btnScreenOff's jump-straight-to-
     *  OFF handler - the preset is chosen by the server when the stream opens, so applying
     *  a change means reconnecting rather than trying to renegotiate mid-stream. */
    private fun setQualityLevel(level: Int) {
        prefs.edit().putInt("quality", level).apply()
        btnQuality.text = ScreenClient.QUALITY_NAMES[level]
        screen.stop()
        startScreenStream()
    }

    /** Reuses whatever host the input connection resolved (typed, saved, or from a QR
     *  scan) so the screen stream never asks for the IP a second time. */
    private fun startScreenStream() {
        // OFF frees all Wi-Fi airtime for trackpad input - video and input share the same
        // radio, so a heavy stream directly costs cursor responsiveness.
        if (qualityLevel() == ScreenClient.OFF_QUALITY) {
            screen.stop()
            screenStatus.text = "Screen mirroring off - tap quality to re-enable"
            screenStatus.visibility = View.VISIBLE
            return
        }

        // Reads prefs directly, not etHost.text - setupTabs() (which selects CONTROL and
        // triggers this on a cold start) runs before setupConfig() populates that field.
        val host = conn.currentHost ?: prefs.getString("host", null)?.takeIf { it.isNotBlank() }
        if (host.isNullOrBlank()) {
            screenStatus.text = "Set the PC IP in CONFIG first"
            screenStatus.visibility = View.VISIBLE
            return
        }

        screenStatus.text = "Connecting to screen..."
        screenStatus.visibility = View.VISIBLE
        screen.start(host, ScreenClient.PORT, qualityLevel())
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
        setKeyActive(btnKeys, panel == CenterPanel.KEYS)
        setKeyActive(btnText, panel == CenterPanel.TEXT)
    }

    /** Swaps a button between the normal (dark bg, light text) and active (light mint bg,
     *  dark text) look - key_bg_active's fill is the same light accent color used
     *  throughout this app, so text_primary (near-white, the normal-state color every
     *  button here uses) reads as low-contrast on top of it. Every "active" toggle in this
     *  file (held keys, the KEYS/TEXT panel toggle) goes through this now instead of
     *  swapping only the background, which is what left the text unreadable. */
    private fun setKeyActive(button: Button, active: Boolean) {
        button.setBackgroundResource(if (active) R.drawable.key_bg_active else R.drawable.key_bg)
        button.setTextColor(getColor(if (active) R.color.bg else R.color.text_primary))
    }

    private fun setupTextPanel() {
        btnSendText.setOnClickListener {
            val text = etTextInput.text.toString()
            if (text.isNotEmpty()) {
                conn.send(Packet.Text(text))
                etTextInput.text.clear()
            }
        }

        btnPickImage.setOnClickListener { pickImageLauncher.launch("image/*") }
        btnClearImage.setOnClickListener { clearPendingImages() }
        btnSendImage.setOnClickListener { sendPendingImages() }
    }

    private fun stagePendingImages(uris: List<Uri>) {
        pendingImageUris.clear()
        pendingImageUris.addAll(uris)
        ivPastedImage.setImageURI(uris[0])
        btnSendImage.text = if (uris.size > 1) "Send ${uris.size} Images" else "Send Image"
        imagePreviewRow.visibility = View.VISIBLE
    }

    private fun clearPendingImages() {
        pendingImageUris.clear()
        ivPastedImage.setImageDrawable(null)
        imagePreviewRow.visibility = View.GONE
    }

    /** Sends every staged image, one packet each, in order. Each is downscaled first
     *  (1600px max edge, JPEG quality 80) - a picked image can be a full camera-resolution
     *  photo, and this travels over the same newline-delimited TCP channel as every input
     *  packet; keeping each payload to a few hundred KB instead of several MB keeps that
     *  channel responsive for whatever else is sharing it. Runs synchronously on the UI
     *  thread - a brief decode/compress stall on an explicit "Send" tap (even for several
     *  images) is an acceptable tradeoff against the complexity of a background-thread
     *  handoff for what's a deliberate, infrequent action, not a hot path like MOVE
     *  packets. One failed image (unreadable Uri, decode error) is reported and skipped
     *  rather than abandoning the rest of the batch. */
    private fun sendPendingImages() {
        if (pendingImageUris.isEmpty()) return
        var failures = 0
        for (uri in pendingImageUris) {
            try {
                val original = contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it) }
                if (original == null) {
                    failures++
                    continue
                }

                val maxDim = 1600
                val scale = minOf(1f, maxDim.toFloat() / maxOf(original.width, original.height))
                val scaled = if (scale < 1f) {
                    Bitmap.createScaledBitmap(
                        original, (original.width * scale).toInt(), (original.height * scale).toInt(), true)
                } else original

                val out = ByteArrayOutputStream()
                scaled.compress(Bitmap.CompressFormat.JPEG, 80, out)
                // NO_WRAP is not optional here - the default MIME-style encoding inserts
                // line breaks, which would fragment this packet across multiple lines on a
                // protocol that reads one JSON object per line (Server.cs's ReadLineAsync).
                val base64 = Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP)
                conn.send(Packet.Image(base64))

                if (scaled !== original) scaled.recycle()
                original.recycle()
            } catch (e: Exception) {
                failures++
            }
        }

        if (failures > 0) {
            Toast.makeText(this, "Sent, but $failures image(s) failed", Toast.LENGTH_SHORT).show()
        }
        clearPendingImages()
    }

    /** RemoteKeyboardView (a standalone, portable component - see that file) owns every
     *  gesture (hold-to-combo, CapsLock toggle, Fn-held nav reveal, custom-key recording)
     *  itself; this just wires its plain-VK-int callbacks to the actual wire protocol. */
    private fun setupKeyboard() {
        remoteKeyboard.onKeyDown = { vk -> conn.send(Packet.VkDown(vk)) }
        remoteKeyboard.onKeyUp = { vk -> conn.send(Packet.VkUp(vk)) }
        remoteKeyboard.onKeyTap = { vk -> conn.send(Packet.VkTap(vk)) }
        remoteKeyboard.onCombo = { keys -> conn.send(Packet.Combo(keys)) }
        remoteKeyboard.onCharTyped = { ch -> conn.send(Packet.Key(ch)) }
    }

    /** Releases every currently-held key - called on [onPause] so backgrounding the app
     *  (switching away, locking the phone) can never leave a key stuck down on the PC. */
    private fun releaseAllHeldKeys() = remoteKeyboard.releaseAllHeld()

    /** Same bookkeeping as [releaseAllHeldKeys] but without sending anything - for when
     *  the connection itself is already gone (no socket to send a VKUP over even if we
     *  wanted to), not just the app backgrounding while the connection stays alive. */
    private fun resetHeldKeysVisual() = remoteKeyboard.resetHeldVisuals()

    private fun setupCustomKeys() {
        customKeys.addAll(CustomKeyStore.load(prefs))
        renderCustomKeys()
        btnAddCustomKey.setOnClickListener { startRecordingCustomKey() }
        btnCancelRecording.setOnClickListener { cancelRecordingCustomKey() }
        btnSaveRecording.setOnClickListener { finishRecordingCustomKey() }
        // Collapsed by default (see the layout comment) - the keyboard itself is already
        // tall enough that an always-expanded list here pushed it out of view.
        btnToggleCustomKeys.setOnClickListener {
            val expand = customKeysContent.visibility != View.VISIBLE
            customKeysContent.visibility = if (expand) View.VISIBLE else View.GONE
            btnToggleCustomKeys.text = (if (expand) "▾" else "▸") + " Custom Keys (${customKeys.size})"
        }
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
                // Buttons built this way (plain Button(context, null, 0, style), not
                // inflated from XML) don't go through AppCompat's inflater, which is what
                // normally applies the theme's centered-text default - without this, text
                // renders left-aligned instead of centered like every XML-declared button.
                gravity = android.view.Gravity.CENTER
                layoutParams = LinearLayout.LayoutParams(0, dp(44), 1f).apply {
                    setMargins(margin, margin, margin, margin)
                }
                setOnClickListener { conn.send(Packet.Combo(key.keys)) }
                setOnLongClickListener { confirmDeleteCustomKey(key); true }
            }
            row?.addView(button)
        }
        // Keeps the collapsed header's count accurate after an add/delete without needing
        // to toggle the section open and closed - the arrow glyph reflects current state,
        // read back from the content's own visibility rather than tracked separately.
        val expanded = customKeysContent.visibility == View.VISIBLE
        btnToggleCustomKeys.text = (if (expanded) "▾" else "▸") + " Custom Keys (${customKeys.size})"
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

    /** Replaces the old spinner-and-checkboxes dialog: hold down any key(s) in the KEYS
     *  section directly (same long-press gesture used everywhere else in this app -
     *  RemoteKeyboardView's own recording mode handles the actual capture) to add them to
     *  the combo, in order, then tap Save. The screen preview and bottom nav are swapped
     *  out for an instruction bar and a Save/Cancel bar so the keyboard is the obvious,
     *  undistracted focus while recording. */
    private fun startRecordingCustomKey() {
        showPanel(CenterPanel.KEYS)
        screenPreviewFrame.visibility = View.GONE
        bottomNavRow.visibility = View.GONE
        recordingInstructionBar.visibility = View.VISIBLE
        recordingButtonBar.visibility = View.VISIBLE
        remoteKeyboard.onRecordingChanged = { keys -> updateRecordingInstruction(keys) }
        remoteKeyboard.startRecording()
        updateRecordingInstruction(emptyList())
    }

    private fun cancelRecordingCustomKey() {
        remoteKeyboard.cancelRecording()
        remoteKeyboard.onRecordingChanged = null
        screenPreviewFrame.visibility = View.VISIBLE
        bottomNavRow.visibility = View.VISIBLE
        recordingInstructionBar.visibility = View.GONE
        recordingButtonBar.visibility = View.GONE
    }

    private fun updateRecordingInstruction(keys: List<Int>) {
        recordingInstructionBar.text = if (keys.isEmpty())
            "Hold down keys to add them to your combo"
        else
            "Combo: " + keys.joinToString(" + ") { vkLabel(it) } + "  -  hold more, or tap a held key to remove it"
    }

    /** Prompts for a name (defaulting to the combo's own key names, same auto-naming the
     *  old dialog did) and saves - or, with nothing recorded, just exits recording mode
     *  the same as Cancel, since there's nothing to save. */
    private fun finishRecordingCustomKey() {
        val keys = remoteKeyboard.finishRecording()
        remoteKeyboard.onRecordingChanged = null
        screenPreviewFrame.visibility = View.VISIBLE
        bottomNavRow.visibility = View.VISIBLE
        recordingInstructionBar.visibility = View.GONE
        recordingButtonBar.visibility = View.GONE

        if (keys.isEmpty()) return

        val defaultLabel = keys.joinToString("+") { vkLabel(it) }
        val input = EditText(this).apply {
            setText(defaultLabel)
            setTextColor(getColor(R.color.text_primary))
            setPadding(dp(20), dp(12), dp(20), dp(12))
        }
        AlertDialog.Builder(this)
            .setTitle("Name this combo")
            .setView(input)
            .setPositiveButton("Save") { _, _ ->
                val label = input.text.toString().trim().ifEmpty { defaultLabel }
                customKeys.add(CustomKey(label, keys))
                CustomKeyStore.save(prefs, customKeys)
                renderCustomKeys()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun vkLabel(vk: Int) = when (vk) {
        RemoteKeyboardView.VK.CONTROL -> "Ctrl"
        RemoteKeyboardView.VK.ALT -> "Alt"
        RemoteKeyboardView.VK.SHIFT -> "Shift"
        RemoteKeyboardView.VK.LWIN -> "Win"
        RemoteKeyboardView.VK.ESC -> "Esc"
        RemoteKeyboardView.VK.TAB -> "Tab"
        RemoteKeyboardView.VK.ENTER -> "Enter"
        RemoteKeyboardView.VK.SPACE -> "Space"
        RemoteKeyboardView.VK.BACK -> "Backspace"
        RemoteKeyboardView.VK.DELETE -> "Del"
        RemoteKeyboardView.VK.HOME -> "Home"
        RemoteKeyboardView.VK.END -> "End"
        RemoteKeyboardView.VK.PAGE_UP -> "PgUp"
        RemoteKeyboardView.VK.PAGE_DOWN -> "PgDn"
        RemoteKeyboardView.VK.PRINT_SCREEN -> "PrtSc"
        RemoteKeyboardView.VK.LEFT -> "←"
        RemoteKeyboardView.VK.UP -> "↑"
        RemoteKeyboardView.VK.DOWN -> "↓"
        RemoteKeyboardView.VK.RIGHT -> "→"
        RemoteKeyboardView.VK.OEM_COMMA -> ","
        RemoteKeyboardView.VK.OEM_PERIOD -> "."
        RemoteKeyboardView.VK.OEM_MINUS -> "-"
        RemoteKeyboardView.VK.OEM_PLUS -> "="
        RemoteKeyboardView.VK.OEM_1 -> ";"
        RemoteKeyboardView.VK.OEM_2 -> "/"
        RemoteKeyboardView.VK.OEM_3 -> "`"
        RemoteKeyboardView.VK.OEM_4 -> "["
        RemoteKeyboardView.VK.OEM_5 -> "\\"
        RemoteKeyboardView.VK.OEM_6 -> "]"
        RemoteKeyboardView.VK.OEM_7 -> "'"
        RemoteKeyboardView.VK.F1 -> "F1"
        RemoteKeyboardView.VK.F2 -> "F2"
        RemoteKeyboardView.VK.F3 -> "F3"
        RemoteKeyboardView.VK.F4 -> "F4"
        RemoteKeyboardView.VK.F5 -> "F5"
        RemoteKeyboardView.VK.F6 -> "F6"
        RemoteKeyboardView.VK.F7 -> "F7"
        RemoteKeyboardView.VK.F8 -> "F8"
        RemoteKeyboardView.VK.F9 -> "F9"
        RemoteKeyboardView.VK.F10 -> "F10"
        RemoteKeyboardView.VK.F11 -> "F11"
        RemoteKeyboardView.VK.F12 -> "F12"
        in 'A'.code..'Z'.code, in '0'.code..'9'.code -> vk.toChar().toString()
        else -> "0x%02X".format(vk)
    }

    private fun setupSavedDevices() {
        savedDevices.addAll(SavedDeviceStore.load(prefs))
        renderSavedDevices()
        btnSaveDevice.setOnClickListener { showSaveDeviceDialog() }
    }

    /** Rebuilds savedDevicesContainer from [savedDevices], one full-width button per
     *  device. Tap switches to it (fills the host/port fields and reconnects); long-press
     *  offers to delete. Same "build in code -> LayoutParams must be set explicitly, a
     *  style's layout_* attributes don't apply on their own" situation as
     *  [renderCustomKeys]. */
    private fun renderSavedDevices() {
        savedDevicesContainer.removeAllViews()
        val margin = dp(2)
        for (device in savedDevices) {
            val label = if (device.location.isNotBlank()) "${device.name} — ${device.location}" else device.name
            val button = Button(this, null, 0, R.style.KeyButton).apply {
                text = "${DeviceTypeCatalog.icon(device.deviceType)}  $label  (${device.host}:${device.port})"
                // See renderCustomKeys' identical fix - code-constructed buttons need this
                // set explicitly, they don't get it for free from the theme the way an
                // XML-declared button does.
                gravity = android.view.Gravity.CENTER
                layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(44)).apply {
                    setMargins(margin, margin, margin, margin)
                }
                setOnClickListener { selectSavedDevice(device) }
                setOnLongClickListener { confirmDeleteSavedDevice(device); true }
            }
            savedDevicesContainer.addView(button)
        }
    }

    /** Switches the active target: fills the host/port fields, persists them as the
     *  last-used connection too, and reconnects. `disconnect()` first is necessary even
     *  when already connected elsewhere - `ConnectionManager.connect()` is a no-op while
     *  already running, so without this a tap on a different saved device while connected
     *  would silently do nothing. */
    private fun selectSavedDevice(device: SavedDevice) {
        etHost.setText(device.host)
        etPort.setText(device.port.toString())
        prefs.edit().putString("host", device.host).putInt("port", device.port).apply()
        conn.disconnect()
        conn.connect(device.host, device.port)
    }

    private fun confirmDeleteSavedDevice(device: SavedDevice) {
        AlertDialog.Builder(this)
            .setTitle("Delete \"${device.name}\"?")
            .setPositiveButton("Delete") { _, _ ->
                savedDevices.remove(device)
                SavedDeviceStore.save(prefs, savedDevices)
                renderSavedDevices()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun showSaveDeviceDialog() {
        val host = etHost.text.toString().trim()
        val port = etPort.text.toString().toIntOrNull() ?: ConnectionManager.DEFAULT_PORT
        if (host.isEmpty()) {
            AlertDialog.Builder(this)
                .setTitle("No IP address")
                .setMessage("Type the PC's IP address below before saving it as a device - a blank IP means auto-discover, which isn't a fixed device to save.")
                .setPositiveButton("OK", null)
                .show()
            return
        }

        val view = layoutInflater.inflate(R.layout.dialog_save_device, null)
        val etName = view.findViewById<EditText>(R.id.etDeviceName)
        val etLocation = view.findViewById<EditText>(R.id.etDeviceLocation)
        val spinner = view.findViewById<Spinner>(R.id.spinnerDeviceType)
        spinner.adapter = ArrayAdapter(
            this, android.R.layout.simple_spinner_dropdown_item, DeviceTypeCatalog.types
        )

        AlertDialog.Builder(this)
            .setTitle("Save device")
            .setView(view)
            .setPositiveButton("Save") { _, _ ->
                val type = DeviceTypeCatalog.types[spinner.selectedItemPosition]
                val name = etName.text.toString().trim().ifEmpty { type }
                val location = etLocation.text.toString().trim()
                savedDevices.add(SavedDevice(name, host, port, type, location))
                SavedDeviceStore.save(prefs, savedDevices)
                renderSavedDevices()
            }
            .setNegativeButton("Cancel", null)
            .show()
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

        // Range 0.2s-1.5s: short enough not to feel laggy for someone who wants a snappy
        // hold, long enough at the top end that fast typing can't accidentally trigger it.
        // Narrower than the original 0.3s-3.0s range - 3s felt far too long in practice.
        // coerceIn also migrates an old saved value from outside the new range (e.g. a
        // pre-existing 2.0s pref) down to the new max instead of leaving remoteKeyboard's
        // threshold and the slider's displayed position inconsistent with each other.
        val savedHoldThresholdSec = prefs.getFloat("holdThresholdSec", 0.8f).coerceIn(0.2f, 1.5f)
        remoteKeyboard.holdThresholdMs = (savedHoldThresholdSec * 1000).toLong()
        holdThresholdSeek.progress = (((savedHoldThresholdSec - 0.2f) / 0.1f).toInt()).coerceIn(0, holdThresholdSeek.max)
        holdThresholdLabel.text = "Hold threshold: %.1fs".format(savedHoldThresholdSec)

        holdThresholdSeek.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(seekBar: SeekBar, progress: Int, fromUser: Boolean) {
                val valueSec = 0.2f + progress * 0.1f
                remoteKeyboard.holdThresholdMs = (valueSec * 1000).toLong()
                holdThresholdLabel.text = "Hold threshold: %.1fs".format(valueSec)
                prefs.edit().putFloat("holdThresholdSec", valueSec).apply()
            }
            override fun onStartTrackingTouch(seekBar: SeekBar) {}
            override fun onStopTrackingTouch(seekBar: SeekBar) {}
        })
    }

    /** Shown when the PC doesn't recognize this device and needs a one-time code. Stays
     *  open across wrong attempts (the positive button's default dismiss-on-click is
     *  overridden via setOnShowListener) so the user can just retry without re-opening it;
     *  only [dismissPairingDialog] (on success or give-up, via `onPairingEnded`) actually
     *  closes it. Cancel disconnects outright rather than leaving a half-finished
     *  handshake for `connectLoop` to keep blocking on. */
    private fun showPairingDialog(pcLabel: String) {
        if (pairingDialog?.isShowing == true) return

        val view = layoutInflater.inflate(R.layout.dialog_pairing_code, null)
        val etCode = view.findViewById<EditText>(R.id.etPairingCode)

        val dialog = AlertDialog.Builder(this)
            .setTitle("Pair with $pcLabel")
            .setMessage("Enter the code shown on the PC's screen.")
            .setView(view)
            .setPositiveButton("Submit", null)
            .setNegativeButton("Cancel") { _, _ -> conn.disconnect() }
            .setCancelable(false)
            .create()

        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val code = etCode.text.toString().trim()
                if (code.isNotEmpty()) {
                    view.findViewById<TextView>(R.id.pairingErrorText).visibility = View.GONE
                    conn.submitPairingCode(code)
                }
            }
        }

        dialog.show()
        pairingDialog = dialog
    }

    private fun showPairingError(message: String) {
        pairingDialog?.findViewById<TextView>(R.id.pairingErrorText)?.apply {
            text = message
            visibility = View.VISIBLE
        }
    }

    private fun dismissPairingDialog() {
        pairingDialog?.dismiss()
        pairingDialog = null
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
            if (state != ConnectionManager.State.CONNECTED) {
                latencyText.visibility = View.GONE
                // The PC-side Server already releases anything this session left held the
                // instant it notices the connection is gone (see Server.ReleaseHeldInput) -
                // that's the fix that matters. This just keeps the phone's own UI honest:
                // don't keep showing a key as held when there's no live connection to it.
                resetHeldKeysVisual()
            }
        }
        conn.onLog = { msg -> logText.text = msg }
        // logText alone was easy to miss - it's a small status line, overwritten by the
        // NEXT retry attempt's own log within a few seconds since ConnectionManager keeps
        // retrying every 3s. A Toast is hard to miss and only fires once per connect
        // attempt (not every retry), reported by the user as wanting a clear reason when a
        // tapped saved device fails to connect.
        conn.onConnectFailed = { reason -> Toast.makeText(this, reason, Toast.LENGTH_LONG).show() }
        conn.onLatency = { rtt ->
            latencyText.visibility = View.VISIBLE
            latencyText.text = "${rtt}ms"
        }
        conn.onPairingRequired = { pcLabel ->
            lastPcLabel = pcLabel
            val qrCode = pendingQrCode
            if (qrCode != null) {
                pendingQrCode = null
                conn.submitPairingCode(qrCode)
            } else {
                showPairingDialog(pcLabel)
            }
        }
        conn.onPairingWrongCode = { attemptsLeft ->
            // A QR-submitted code can be wrong (stale/expired QR still on screen) with no
            // dialog open yet to show the error in - open it now so the user can fall back
            // to typing the current code instead of the flow silently going nowhere.
            if (pairingDialog?.isShowing != true) showPairingDialog(lastPcLabel)
            showPairingError("Wrong code - $attemptsLeft attempt(s) left")
        }
        conn.onPairingEnded = {
            // A scanned code that never got used (e.g. pairing was closed on the PC, or
            // the attempt was abandoned) shouldn't linger and get silently auto-submitted
            // on some later, unrelated pairing attempt.
            pendingQrCode = null
            dismissPairingDialog()
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

    override fun onPause() {
        super.onPause()
        releaseAllHeldKeys()
        // Backgrounding mid-recording (e.g. a notification, a call) shouldn't leave the
        // UI stuck with the bottom nav/screen preview hidden when the user comes back -
        // nothing was saved yet anyway, so there's nothing destructive about dropping it.
        // recordingButtonBar's visibility is the simplest reliable proxy for "recording UI
        // is currently showing" - the actual recording STATE now lives inside
        // remoteKeyboard, not as a separate flag here.
        if (recordingButtonBar.visibility == View.VISIBLE) cancelRecordingCustomKey()
        // Don't keep pulling video in the background - real Wi-Fi/battery cost for no
        // visible benefit while the app isn't on screen.
        screen.stop()
    }

    override fun onResume() {
        super.onResume()
        if (controlPanel.visibility == View.VISIBLE) startScreenStream()
    }

    override fun onDestroy() {
        super.onDestroy()
        screen.stop()
        conn.disconnect()
    }
}
