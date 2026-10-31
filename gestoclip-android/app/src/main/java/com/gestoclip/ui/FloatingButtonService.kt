package com.gestoclip.ui

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.drawable.GradientDrawable
import android.os.IBinder
import android.provider.Settings
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.TextView
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.gestoclip.GestoRuntime
import com.gestoclip.vision.GestureForegroundService
import kotlin.math.abs

class FloatingButtonService : Service() {

    private var windowManager: WindowManager? = null
    private var floatingView: View? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        GestoRuntime.ensureInitialized(applicationContext)
        startForegroundOverlayNotification()
        if (Settings.canDrawOverlays(this)) {
            createOverlayButton()
        } else {
            stopSelf()
        }
    }

    private fun startForegroundOverlayNotification() {
        val channelId = "gestoclip_overlay_channel"
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (nm.getNotificationChannel(channelId) == null) {
            val ch = NotificationChannel(channelId, "Botón Flotante GestoClip", NotificationManager.IMPORTANCE_MIN)
            nm.createNotificationChannel(ch)
        }
        val notif = NotificationCompat.Builder(this, channelId)
            .setSmallIcon(android.R.drawable.ic_menu_share)
            .setContentTitle("GestoClip activo")
            .setContentText("Toca el botón flotante para copiar o pegar con un gesto")
            .setOngoing(true)
            .build()
        startForeground(1002, notif)
    }

    private fun createOverlayButton() {
        val wm = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        windowManager = wm

        val density = resources.displayMetrics.density
        val sizePx = (56 * density).toInt()

        val button = TextView(this).apply {
            text = "🖐️"
            textSize = 22f
            gravity = Gravity.CENTER
            background = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                setColor(Color.parseColor("#DD2563EB"))
                setStroke((2 * density).toInt(), Color.parseColor("#93C5FD"))
            }
            elevation = 12f
        }

        val params = WindowManager.LayoutParams(
            sizePx,
            sizePx,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            x = (16 * density).toInt()
            y = (220 * density).toInt()
        }

        var initialX = 0
        var initialY = 0
        var initialTouchX = 0f
        var initialTouchY = 0f

        button.setOnTouchListener { _, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN -> {
                    initialX = params.x
                    initialY = params.y
                    initialTouchX = event.rawX
                    initialTouchY = event.rawY
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    params.x = initialX + (event.rawX - initialTouchX).toInt()
                    params.y = initialY + (event.rawY - initialTouchY).toInt()
                    wm.updateViewLayout(button, params)
                    true
                }
                MotionEvent.ACTION_UP -> {
                    val dx = abs(event.rawX - initialTouchX)
                    val dy = abs(event.rawY - initialTouchY)
                    if (dx < 12 * density && dy < 12 * density) {
                        GestureForegroundService.start(applicationContext)
                    }
                    true
                }
                else -> false
            }
        }

        wm.addView(button, params)
        floatingView = button
        isRunning = true
    }

    override fun onDestroy() {
        try {
            floatingView?.let { windowManager?.removeView(it) }
        } catch (_: Exception) {}
        floatingView = null
        isRunning = false
        super.onDestroy()
    }

    companion object {
        @Volatile
        var isRunning: Boolean = false
            private set

        fun start(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, FloatingButtonService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, FloatingButtonService::class.java))
        }
    }
}
