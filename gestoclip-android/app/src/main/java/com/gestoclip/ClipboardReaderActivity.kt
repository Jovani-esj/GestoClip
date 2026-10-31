package com.gestoclip

import android.app.Activity
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Bundle

/**
 * Actividad transparente (~100 ms) para leer el portapapeles tras ACTION_COPY en Android 10+.
 */
class ClipboardReaderActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.decorView.postDelayed({
            readAndFinish()
        }, 80L)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            readAndFinish()
        }
    }

    private fun readAndFinish() {
        try {
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            val text = cm.primaryClip?.getItemAt(0)?.coerceToText(this)?.toString()
            GestoRuntime.pendingClipboardDeferred?.complete(text)
        } catch (_: Exception) {
            GestoRuntime.pendingClipboardDeferred?.complete(null)
        } finally {
            finish()
            overridePendingTransition(0, 0)
        }
    }

    companion object {
        fun launch(context: Context) {
            val intent = Intent(context, ClipboardReaderActivity::class.java).apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            }
            context.startActivity(intent)
        }
    }
}
