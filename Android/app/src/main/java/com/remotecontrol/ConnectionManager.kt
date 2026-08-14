package com.remotecontrol

import android.os.Handler
import android.os.Looper
import android.util.Log
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
import java.util.concurrent.atomic.AtomicBoolean

/** TCP client to RemoteControl.exe: connect/reconnect, UDP discovery, and PING/PONG latency. */
class ConnectionManager {

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
    }

    var onStateChanged: ((State) -> Unit)? = null
    var onLog: ((String) -> Unit)? = null
    var onLatency: ((Long) -> Unit)? = null

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
                writer = PrintWriter(s.getOutputStream(), true)
                setState(State.CONNECTED)
                log("Connected to $target:$port")
                if (offlineQueue.isNotEmpty()) log("Replaying ${offlineQueue.size} queued packet(s)")
                drainOfflineQueue()
                readLoop(s)
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

    /** Reads PONG replies and paces PING sends every [PING_INTERVAL_MS]. Returns on clean EOF
     *  or throws on a real socket error, both of which send [connectLoop] back to reconnect. */
    private fun readLoop(s: Socket) {
        val reader = BufferedReader(InputStreamReader(s.getInputStream()))
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
