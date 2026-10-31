package com.gestoclip.access

import android.accessibilityservice.AccessibilityService
import android.graphics.Bitmap
import android.view.Display
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import com.gestoclip.GestoRuntime
import com.gestoclip.protocol.Kinds
import com.gestoclip.protocol.Payload
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.delay
import java.io.ByteArrayOutputStream

class GestoAccessibilityService : AccessibilityService() {

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        GestoRuntime.ensureInitialized(applicationContext)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // No rastreamos eventos pasivos por privacidad; solo actuamos cuando el usuario hace un gesto.
    }

    override fun onInterrupt() {}

    override fun onDestroy() {
        if (instance === this) instance = null
        super.onDestroy()
    }

    suspend fun captureSelectionOrScreenshot(): Payload {
        val root = rootInActiveWindow
        val selectedNode = findNodeWithTextSelection(findFocus(AccessibilityNodeInfo.FOCUS_INPUT))
            ?: findNodeWithTextSelection(root)

        if (selectedNode != null) {
            val copied = selectedNode.performAction(AccessibilityNodeInfo.ACTION_COPY)
            if (copied) {
                delay(80L)
                val text = GestoRuntime.awaitClipboardViaTransparentActivity()
                if (!text.isNullOrEmpty()) {
                    return Payload(
                        kind = Kinds.TEXT,
                        name = "seleccion_android.txt",
                        mime = "text/plain",
                        data = text.toByteArray(Charsets.UTF_8)
                    )
                }
            }
        }

        // Sin selección de texto: tomamos captura de pantalla nativa (API 30+)
        return takeScreenPng()
    }

    fun pasteIntoFocusedNode(): Boolean {
        val focus = findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
            ?: findEditableNode(rootInActiveWindow)
            ?: return false
        return focus.performAction(AccessibilityNodeInfo.ACTION_PASTE)
    }

    private fun findNodeWithTextSelection(node: AccessibilityNodeInfo?): AccessibilityNodeInfo? {
        if (node == null) return null
        val start = node.textSelectionStart
        val end = node.textSelectionEnd
        if (start >= 0 && end >= 0 && start != end) {
            return node
        }
        for (i in 0 until node.childCount) {
            val found = findNodeWithTextSelection(node.getChild(i))
            if (found != null) return found
        }
        return null
    }

    private fun findEditableNode(node: AccessibilityNodeInfo?): AccessibilityNodeInfo? {
        if (node == null) return null
        if (node.isEditable && node.isFocused) return node
        for (i in 0 until node.childCount) {
            val found = findEditableNode(node.getChild(i))
            if (found != null) return found
        }
        return null
    }

    private suspend fun takeScreenPng(): Payload {
        val deferred = CompletableDeferred<Payload>()
        takeScreenshot(
            Display.DEFAULT_DISPLAY,
            mainExecutor,
            object : TakeScreenshotCallback {
                override fun onSuccess(screenshot: ScreenshotResult) {
                    try {
                        val hwBuffer = screenshot.hardwareBuffer
                        val colorSpace = screenshot.colorSpace
                        val hwBitmap = Bitmap.wrapHardwareBuffer(hwBuffer, colorSpace)
                        val swBitmap = hwBitmap?.copy(Bitmap.Config.ARGB_8888, false)
                        hwBuffer.close()
                        hwBitmap?.recycle()
                        if (swBitmap == null) {
                            deferred.completeExceptionally(IllegalStateException("No se pudo convertir la captura de pantalla"))
                            return
                        }
                        val bos = ByteArrayOutputStream()
                        swBitmap.compress(Bitmap.CompressFormat.PNG, 90, bos)
                        swBitmap.recycle()
                        deferred.complete(
                            Payload(
                                kind = Kinds.SCREENSHOT,
                                name = "captura_${System.currentTimeMillis()}.png",
                                mime = "image/png",
                                data = bos.toByteArray()
                            )
                        )
                    } catch (e: Exception) {
                        deferred.completeExceptionally(e)
                    }
                }

                override fun onFailure(errorCode: Int) {
                    deferred.completeExceptionally(IllegalStateException("takeScreenshot falló con código $errorCode"))
                }
            }
        )
        return deferred.await()
    }

    companion object {
        @Volatile
        var instance: GestoAccessibilityService? = null
            private set
    }
}
