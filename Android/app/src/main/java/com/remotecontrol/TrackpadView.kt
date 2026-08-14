package com.remotecontrol

import android.content.Context
import android.util.AttributeSet
import android.util.Log
import android.view.GestureDetector
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import kotlin.math.roundToInt

private const val TAG = "RCTrackpad"

/** Translates touch gestures into [Packet]s. 1 finger drags the cursor, 2+ fingers scroll,
 *  tap count = finger count picks left/right/middle click, long-press-and-drag holds the
 *  left button down. */
class TrackpadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    var onPacket: ((Packet) -> Unit)? = null
    var sensitivity: Float = 1.4f

    private var accumX = 0f
    private var accumY = 0f
    private var lastX = 0f
    private var lastY = 0f

    /** Live finger count, used while a gesture is in progress (move/scroll routing). */
    private var activePointers = 0

    /** Highest finger count seen this gesture. GestureDetector's tap callbacks fire on a
     *  delayed handler message well after ACTION_UP, by which point [activePointers] has
     *  already been reset - so tap classification reads this instead. Reset on ACTION_DOWN. */
    private var maxPointers = 0

    private var dragging = false

    private val gestureDetector = GestureDetector(context, object : GestureDetector.SimpleOnGestureListener() {
        override fun onDown(e: MotionEvent): Boolean = true

        override fun onSingleTapConfirmed(e: MotionEvent): Boolean {
            when (maxPointers) {
                1 -> onPacket?.invoke(Packet.LeftClick)
                2 -> onPacket?.invoke(Packet.RightClick)
                else -> onPacket?.invoke(Packet.MiddleClick)
            }
            return true
        }

        override fun onDoubleTap(e: MotionEvent): Boolean {
            onPacket?.invoke(Packet.LeftClick)
            onPacket?.invoke(Packet.LeftClick)
            return true
        }

        override fun onLongPress(e: MotionEvent) {
            if (maxPointers != 1) return
            dragging = true
            performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
            onPacket?.invoke(Packet.LeftDown)
        }

        override fun onScroll(e1: MotionEvent?, e2: MotionEvent, distanceX: Float, distanceY: Float): Boolean {
            if (activePointers < 2) return false
            val ticks = (-distanceY / 40f).roundToInt()
            if (ticks != 0) onPacket?.invoke(Packet.Scroll(ticks))
            return true
        }
    })

    override fun onTouchEvent(event: MotionEvent): Boolean {
        gestureDetector.onTouchEvent(event)

        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                Log.d(TAG, "ACTION_DOWN at (${event.x}, ${event.y})")
                activePointers = 1
                maxPointers = 1
                resetOrigin(event)
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                activePointers = event.pointerCount
                maxPointers = maxOf(maxPointers, activePointers)
                resetOrigin(event)
            }
            MotionEvent.ACTION_POINTER_UP -> {
                activePointers = (event.pointerCount - 1).coerceAtLeast(0)
                resetOrigin(event)
            }
            MotionEvent.ACTION_MOVE -> {
                if (activePointers == 1) {
                    accumX += (event.x - lastX) * sensitivity
                    accumY += (event.y - lastY) * sensitivity
                    val sendX = accumX.toInt()
                    val sendY = accumY.toInt()
                    if (sendX != 0 || sendY != 0) {
                        onPacket?.invoke(Packet.Move(sendX.toFloat(), sendY.toFloat()))
                        accumX -= sendX
                        accumY -= sendY
                    }
                }
                lastX = event.x
                lastY = event.y
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                if (dragging) onPacket?.invoke(Packet.LeftUp)
                dragging = false
                activePointers = 0
                accumX = 0f
                accumY = 0f
            }
        }
        return true
    }

    private fun resetOrigin(event: MotionEvent) {
        lastX = event.x
        lastY = event.y
        accumX = 0f
        accumY = 0f
    }
}
