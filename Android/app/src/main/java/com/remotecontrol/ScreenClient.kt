package com.remotecontrol

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.EOFException
import java.io.InputStream
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.coroutines.coroutineContext

/**
 * Receives the PC's screen as an MJPEG stream from [ScreenStreamer] (Windows side, port
 * 5202) and hands decoded frames to the UI. Same HTTP endpoint a browser would use -
 * `GET /stream.mjpg` - so the same server also has a zero-install browser fallback.
 *
 * Wire format (multipart/x-mixed-replace), repeating forever:
 *
 *     --rcframe
 *     Content-Type: image/jpeg
 *     Content-Length: 61521
 *     <blank line>
 *     <61521 bytes of JPEG>
 *
 * Every part carries Content-Length, so frames are read by exact byte count rather than by
 * scanning for the boundary marker - which would risk a false match against bytes inside
 * the JPEG payload itself.
 */
class ScreenClient {

    companion object {
        const val PORT = 5202

        /** Must stay in step with ScreenStreamer.Presets on the Windows side, plus the
         *  client-only OFF entry at the end. */
        val QUALITY_NAMES = listOf("LOW", "MED", "HIGH", "MAX", "OFF")

        /** Screen mirroring fully disabled - frees all Wi-Fi airtime for trackpad input.
         *  Never sent to the server; MainActivity calls [stop] instead of [start] when
         *  this index is selected. */
        val OFF_QUALITY = QUALITY_NAMES.lastIndex

        /** LOW by default - video and input share Wi-Fi airtime, so a heavy stream costs
         *  cursor responsiveness. Must match ScreenStreamer's default preset index. */
        const val DEFAULT_QUALITY = 0

        private const val CONNECT_TIMEOUT_MS = 5_000
        private const val READ_TIMEOUT_MS = 15_000
        private const val RECONNECT_DELAY_MS = 2_000L

        // A frame far larger than this means the stream desynchronised and a length is
        // being read out of the middle of a JPEG - bail rather than allocate wildly.
        private const val MAX_FRAME_BYTES = 16 * 1024 * 1024
    }

    enum class State { STOPPED, CONNECTING, STREAMING }

    var onFrame: ((Bitmap) -> Unit)? = null
    var onStateChange: ((State) -> Unit)? = null
    var onLog: ((String) -> Unit)? = null

    private var scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private var socket: Socket? = null

    /** RGB_565 - half the memory of ARGB_8888, and the screen stream has no alpha
     *  channel to preserve. */
    private val decodeOptions = BitmapFactory.Options().apply { inPreferredConfig = Bitmap.Config.RGB_565 }

    /**
     * Begins streaming from [host]:[port] at [quality]. Reconnects on its own until [stop]
     * is called, so a sleeping PC or a Wi-Fi blip recovers without user action.
     */
    fun start(host: String, port: Int = PORT, quality: Int = DEFAULT_QUALITY) {
        stop()
        scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

        scope.launch {
            while (isActive) {
                setState(State.CONNECTING)
                try {
                    streamFrom(host, port, quality)
                } catch (cancelled: CancellationException) {
                    throw cancelled // stop() was called - must propagate or this never dies
                } catch (e: Exception) {
                    log("Stream ended: ${e.message}")
                }

                closeSocket()
                setState(State.STOPPED)
                if (!isActive) break
                delay(RECONNECT_DELAY_MS)
            }
        }
    }

    fun stop() {
        scope.cancel()
        closeSocket()
        setState(State.STOPPED)
    }

    private fun closeSocket() {
        socket?.runCatching { close() }
        socket = null
    }

    private suspend fun streamFrom(host: String, port: Int, quality: Int) {
        log("Opening screen stream at $host:$port (${QUALITY_NAMES.getOrNull(quality) ?: "?"})...")

        val sock = Socket()
        sock.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
        sock.soTimeout = READ_TIMEOUT_MS
        sock.tcpNoDelay = true
        socket = sock

        val output = sock.getOutputStream()
        val request = "GET /stream.mjpg?q=$quality HTTP/1.1\r\nHost: $host\r\nConnection: close\r\n\r\n"
        output.write(request.toByteArray(Charsets.US_ASCII))
        output.flush()

        val input = BufferedInputStream(sock.getInputStream(), 64 * 1024)

        val statusLine = readLine(input) ?: throw EOFException("no response from server")
        if (!statusLine.contains("200")) throw java.io.IOException("server said: $statusLine")

        while (true) {
            val header = readLine(input) ?: throw EOFException("headers truncated")
            if (header.isEmpty()) break
        }

        setState(State.STREAMING)
        log("Screen stream connected")
        readFramesForever(input)
    }

    private suspend fun readFramesForever(input: InputStream) {
        while (coroutineContext.isActive) {
            val frameSize = readNextPartLength(input)
            if (frameSize <= 0 || frameSize > MAX_FRAME_BYTES) {
                throw java.io.IOException("bad frame length: $frameSize")
            }

            val frameBytes = ByteArray(frameSize)
            readFully(input, frameBytes)

            val bitmap = BitmapFactory.decodeByteArray(frameBytes, 0, frameSize, decodeOptions)
            if (bitmap == null) {
                log("Dropped an undecodable frame")
                continue
            }

            // Suspending until the main thread has taken the frame is natural
            // backpressure - never decodes faster than the UI can display.
            withContext(Dispatchers.Main) { onFrame?.invoke(bitmap) }
        }
    }

    private fun readNextPartLength(input: InputStream): Int {
        var contentLength = -1
        while (true) {
            val line = readLine(input) ?: throw EOFException("stream closed between frames")
            if (line.startsWith("Content-Length:", ignoreCase = true)) {
                contentLength = line.substringAfter(':').trim().toIntOrNull() ?: -1
            } else if (line.isEmpty() && contentLength > 0) {
                return contentLength
            }
        }
    }

    private fun readLine(input: InputStream): String? {
        val line = StringBuilder()
        while (true) {
            val byte = input.read()
            if (byte == -1) return if (line.isEmpty()) null else line.toString()
            if (byte == '\n'.code) return line.toString()
            if (byte != '\r'.code) line.append(byte.toChar())
        }
    }

    private fun readFully(input: InputStream, buffer: ByteArray) {
        var offset = 0
        while (offset < buffer.size) {
            val readCount = input.read(buffer, offset, buffer.size - offset)
            if (readCount == -1) throw EOFException("stream ended mid-frame")
            offset += readCount
        }
    }

    private fun setState(state: State) {
        CoroutineScope(Dispatchers.Main).launch { onStateChange?.invoke(state) }
    }

    private fun log(message: String) {
        CoroutineScope(Dispatchers.Main).launch { onLog?.invoke(message) }
    }
}
