package com.gestoclip.vision

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.Looper
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleService
import com.gestoclip.GestoRuntime
import java.util.concurrent.Executors

class GestureForegroundService : LifecycleService() {

    private val cameraExecutor = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val stateMachine = GestureStateMachine(stableFrames = 4)
    private var mpHelper: MediaPipeGestureHelper? = null
    private var cameraProvider: ProcessCameraProvider? = null
    private var completed = false

    private val timeoutRunnable = Runnable {
        if (!completed) {
            completed = true
            GestoRuntime.setStatus("⏱️ Tiempo de cámara agotado sin detectar transición de gesto.")
            GestoRuntime.toast("No se detectó ningún gesto")
            stopSelfCleanly()
        }
    }

    override fun onCreate() {
        super.onCreate()
        GestoRuntime.ensureInitialized(applicationContext)
        startForegroundWithCameraType()
        startCameraSession()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        return START_NOT_STICKY
    }

    private fun startForegroundWithCameraType() {
        val channelId = "gestoclip_camera_channel"
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (nm.getNotificationChannel(channelId) == null) {
            val ch = NotificationChannel(channelId, "Detección de Gestos", NotificationManager.IMPORTANCE_LOW)
            nm.createNotificationChannel(ch)
        }
        val notif: Notification = NotificationCompat.Builder(this, channelId)
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentTitle("GestoClip — Cámara activa (5 s)")
            .setContentText("Muestra palma → puño (copiar) o puño → palma (pegar)")
            .setOngoing(true)
            .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(1001, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA)
        } else {
            startForeground(1001, notif)
        }
    }

    private fun startCameraSession() {
        GestoRuntime.setStatus("📷 Cámara activa (5 s): haz palma→puño (copiar) o puño→palma (pegar)")
        GestoRuntime.toast("Cámara activa: haz tu gesto (🖐️⇄✊)")

        try {
            mpHelper = MediaPipeGestureHelper(this) { pose, _ ->
                if (completed) return@MediaPipeGestureHelper
                val ev = stateMachine.push(pose)
                if (ev != GestureEvent.NONE) {
                    completed = true
                    mainHandler.post {
                        when (ev) {
                            GestureEvent.COPY -> GestoRuntime.executeCopyOrCapture()
                            GestureEvent.PASTE -> GestoRuntime.executePaste()
                            GestureEvent.NONE -> {}
                        }
                        stopSelfCleanly()
                    }
                }
            }
        } catch (e: Exception) {
            GestoRuntime.setStatus("❌ Error al iniciar MediaPipe: ${e.message}")
            stopSelfCleanly()
            return
        }

        val future = ProcessCameraProvider.getInstance(this)
        future.addListener({
            try {
                val provider = future.get()
                cameraProvider = provider
                val resolutionSelector = ResolutionSelector.Builder()
                    .setResolutionStrategy(ResolutionStrategy.HIGHEST_AVAILABLE_STRATEGY)
                    .build()
                val analysis = ImageAnalysis.Builder()
                    .setResolutionSelector(resolutionSelector)
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .build()

                analysis.setAnalyzer(cameraExecutor) { imageProxy ->
                    if (!completed) {
                        mpHelper?.recognizeAsync(imageProxy)
                    } else {
                        imageProxy.close()
                    }
                }

                provider.unbindAll()
                provider.bindToLifecycle(this, CameraSelector.DEFAULT_FRONT_CAMERA, analysis)
                mainHandler.postDelayed(timeoutRunnable, 5500L)
            } catch (e: Exception) {
                GestoRuntime.setStatus("❌ No se pudo abrir la cámara frontal: ${e.message}")
                stopSelfCleanly()
            }
        }, ContextCompat.getMainExecutor(this))
    }

    private fun stopSelfCleanly() {
        mainHandler.removeCallbacks(timeoutRunnable)
        try {
            cameraProvider?.unbindAll()
            mpHelper?.close()
        } catch (_: Exception) {}
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        mainHandler.removeCallbacks(timeoutRunnable)
        try {
            cameraProvider?.unbindAll()
            mpHelper?.close()
            cameraExecutor.shutdown()
        } catch (_: Exception) {}
        super.onDestroy()
    }

    companion object {
        fun start(context: Context) {
            val intent = Intent(context, GestureForegroundService::class.java)
            ContextCompat.startForegroundService(context, intent)
        }
    }
}
