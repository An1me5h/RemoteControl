package com.remotecontrol

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.Log
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.PrintWriter
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.Executors
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** TCP client to RemoteControl.exe: connect/reconnect, UDP discovery, pairing handshake,
 *  and PING/PONG latency. [context] is only used to read [DeviceIdentity.id] -
 *  applicationContext, so it can't leak an Activity. */
class ConnectionManager(private val context: Context) {

    enum class State { DISCONNECTED, CONNECTING, CONNECTED }

    companion object {
        private const val TAG = "RCConn"
        const val DEFAULT_PORT = 5201
        const val DISCOVERY_PORT = 58201
        const val DISCOVERY_MESSAGE = "RC_DISCOVER"
        const val PING_INTERVAL_MS = 5000L
        const val RECONNECT_DELAY_MS = 3000L
        const val DISCOVERY_TIMEOUT_MS = 4000
        const val CONNECT_TIMEOUT_MS = 4000

        /** Cap on packets remembered while offline - a few seconds' worth of dragging at
         *  most; older ones are dropped first so a long outage doesn't grow this forever. */
        const val MAX_QUEUED_PACKETS = 300

        /** Pace for replaying the backlog after reconnecting - the PC gets one packet
         *  every this-many ms instead of the whole backlog in one burst. */
        const val DRAIN_INTERVAL_MS = 10L

        /** How long to wait for the PC's first handshake reply (WELCOME/PAIRREQUIRED/
         *  REJECTED) - this should be near-instant, so a short timeout is fine. */
        const val HANDSHAKE_REPLY_TIMEOUT_MS = 10_000

        /** How long to wait for the *user* to type a pairing code before giving up - has
         *  to be generous. Kept a little under the PC's own 120s handshake budget
         *  (PairingCoordinator.HandshakeTimeoutSeconds) so this side times out first with
         *  a clear message, rather than submitting a code to a connection the PC already
         *  dropped. */
        const val PAIRING_CODE_WAIT_MS = 110_000L

        /** How often [waitForPairingCode] wakes up to recheck `running` - keeps a stale
         *  handshake from blocking (and holding the PC's connection slot) for anywhere
         *  near the full [PAIRING_CODE_WAIT_MS] after a `disconnect()` call. */
        const val PAIRING_CODE_POLL_SLICE_MS = 500L
    }

    var onStateChanged: ((State) -> Unit)? = null
    var onLog: ((String) -> Unit)? = null
    var onLatency: ((Long) -> Unit)? = null

    /** Fired when the PC doesn't recognize this device and needs a pairing code, with the
     *  PC's own display name/model for context. Answer with [submitPairingCode]. */
    var onPairingRequired: ((pcLabel: String) -> Unit)? = null

    /** Fired when a submitted code was wrong; still waiting for another attempt. */
    var onPairingWrongCode: ((attemptsLeft: Int) -> Unit)? = null

    /** Fired when pairing finished (success or failure) - dismiss any code-entry UI. */
    var onPairingEnded: (() -> Unit)? = null

    var currentHost: String? = null
        private set

