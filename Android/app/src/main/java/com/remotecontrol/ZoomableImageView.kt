package com.remotecontrol

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Matrix
import android.util.AttributeSet
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import androidx.appcompat.widget.AppCompatImageView

/**
 * ImageView with pinch-to-zoom and drag-to-pan, used for the live PC screen. View-only -
 * it does not move the cursor. A phone in portrait shows a landscape desktop as a small
 * letterboxed strip, so zooming lets you read text without raising the stream resolution
 * (magnification is free - it happens entirely on the phone, no extra bandwidth).
 *
 * Gestures:
 *   pinch       -> zoom 1x-8x
 *   drag        -> pan (only while zoomed in; at 1x the image already fits)
 *   double-tap  -> toggle between fit and 3x, centred on the tap
 */
class ZoomableImageView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : AppCompatImageView(context, attrs) {

    companion object {
        private const val MIN_ZOOM = 1f
        private const val MAX_ZOOM = 8f
        private const val DOUBLE_TAP_ZOOM = 3f
    }

    private val displayMatrix = Matrix()
    private var zoom = 1f
    private var panX = 0f
    private var panY = 0f

    /** Reported so the activity can show a "2.4x" badge. */
    var onZoomChanged: ((Float) -> Unit)? = null

    init {
        // Required - the transform is driven manually rather than letting the ImageView
        // apply fitCenter.
        scaleType = ScaleType.MATRIX
    }

    private val scaleDetector = ScaleGestureDetector(context,
        object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(detector: ScaleGestureDetector): Boolean {
                val previousZoom = zoom
                zoom = (zoom * detector.scaleFactor).coerceIn(MIN_ZOOM, MAX_ZOOM)

                // Keep the point under the user's fingers stationary while zooming. panX/
                // panY are offsets from the view's CENTER (applyTransform adds them on top
                // of the already-centered fitCenter offset) - detector.focusX/focusY are
                // raw view coordinates (0 at the left/top edge) and have to be re-centered
                // the same way before use here, or every pinch tick nudges the image by
                // (view size / 2) * (1 - actualFactor) more than it should. The double-tap
                // handler below already centers its own pan math this way; this one didn't,
                // which is what made the image visibly drift/jump mid-pinch.
                val actualFactor = zoom / previousZoom
                val focusX = detector.focusX - width / 2f
                val focusY = detector.focusY - height / 2f
                panX = focusX + (panX - focusX) * actualFactor
                panY = focusY + (panY - focusY) * actualFactor

                applyTransform()
                onZoomChanged?.invoke(zoom)
                return true
            }
        })

    private val tapDetector = GestureDetector(context,
        object : GestureDetector.SimpleOnGestureListener() {
            override fun onDoubleTap(e: MotionEvent): Boolean {
                if (zoom > MIN_ZOOM) {
                    resetZoom()
                } else {
                    zoom = DOUBLE_TAP_ZOOM
                    panX = (width / 2f - e.x) * (DOUBLE_TAP_ZOOM - 1f)
                    panY = (height / 2f - e.y) * (DOUBLE_TAP_ZOOM - 1f)
                    applyTransform()
                    onZoomChanged?.invoke(zoom)
                }
                return true
            }
        })

    private var lastTouchX = 0f
    private var lastTouchY = 0f

    override fun onTouchEvent(event: MotionEvent): Boolean {
        scaleDetector.onTouchEvent(event)
        tapDetector.onTouchEvent(event)

        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                lastTouchX = event.x
                lastTouchY = event.y
            }
            MotionEvent.ACTION_MOVE -> {
                // Single-finger drag pans. Two fingers means a pinch, which the scale
                // detector already handles - panning at the same time would fight it.
                if (event.pointerCount == 1 && zoom > MIN_ZOOM && !scaleDetector.isInProgress) {
                    panX += event.x - lastTouchX
                    panY += event.y - lastTouchY
                    lastTouchX = event.x
                    lastTouchY = event.y
                    applyTransform()
                }
            }
            MotionEvent.ACTION_POINTER_UP -> {
                // A pinch ending by lifting ONE finger (the common case - people rarely
                // lift both at once) left lastTouchX/Y stale at wherever it was from
                // before the pinch even started, since nothing updated it while 2+
                // fingers were down. The very next single-finger ACTION_MOVE (once this
                // drops back to 1 finger) then diffed against that stale point instead of
                // the remaining finger's real position, producing a sudden jump - a
                // second, independent cause of the reported "jumping while zooming", on
                // top of the pinch-focus centering bug already fixed. event.x/y (index 0)
                // can be the LIFTING finger, not the one staying down - same reasoning as
                // TrackpadView's resetOriginExcluding, which this mirrors.
                resetLastTouchExcluding(event, event.actionIndex)
            }
        }
        return true
    }

    private fun resetLastTouchExcluding(event: MotionEvent, excludeIndex: Int) {
        for (i in 0 until event.pointerCount) {
            if (i != excludeIndex) {
                lastTouchX = event.getX(i)
                lastTouchY = event.getY(i)
                return
            }
        }
    }

    fun resetZoom() {
        zoom = 1f
        panX = 0f
        panY = 0f
        applyTransform()
        onZoomChanged?.invoke(zoom)
    }

    /**
     * Rebuilds the image matrix: fit the frame to the view, scale by the user's zoom, then
     * translate by the pan offset - clamped so the image can never be dragged fully off
     * screen.
     */
    private fun applyTransform() {
        val image = drawable ?: return
        val viewWidth = width.toFloat()
        val viewHeight = height.toFloat()
        val imageWidth = image.intrinsicWidth.toFloat()
        val imageHeight = image.intrinsicHeight.toFloat()
        if (viewWidth <= 0f || viewHeight <= 0f || imageWidth <= 0f || imageHeight <= 0f) return

        // Base scale = fitCenter, so 1x always means "whole screen visible".
        val fitScale = minOf(viewWidth / imageWidth, viewHeight / imageHeight)
        val totalScale = fitScale * zoom
        val scaledWidth = imageWidth * totalScale
        val scaledHeight = imageHeight * totalScale

        clampPan(viewWidth, viewHeight, scaledWidth, scaledHeight)

        val offsetX = (viewWidth - scaledWidth) / 2f + panX
        val offsetY = (viewHeight - scaledHeight) / 2f + panY

        displayMatrix.reset()
        displayMatrix.postScale(totalScale, totalScale)
        displayMatrix.postTranslate(offsetX, offsetY)
        imageMatrix = displayMatrix
    }

    /** Limits panning to the slack between the scaled image and the view. When the image
     *  is smaller than the view on an axis it stays centred, so pan is pinned to zero. */
    private fun clampPan(viewWidth: Float, viewHeight: Float, scaledWidth: Float, scaledHeight: Float) {
        val slackX = maxOf(0f, (scaledWidth - viewWidth) / 2f)
        val slackY = maxOf(0f, (scaledHeight - viewHeight) / 2f)
        panX = panX.coerceIn(-slackX, slackX)
        panY = panY.coerceIn(-slackY, slackY)
    }

    override fun setImageBitmap(bm: Bitmap?) {
        super.setImageBitmap(bm)
        // New frame, same view: re-apply so zoom/pan survive the swap.
        applyTransform()
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        applyTransform()
    }
}
