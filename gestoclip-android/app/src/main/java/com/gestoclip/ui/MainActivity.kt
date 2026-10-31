package com.gestoclip.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.gestoclip.GestoRuntime
import com.gestoclip.access.GestoAccessibilityService
import com.gestoclip.net.UdpDiscovery
import com.gestoclip.vision.GestureForegroundService
import com.google.zxing.BinaryBitmap
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import kotlinx.coroutines.launch
import java.util.concurrent.Executors

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        GestoRuntime.ensureInitialized(applicationContext)

        setContent {
            MaterialTheme(
                colorScheme = darkColorScheme(
                    primary = Color(0xFF3B82F6),
                    secondary = Color(0xFF10B981),
                    background = Color(0xFF121216),
                    surface = Color(0xFF1E1E26)
                )
            ) {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    GestoClipMainScreen()
                }
            }
        }
    }
}

@Composable
private fun GestoClipMainScreen() {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    val statusMsg by GestoRuntime.statusMessage.collectAsState()
    val pairedPeers by GestoRuntime.pairedPeers.collectAsState()
    val discoveredPeers by GestoRuntime.discoveredPeers.collectAsState()
    val pendingInbox by GestoRuntime.pendingInbox.collectAsState()

    var hasCameraPerm by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        )
    }
    var canOverlay by remember { mutableStateOf(Settings.canDrawOverlays(context)) }
    var isAccessibilityOn by remember { mutableStateOf(GestoAccessibilityService.instance != null) }
    var floatingActive by remember { mutableStateOf(FloatingButtonService.isRunning) }
    var showQrScanner by remember { mutableStateOf(false) }
    var manualQrUri by remember { mutableStateOf("") }

    val permLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { res ->
        hasCameraPerm = res[Manifest.permission.CAMERA] == true ||
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        // Encabezado
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
            shape = RoundedCornerShape(14.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(16.dp)) {
                Text("🖐️ GestoClip Android", fontSize = 22.sp, fontWeight = FontWeight.Bold, color = Color.White)
                Text(
                    "IP Local: ${UdpDiscovery.getLocalIpAddress()}:${GestoRuntime.channel.listenPort}  •  ID: ${GestoRuntime.deviceId}",
                    fontSize = 12.sp,
                    color = Color(0xFFA1A1AA)
                )
                Spacer(modifier = Modifier.height(8.dp))
                Text(statusMsg, fontSize = 13.sp, color = Color(0xFF60A5FA), fontWeight = FontWeight.Medium)
            }
        }

        // Tarjeta de Permisos y Asistente
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
            shape = RoundedCornerShape(14.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("1. Permisos y Botón Flotante", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)

                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                    Text(if (hasCameraPerm) "✅ Cámara y Notificaciones" else "🔸 Falta permiso de Cámara", fontSize = 13.sp, color = Color.White)
                    if (!hasCameraPerm) {
                        Button(onClick = {
                            val perms = mutableListOf(Manifest.permission.CAMERA)
                            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                                perms.add(Manifest.permission.POST_NOTIFICATIONS)
                            }
                            permLauncher.launch(perms.toTypedArray())
                        }) { Text("Conceder") }
                    }
                }

                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                    Text(if (canOverlay) "✅ Superposición (Botón Flotante)" else "🔸 Permiso de Superposición", fontSize = 13.sp, color = Color.White)
                    OutlinedButton(onClick = {
                        val intent = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}"))
                        context.startActivity(intent)
                        canOverlay = Settings.canDrawOverlays(context)
                    }) { Text(if (canOverlay) "Revisar" else "Activar") }
                }

                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                    isAccessibilityOn = GestoAccessibilityService.instance != null
                    Text(
                        if (isAccessibilityOn) "✅ Servicio de Accesibilidad activo" else "🔸 Servicio de Accesibilidad (Copiar/Capturar)",
                        fontSize = 13.sp,
                        color = Color.White,
                        modifier = Modifier.weight(1f)
                    )
                    OutlinedButton(onClick = {
                        context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                    }) { Text("Ajustes") }
                }

                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text("Mostrar Botón Flotante (🖐️)", fontSize = 14.sp, fontWeight = FontWeight.Medium, color = Color.White)
                        Text("Tócalo desde cualquier app para leer tu gesto en 5 s", fontSize = 11.5.sp, color = Color(0xFFA1A1AA))
                    }
                    Switch(
                        checked = floatingActive,
                        onCheckedChange = { want ->
                            canOverlay = Settings.canDrawOverlays(context)
                            if (!canOverlay) {
                                val intent = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}"))
                                context.startActivity(intent)
                            } else {
                                if (want) FloatingButtonService.start(context) else FloatingButtonService.stop(context)
                                floatingActive = want
                            }
                        }
                    )
                }
            }
        }

        // Tarjeta de Gestos y Acciones Directas
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
            shape = RoundedCornerShape(14.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("2. Ejecutar Gesto o Acción Ahora", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)
                Button(
                    onClick = { GestureForegroundService.start(context) },
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("📷 Abrir Cámara de Gestos (5 s) — 🖐️⇄✊")
                }
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxWidth()) {
                    OutlinedButton(
                        onClick = { GestoRuntime.executeCopyOrCapture() },
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("✊ Copiar / Capturar")
                    }
                    OutlinedButton(
                        onClick = { GestoRuntime.executePaste() },
                        modifier = Modifier.weight(1f)
                    ) {
                        Text(if (pendingInbox != null) "🖐️ Pegar (${pendingInbox!!.name})" else "🖐️ Pegar recibido")
                    }
                }
            }
        }

        // Tarjeta de Emparejamiento QR con Windows 11
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
            shape = RoundedCornerShape(14.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("3. Emparejar con PC Windows (QR)", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)
                Button(
                    onClick = { showQrScanner = !showQrScanner },
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(if (showQrScanner) "Cerrar Escáner QR" else "📷 Escanear QR de Windows con la Cámara")
                }

                if (showQrScanner && hasCameraPerm) {
                    QrScannerView(
                        onQrDetected = { qrText ->
                            showQrScanner = false
                            manualQrUri = qrText
                            scope.launch {
                                try {
                                    GestoRuntime.pairWithQrUri(qrText)
                                } catch (e: Exception) {
                                    GestoRuntime.setStatus("❌ Error al emparejar: ${e.message}")
                                }
                            }
                        }
                    )
                }

                OutlinedTextField(
                    value = manualQrUri,
                    onValueChange = { manualQrUri = it },
                    label = { Text("O pega aquí el enlace gestoclip://pair?...") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                OutlinedButton(
                    onClick = {
                        scope.launch {
                            try {
                                GestoRuntime.pairWithQrUri(manualQrUri)
                            } catch (e: Exception) {
                                GestoRuntime.setStatus("❌ ${e.message}")
                            }
                        }
                    }
                ) {
                    Text("🔗 Conectar con enlace")
                }
            }
        }

        // Tarjeta de Dispositivos Emparejados y Red Local
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
            shape = RoundedCornerShape(14.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row(
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("4. Equipos Emparejados y Red LAN", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)
                    OutlinedButton(onClick = { GestoRuntime.refreshLists() }) { Text("Actualizar") }
                }

                Text("🔒 Emparejados (${pairedPeers.size}):", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF34D399))
                if (pairedPeers.isEmpty()) {
                    Text("Ningún equipo emparejado todavía.", fontSize = 12.sp, color = Color(0xFFA1A1AA))
                } else {
                    for (p in pairedPeers) {
                        Row(
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Text("${p.name} [${p.platform}] (${p.fingerprint.take(10)}…)", fontSize = 12.5.sp, color = Color.White, fontFamily = FontFamily.Monospace)
                            OutlinedButton(onClick = {
                                GestoRuntime.pairedStore.remove(p.fingerprint)
                                GestoRuntime.refreshLists()
                            }) { Text("Quitar", fontSize = 11.sp) }
                        }
                    }
                }

                Spacer(modifier = Modifier.height(4.dp))
                Text("📡 Detectados en Wi-Fi (${discoveredPeers.size}):", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF60A5FA))
                if (discoveredPeers.isEmpty()) {
                    Text("Esperando balizas UDP en el puerto 8766...", fontSize = 12.sp, color = Color(0xFFA1A1AA))
                } else {
                    for (d in discoveredPeers) {
                        val paired = GestoRuntime.pairedStore.isPaired(d.pubKeyFp)
                        Text(
                            "• ${d.name} [${d.platform}] — ${d.ipAddress}:${d.port} (${if (paired) "✅ Emparejado" else "No emparejado"})",
                            fontSize = 12.sp,
                            color = Color(0xFFE4E4E7)
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun QrScannerView(onQrDetected: (String) -> Unit) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val reader = remember { MultiFormatReader() }
    var detected by remember { mutableStateOf(false) }

    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(240.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(Color.Black)
    ) {
        AndroidView(
            modifier = Modifier.fillMaxSize(),
            factory = { ctx ->
                val previewView = PreviewView(ctx)
                val cameraProviderFuture = ProcessCameraProvider.getInstance(ctx)
                val executor = Executors.newSingleThreadExecutor()

                cameraProviderFuture.addListener({
                    try {
                        val provider = cameraProviderFuture.get()
                        val preview = Preview.Builder().build().also {
                            it.surfaceProvider = previewView.surfaceProvider
                        }
                        val analysis = ImageAnalysis.Builder()
                            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                            .build()

                        analysis.setAnalyzer(executor) { imageProxy ->
                            try {
                                if (!detected && imageProxy.planes.isNotEmpty()) {
                                    val buffer = imageProxy.planes[0].buffer
                                    val bytes = ByteArray(buffer.remaining())
                                    buffer.get(bytes)
                                    val w = imageProxy.width
                                    val h = imageProxy.height
                                    val source = PlanarYUVLuminanceSource(bytes, w, h, 0, 0, w, h, false)
                                    val bitmap = BinaryBitmap(HybridBinarizer(source))
                                    val result = reader.decodeWithState(bitmap)
                                    val txt = result.text
                                    if (!txt.isNullOrBlank() && txt.startsWith("gestoclip://pair", ignoreCase = true)) {
                                        detected = true
                                        previewView.post { onQrDetected(txt) }
                                    }
                                }
                            } catch (_: Exception) {
                            } finally {
                                reader.reset()
                                imageProxy.close()
                            }
                        }

                        provider.unbindAll()
                        provider.bindToLifecycle(lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
                    } catch (_: Exception) {}
                }, ContextCompat.getMainExecutor(ctx))

                previewView
            }
        )
    }

    DisposableEffect(Unit) {
        onDispose {
            try {
                ProcessCameraProvider.getInstance(context).get().unbindAll()
            } catch (_: Exception) {}
        }
    }
}