    private val running = AtomicBoolean(false)
    private val executor = Executors.newSingleThreadExecutor()
    // Separate from [executor]: that one is tied up for the whole lifetime of connectLoop/
    // readLoop, so a write submitted there would never run until disconnect. This one exists
    // solely so `send()` never touches the socket on the caller's thread - callers include the
    // UI thread (trackpad/keyboard), where a direct write throws NetworkOnMainThreadException.
    private val writeExecutor = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())

    @Volatile private var socket: Socket? = null
    @Volatile private var writer: PrintWriter? = null
    private var lastPingSentAt = 0L

    /** Bridges the pairing-code dialog (runs on the UI thread) back to connectLoop's
     *  background thread, which blocks waiting for whatever the user submits. */
    private val pairingCodeQueue = LinkedBlockingQueue<String>()

    /** Packets that couldn't be sent yet - either never connected, or connected but still
     *  draining a backlog from before. Replayed in order once the drain loop gets to them. */
    private val offlineQueue = ConcurrentLinkedQueue<Packet>()

    /** host == null means "discover it on the LAN"; a non-null host skips discovery. */
    fun connect(host: String?, port: Int) {
        if (running.getAndSet(true)) return
        executor.execute { connectLoop(host, port) }
    }

    fun disconnect() {
        running.set(false)
        offlineQueue.clear() // user-initiated - the backlog is no longer relevant
        closeSocket()
        setState(State.DISCONNECTED)
    }

    /** Called from the UI thread when the user submits a code typed into the pairing
     *  dialog. Whatever's waiting in [performHandshake] picks it up from the queue. */
    fun submitPairingCode(code: String) {
        pairingCodeQueue.offer(code)
    }

    /** Callable from any thread, including the UI thread (trackpad/keyboard call this
     *  directly from touch/click handlers). Never touches the socket itself here - only
     *  hands the packet to [writeExecutor], which is the one and only place that ever
     *  calls `PrintWriter.println()`. Packets queue (capped, replayed in order once
     *  reconnected) rather than being dropped when there's no live connection. */
    fun send(packet: Packet) {
        writeExecutor.execute { processOne(packet) }
    }

    /** Runs only on [writeExecutor]. Writes directly if connected; otherwise queues.
     *
     *  IMPORTANT: `PrintWriter` never throws on a failed write - it swallows the
     *  IOException internally and just sets an error flag (see `checkError()`), so that's
     *  what actually detects a dead connection here, not a try/catch. On failure we force-
     *  close the socket so the blocked read in [readLoop] notices immediately and
     *  [connectLoop] reconnects, instead of every future send silently queueing forever
     *  behind a connection nothing would otherwise un-stick.
     *
     *  Returns false if the packet ended up queued instead of sent - used by
     *  [drainOfflineQueue] to know when to stop draining. */
    private fun processOne(packet: Packet): Boolean {
        val w = writer
        if (w == null) {
            Log.d(TAG, "processOne ${packet.encode()}  not connected, queued=${offlineQueue.size + 1}")
            enqueue(packet)
            return false
        }
        w.println(packet.encode())
        if (w.checkError()) {
            Log.d(TAG, "processOne ${packet.encode()}  write failed, forcing reconnect")
            log("Send failed - connection is dead, forcing reconnect")
            closeSocket()
            enqueue(packet)
            return false
        }
        Log.d(TAG, "processOne ${packet.encode()}  sent")
        return true
    }

    private fun enqueue(packet: Packet) {
        if (offlineQueue.size >= MAX_QUEUED_PACKETS) offlineQueue.poll()
        offlineQueue.offer(packet)
    }

    /** Replays anything queued while disconnected, one packet every [DRAIN_INTERVAL_MS] so
     *  the PC doesn't get flooded with a burst of backlog all at once. Submitted to
     *  [writeExecutor] - since that's single-threaded, any `send()` calls made while this
     *  is running simply wait their turn, so live input can never jump ahead of the
     *  backlog it's replaying. Stops the moment a write fails rather than churning through
     *  the rest of the queue re-queueing every remaining packet for no reason - that
     *  failure already forced a fresh reconnect, which will trigger its own drain. */
    private fun drainOfflineQueue() {
        writeExecutor.execute {
            while (offlineQueue.isNotEmpty()) {
                val packet = offlineQueue.poll() ?: break
                if (!processOne(packet)) break
                Thread.sleep(DRAIN_INTERVAL_MS)
            }
        }
    }

    private fun connectLoop(host: String?, port: Int) {
        while (running.get()) {
            setState(State.CONNECTING)
            val target = host ?: discoverHost(port)
            if (target == null) {
                log("Discovery failed")
                sleep(RECONNECT_DELAY_MS)
                continue
            }

            try {
                val s = Socket()
                s.tcpNoDelay = true
                s.connect(InetSocketAddress(target, port), CONNECT_TIMEOUT_MS)
                socket = s
                currentHost = target
                val reader = BufferedReader(InputStreamReader(s.getInputStream()))
                val handshakeWriter = PrintWriter(s.getOutputStream(), true)

                if (!performHandshake(s, reader, handshakeWriter)) {
                    // Rejected/timed out/abandoned - already logged. Treat like any other
                    // failed connection attempt: fall through to the reconnect sleep below.
                    closeSocket()
                    if (running.get()) sleep(RECONNECT_DELAY_MS)
                    continue
                }

                writer = handshakeWriter
                setState(State.CONNECTED)
                log("Connected to $target:$port")
                if (offlineQueue.isNotEmpty()) log("Replaying ${offlineQueue.size} queued packet(s)")
                drainOfflineQueue()
                readLoop(s, reader)
                if (running.get()) log("Disconnected")
            } catch (e: Exception) {
                if (running.get()) log("Connection error: ${e.message}")
            } finally {
                closeSocket()
            }

            if (running.get()) sleep(RECONNECT_DELAY_MS)
        }
        setState(State.DISCONNECTED)
    }

    /** Sends HELLO and handles whatever the PC asks for next: WELCOME (done), REJECTED
     *  (done, failed), or PAIRREQUIRED (blocks this thread on [pairingCodeQueue] until the
     *  UI submits a code, loops on WRONGCODE). Returns true only on WELCOME. Runs entirely
     *  on the connectLoop background thread - the blocking wait for a UI-driven pairing
     *  code is exactly what [pairingCodeQueue] bridges. */
    private fun performHandshake(s: Socket, reader: BufferedReader, writer: PrintWriter): Boolean {
        val hello = Packet.Hello(
            deviceId = DeviceIdentity.id(context),
            model = DeviceIdentity.model,
            build = DeviceIdentity.buildNumber,
            name = DeviceIdentity.model
        )
        writer.println(hello.encode())

        s.soTimeout = HANDSHAKE_REPLY_TIMEOUT_MS
        var reply = readHandshakeMessage(reader) ?: run {
            log("No response from PC during handshake")
            return false
        }

        when (reply.optString("t")) {
            "WELCOME" -> return true
            "REJECTED" -> {
                log("Connection rejected: ${describeRejection(reply.optString("reason"))}")
                // Rejected before ever reaching the pairing loop below (e.g. "busy", or
                // pairing isn't open) - onPairingEnded still needs to fire so a scanned
                // QR code stashed for this attempt doesn't linger for some later one.
                mainHandler.post { onPairingEnded?.invoke() }
                return false
            }
            "PAIRREQUIRED" -> { /* fall through to the pairing loop below */ }
            else -> {
                log("Unexpected handshake reply: ${reply.optString("t")}")
                return false
            }
        }

        mainHandler.post { onPairingRequired?.invoke("this PC") }
        try {
            while (true) {
                val code = waitForPairingCode()
                if (code == null) {
                    if (running.get()) log("Pairing timed out waiting for a code")
                    return false
                }

                writer.println(Packet.PairCode(code).encode())
                reply = readHandshakeMessage(reader) ?: run {
                    log("Connection lost during pairing")
                    return false
                }

                when (reply.optString("t")) {
                    "WELCOME" -> return true
                    "WRONGCODE" -> {
                        val attemptsLeft = reply.optInt("attemptsLeft", 0)
                        mainHandler.post { onPairingWrongCode?.invoke(attemptsLeft) }
                    }
                    "REJECTED" -> {
                        log("Pairing rejected: ${describeRejection(reply.optString("reason"))}")
                        return false
                    }
                    else -> {
                        log("Unexpected pairing reply: ${reply.optString("t")}")
                        return false
                    }
                }
            }
        } finally {
            mainHandler.post { onPairingEnded?.invoke() }
        }
    }

    /** Waits for a code from [pairingCodeQueue] in short slices instead of one long
     *  [PAIRING_CODE_WAIT_MS]-ms blocking poll, so a `disconnect()` call (e.g. from
     *  scanning a fresh QR while a previous attempt is still stuck waiting on a code that
     *  will never come) is noticed within [PAIRING_CODE_POLL_SLICE_MS] instead of the
     *  stale attempt sitting there - and holding the PC's one connection slot - for up to
     *  110 seconds. Returns null on either a real timeout or `running` going false. */
    private fun waitForPairingCode(): String? {
        val deadline = System.currentTimeMillis() + PAIRING_CODE_WAIT_MS
        while (running.get() && System.currentTimeMillis() < deadline) {
            val remaining = (deadline - System.currentTimeMillis()).coerceAtLeast(0L)
            val slice = remaining.coerceAtMost(PAIRING_CODE_POLL_SLICE_MS)
            val code = pairingCodeQueue.poll(slice, TimeUnit.MILLISECONDS)
            if (code != null) return code
        }
        return null
    }

    private fun readHandshakeMessage(reader: BufferedReader): JSONObject? {
        return try {
            val line = reader.readLine() ?: return null
            JSONObject(line)
        } catch (e: Exception) {
            null
        }
    }

    private fun describeRejection(reason: String) = when (reason) {
        "busy" -> "another device is already connected to this PC"
        "wrong_code" -> "wrong pairing code entered too many times"
        "bad_hello" -> "the PC didn't understand this app's handshake (version mismatch?)"
        "pairing_closed" -> "pairing isn't open on this PC right now - ask its owner to click 'Add New Device'"
        "" -> "no reason given"
        else -> reason
    }

    /** Reads PONG replies and paces PING sends every [PING_INTERVAL_MS]. Returns on clean EOF
     *  or throws on a real socket error, both of which send [connectLoop] back to reconnect.
     *  Takes the same [reader] the handshake used - creating a second BufferedReader on the
     *  same stream here would risk losing bytes already sitting in the first one's buffer. */
    private fun readLoop(s: Socket, reader: BufferedReader) {
        var nextPing = System.currentTimeMillis()
        while (running.get() && !s.isClosed) {
            val now = System.currentTimeMillis()
            if (now >= nextPing) {
                lastPingSentAt = now
                send(Packet.Ping)
                nextPing = now + PING_INTERVAL_MS
            }
            s.soTimeout = (nextPing - System.currentTimeMillis()).coerceAtLeast(100L).toInt()
            try {
                val line = reader.readLine() ?: return
                if (line.contains("PONG")) {
                    val rtt = System.currentTimeMillis() - lastPingSentAt
                    onLatency?.let { cb -> mainHandler.post { cb(rtt) } }
                }
            } catch (e: SocketTimeoutException) {
                // expected - loop back around and send the next scheduled ping
            }
        }
    }

    private fun discoverHost(port: Int): String? {
        try {
            DatagramSocket().use { udp ->
                udp.broadcast = true
                udp.soTimeout = DISCOVERY_TIMEOUT_MS
                val message = DISCOVERY_MESSAGE.toByteArray()
                val broadcastAddr = InetAddress.getByName("255.255.255.255")
                udp.send(DatagramPacket(message, message.size, broadcastAddr, DISCOVERY_PORT))

                val buf = ByteArray(256)
                val reply = DatagramPacket(buf, buf.size)
                udp.receive(reply)
                val text = String(reply.data, 0, reply.length).trim()
                val parts = text.split(" ")
                return if (parts.size >= 2 && parts[0] == "RC_HOST") parts[1] else null
            }
        } catch (e: Exception) {
            return null
        }
    }

    private fun closeSocket() {
        try { socket?.close() } catch (e: Exception) { }
        socket = null
        writer = null
    }

    private fun sleep(ms: Long) {
        try { Thread.sleep(ms) } catch (e: InterruptedException) { }
    }

    private fun setState(s: State) {
        mainHandler.post { onStateChanged?.invoke(s) }
    }

    private fun log(msg: String) {
        mainHandler.post { onLog?.invoke(msg) }
    }
}
