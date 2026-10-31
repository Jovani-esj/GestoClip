package com.gestoclip.vision

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Matrix
import android.os.SystemClock
import androidx.camera.core.ImageProxy
import com.google.mediapipe.framework.image.BitmapImageBuilder
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.vision.core.RunningMode
import com.google.mediapipe.tasks.vision.gesturerecognizer.GestureRecognizer
import com.google.mediapipe.tasks.vision.gesturerecognizer.GestureRecognizerResult

enum class HandPose { UNKNOWN, PALM, FIST }

enum class GestureEvent { NONE, COPY, PASTE }

/**
 * Máquina de estados temporal idéntica a la de Windows:
 * exige una pose estable durante [stableFrames] cuadros y detecta la transición:
 * PALM -> FIST = COPY ("agarrar")
 * FIST -> PALM = PASTE ("soltar")
 */
class GestureStateMachine(private val stableFrames: Int = 4) {
    private var candidate: HandPose = HandPose.UNKNOWN
    private var count: Int = 0
    private var stable: HandPose = HandPose.UNKNOWN

    fun push(pose: HandPose): GestureEvent {
        if (pose == HandPose.UNKNOWN) {
            candidate = HandPose.UNKNOWN
            count = 0
            return GestureEvent.NONE
        }
        if (pose == candidate) {
            count++
        } else {
            candidate = pose
            count = 1
        }
        if (count < stableFrames || candidate == stable) {
            return GestureEvent.NONE
        }
        val prev = stable
        stable = candidate
        if (prev == HandPose.PALM && stable == HandPose.FIST) return GestureEvent.COPY
        if (prev == HandPose.FIST && stable == HandPose.PALM) return GestureEvent.PASTE
        return GestureEvent.NONE
    }

    fun reset() {
        candidate = HandPose.UNKNOWN
        stable = HandPose.UNKNOWN
        count = 0
    }

    companion object {
        fun mapCategoryToPose(categoryName: String?, score: Float, minScore: Float = 0.55f): HandPose {
            if (categoryName == null || score < minScore) return HandPose.UNKNOWN
            return when (categoryName) {
                "Closed_Fist" -> HandPose.FIST
                "Open_Palm" -> HandPose.PALM
                else -> HandPose.UNKNOWN
            }
        }
    }
}

class MediaPipeGestureHelper(
    context: Context,
    private val onPoseDetected: (HandPose, Float) -> Unit
) {
    private var recognizer: GestureRecognizer? = null

    init {
        val baseOptions = BaseOptions.builder()
            .setModelAssetPath("gesture_recognizer.task")
            .build()
        val options = GestureRecognizer.GestureRecognizerOptions.builder()
            .setBaseOptions(baseOptions)
            .setRunningMode(RunningMode.LIVE_STREAM)
            .setNumHands(1)
            .setMinHandDetectionConfidence(0.5f)
            .setMinHandPresenceConfidence(0.5f)
            .setMinTrackingConfidence(0.5f)
            .setResultListener { result, _ -> handleResult(result) }
            .build()
        recognizer = GestureRecognizer.createFromOptions(context, options)
    }

    fun recognizeAsync(imageProxy: ImageProxy) {
        try {
            val bmp = imageProxy.toBitmap()
            val matrix = Matrix().apply {
                postRotate(imageProxy.imageInfo.rotationDegrees.toFloat())
                postScale(-1f, 1f, bmp.width / 2f, bmp.height / 2f)
            }
            val rotated = Bitmap.createBitmap(bmp, 0, 0, bmp.width, bmp.height, matrix, true)
            val mpImage = BitmapImageBuilder(rotated).build()
            recognizer?.recognizeAsync(mpImage, SystemClock.uptimeMillis())
        } catch (_: Exception) {
        } finally {
            imageProxy.close()
        }
    }

    private fun handleResult(result: GestureRecognizerResult) {
        val gestures = result.gestures()
        if (gestures.isEmpty() || gestures[0].isEmpty()) {
            onPoseDetected(HandPose.UNKNOWN, 0f)
            return
        }
        val top = gestures[0][0]
        val pose = GestureStateMachine.mapCategoryToPose(top.categoryName(), top.score())
        onPoseDetected(pose, top.score())
    }

    fun close() {
        try {
            recognizer?.close()
            recognizer = null
        } catch (_: Exception) {}
    }
}
