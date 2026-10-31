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
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.Image
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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
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
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.gestoclip.AndroidGestureMode
import com.gestoclip.GestoRuntime
import com.gestoclip.R
import com.gestoclip.access.GestoAccessibilityService
import com.gestoclip.net.UdpDiscovery
import com.gestoclip.protocol.Kinds
import com.gestoclip.protocol.Payload
import com.gestoclip.vision.GestureForegroundService
import com.google.zxing.BinaryBitmap
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.util.concurrent.Executors

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        GestoRuntime.ensureInitialized(applicationContext)
        handleIncomingShareIntent(intent)

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

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleIncomingShareIntent(intent)
    }

    private fun handleIncomingShareIntent(intent: Intent?) {
        if (intent == null) return
        when (intent.action) {
            Intent.ACTION_SEND -> {
                @Suppress("DEPRECATION")
                val streamUri = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
                } else {
                    intent.getParcelableExtra(Intent.EXTRA_STREAM) as? Uri
                }
                if (streamUri != null) {
                    GestoRuntime.addUrisToShareQueue(listOf(streamUri))
                } else {
                    val sharedText = intent.getStringExtra(Intent.EXTRA_TEXT)
                    if (!sharedText.isNullOrBlank()) {
                        CoroutineScope(Dispatchers.IO).launch {
                            val payload = Payload(Kinds.TEXT, "compartido.txt", "text/plain", sharedText.toByteArray(Charsets.UTF_8))
                            val sent = GestoRuntime.channel.sendToPeers(payload, GestoRuntime.selectedPeerFps.value.takeIf { it.isNotEmpty() })
                            GestoRuntime.setStatus(if (sent > 0) "📤 Texto compartido enviado a $sent equipo(s)" else "⚠️ Sin equipo emparejado activo para recibir el texto.")
                        }
                    }
                }
            }
            Intent.ACTION_SEND_MULTIPLE -> {
                @Suppress("DEPRECATION")
                val uris: List<Uri> = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java) ?: emptyList()
                } else {
                    intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM) ?: emptyList()
                }
                if (uris.isNotEmpty()) {
                    GestoRuntime.addUrisToShareQueue(uris)
                }
            }
        }
    }
}

@Composable
private fun GestoClipMainScreen() {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var currentTab by remember { mutableStateOf("share") }
    val statusMsg by GestoRuntime.statusMessage.collectAsState()
    val pairedPeers by GestoRuntime.pairedPeers.collectAsState()
    val discoveredPeers by GestoRuntime.discoveredPeers.collectAsState()
    val pendingInbox by GestoRuntime.pendingInbox.collectAsState()
    val shareQueue by GestoRuntime.shareQueue.collectAsState()
    val selectedPeerFps by GestoRuntime.selectedPeerFps.collectAsState()
    val activityLog by GestoRuntime.activityLog.collectAsState()
    val isSending by GestoRuntime.isSending.collectAsState()
    val userSettings by GestoRuntime.settings.collectAsState()

    val photoPickerLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.PickMultipleVisualMedia(20)
    ) { uris ->
        if (uris.isNotEmpty()) GestoRuntime.addUrisToShareQueue(uris)
    }

    val documentPickerLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenMultipleDocuments()
    ) { uris ->
        if (uris.isNotEmpty()) GestoRuntime.addUrisToShareQueue(uris)
    }

    val folderPickerLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocumentTree()
    ) { treeUri ->
        if (treeUri != null) {
            try {
                context.contentResolver.takePersistableUriPermission(
                    treeUri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION
                )
            } catch (_: Exception) {}
            val updated = (userSettings.preselectedFolderUris + treeUri.toString()).distinct()
            GestoRuntime.updateSettings(userSettings.copy(preselectedFolderUris = updated))
        }
    }

    Scaffold(
        containerColor = Color(0xFF121216),
        bottomBar = {
            NavigationBar(containerColor = Color(0xFF18181F)) {
                NavigationBarItem(
                    selected = currentTab == "share",
                    onClick = { currentTab = "share" },
                    icon = { Text("📂", fontSize = 18.sp) },
                    label = { Text("Compartir") }
                )
                NavigationBarItem(
                    selected = currentTab == "gestures",
                    onClick = { currentTab = "gestures" },
                    icon = { Text("🖐️", fontSize = 18.sp) },
                    label = { Text("Gestos") }
                )
                NavigationBarItem(
                    selected = currentTab == "peers",
                    onClick = { currentTab = "peers"; GestoRuntime.refreshLists() },
                    icon = { Text("📱", fontSize = 18.sp) },
                    label = { Text("Dispositivos") }
                )
                NavigationBarItem(
                    selected = currentTab == "settings",
                    onClick = { currentTab = "settings" },
                    icon = { Text("⚙️", fontSize = 18.sp) },
                    label = { Text("Ajustes") }
                )
            }
        }
    ) { innerPadding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp)
        ) {
            // Encabezado con el nuevo logo oficial VectorCraft
            Card(
                colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
                shape = RoundedCornerShape(14.dp),
                modifier = Modifier.fillMaxWidth()
            ) {
                Column(modifier = Modifier.padding(14.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Image(
                            painter = painterResource(id = R.drawable.ic_gestoclip_logo),
                            contentDescription = "GestoClip Logo",
                            modifier = Modifier
                                .size(44.dp)
                                .clip(RoundedCornerShape(10.dp))
                        )
                        Spacer(modifier = Modifier.width(12.dp))
                        Column {
                            Text("GestoClip — Compartir y Gestos", fontSize = 19.sp, fontWeight = FontWeight.Bold, color = Color.White)
                            Text(
                                "IP: ${UdpDiscovery.getLocalIpAddress()}:${GestoRuntime.channel.listenPort}  •  Pares: ${pairedPeers.size}",
                                fontSize = 12.sp,
                                color = Color(0xFFA1A1AA)
                            )
                        }
                    }
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(statusMsg, fontSize = 12.5.sp, color = Color(0xFF60A5FA), fontWeight = FontWeight.Medium)
                }
            }

            when (currentTab) {
                "share" -> ShareTabContent(
                    shareQueue = shareQueue,
                    pairedPeers = pairedPeers,
                    discoveredPeers = discoveredPeers,
                    selectedPeerFps = selectedPeerFps,
                    activityLog = activityLog,
                    isSending = isSending,
                    onPickPhotos = {
                        photoPickerLauncher.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))
                    },
                    onPickFiles = {
                        documentPickerLauncher.launch(arrayOf("*/*"))
                    },
                    onGoToPeers = { currentTab = "peers" }
                )

                "gestures" -> GesturesTabContent(pendingInbox = pendingInbox)

                "peers" -> PeersTabContent(
                    pairedPeers = pairedPeers,
                    discoveredPeers = discoveredPeers,
                    scope = scope
                )

                "settings" -> SettingsTabContent(
                    userSettings = userSettings,
                    onAddFolder = { folderPickerLauncher.launch(null) }
                )
            }
        }
    }
}

@Composable
private fun ShareTabContent(
    shareQueue: List<com.gestoclip.storage.SharedItemCandidate>,
    pairedPeers: List<com.gestoclip.net.PairedPeer>,
    discoveredPeers: List<com.gestoclip.net.DiscoveredPeer>,
    selectedPeerFps: Set<String>,
    activityLog: List<com.gestoclip.TransferActivityEntry>,
    isSending: Boolean,
    onPickPhotos: () -> Unit,
    onPickFiles: () -> Unit,
    onGoToPeers: () -> Unit
) {
    val context = LocalContext.current

    // 1. Selección de fotos, videos o archivos (Sin permisos invasivos de galería)
    Card(
        colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("1. Elegir contenido para compartir", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)
            Text(
                "Usa los selectores seguros del sistema o comparte hacia GestoClip desde cualquier app (Galería, Archivos, Navegador).",
                fontSize = 12.sp,
                color = Color(0xFFA1A1AA)
            )
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxWidth()) {
                Button(onClick = onPickPhotos, modifier = Modifier.weight(1f)) {
                    Text("🖼️ Foto o Video")
                }
                Button(onClick = onPickFiles, modifier = Modifier.weight(1f)) {
                    Text("📄 Elegir Archivo")
                }
            }

            if (shareQueue.isNotEmpty()) {
                Spacer(modifier = Modifier.height(4.dp))
                Text(
                    "Seleccionados (${shareQueue.size}):",
                    fontSize = 13.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = Color(0xFF34D399)
                )
                for (item in shareQueue) {
                    Text(
                        "• ${item.name} (${item.sizeFormatted}) [${item.kind}]",
                        fontSize = 12.sp,
                        color = Color(0xFFE4E4E7)
                    )
                }
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                    if (!isSending) {
                        Button(
                            onClick = { GestoRuntime.sendShareQueueNow() },
                            modifier = Modifier.weight(1f)
                        ) {
                            Text("📤 Enviar ahora (${shareQueue.size})")
                        }
                        OutlinedButton(onClick = { GestoRuntime.clearShareQueue() }) {
                            Text("Limpiar")
                        }
                    } else {
                        OutlinedButton(
                            onClick = { GestoRuntime.cancelActiveSend() },
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Text("🚫 Cancelar envío en curso")
                        }
                    }
                }
            }
        }
    }

    // 2. Selección de equipos destino
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
                Text("2. Equipos destino", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)
                OutlinedButton(onClick = onGoToPeers) { Text("Emparejar QR") }
            }
            if (pairedPeers.isEmpty()) {
                Text(
                    "Aún no tienes equipos emparejados. Ve a la pestaña 'Dispositivos' para escanear el QR de tu PC.",
                    fontSize = 12.sp,
                    color = Color(0xFFA1A1AA)
                )
            } else {
                Text(
                    "Elige a qué equipos enviar (si no marcas ninguno, se envía a todos los emparejados conectados):",
                    fontSize = 11.5.sp,
                    color = Color(0xFFA1A1AA)
                )
                for (p in pairedPeers) {
                    val online = discoveredPeers.any { it.pubKeyFp.equals(p.fingerprint, ignoreCase = true) }
                    val isSelected = selectedPeerFps.contains(p.fingerprint.lowercase())
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Checkbox(
                            checked = isSelected,
                            onCheckedChange = { GestoRuntime.togglePeerSelection(p.fingerprint) }
                        )
                        Text(
                            "${p.name} [${p.platform}] — ${if (online) "🟢 En línea" else "⚪ Desconectado"}",
                            fontSize = 13.sp,
                            color = Color.White
                        )
                    }
                }
            }
        }
    }

    // 3. Actividad reciente y "Abrir ubicación"
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
                Text("3. Actividad reciente", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)
                OutlinedButton(onClick = {
                    try {
                        context.startActivity(Intent(android.app.DownloadManager.ACTION_VIEW_DOWNLOADS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    } catch (_: Exception) {}
                }) {
                    Text("📂 Abrir Descargas")
                }
            }
            Text(
                "Las imágenes recibidas se guardan automáticamente en Imágenes/GestoClip y los archivos en Descargas/GestoClip.",
                fontSize = 11.5.sp,
                color = Color(0xFFA1A1AA)
            )
            if (activityLog.isEmpty()) {
                Text("Sin actividad registrada en esta sesión.", fontSize = 12.sp, color = Color(0xFF71717A))
            } else {
                for (entry in activityLog.take(15)) {
                    val icon = if (entry.isSent) "📤" else "📥"
                    val kb = (entry.sizeBytes / 1024).coerceAtLeast(1)
                    val stateText = when (entry.status) {
                        "COMPLETED" -> "✅"
                        "IN_PROGRESS" -> "⏳"
                        "CANCELLED" -> "🚫"
                        else -> "❌ ${entry.errorMessage ?: ""}"
                    }
                    Row(
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(
                            "$icon ${entry.fileName} ($kb KB) • ${entry.peerName} $stateText [${entry.timestamp}]",
                            fontSize = 12.sp,
                            color = Color(0xFFE4E4E7),
                            modifier = Modifier.weight(1f)
                        )
                        if (!entry.isSent && entry.savedUriString != null) {
                            OutlinedButton(onClick = {
                                try {
                                    val viewIntent = Intent(Intent.ACTION_VIEW).apply {
                                        setDataAndType(Uri.parse(entry.savedUriString), if (entry.kind == Kinds.IMAGE || entry.kind == Kinds.SCREENSHOT) "image/*" else "*/*")
                                        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                                    }
                                    context.startActivity(viewIntent)
                                } catch (_: Exception) {}
                            }) {
                                Text("Abrir", fontSize = 11.sp)
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun GesturesTabContent(pendingInbox: Payload?) {
    val context = LocalContext.current
    var floatingActive by remember { mutableStateOf(FloatingButtonService.isRunning) }

    Card(
        colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("Gestos de la Mano en el Aire (🖐️ ⇄ ✊)", fontSize = 17.sp, fontWeight = FontWeight.Bold, color = Color.White)
            Text(
                "• Cerrar el puño (🖐️ → ✊): Ejecuta tu acción configurada (enviar archivos en cola, carpeta preseleccionada o capturar selección/pantalla).\n• Abrir la palma (✊ → 🖐️): Pega o guarda lo último recibido.",
                fontSize = 12.5.sp,
                color = Color(0xFFA1A1AA)
            )
            Button(
                onClick = { GestureForegroundService.start(context) },
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("📷 Activar Cámara de Gestos Ahora (5 s)")
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxWidth()) {
                OutlinedButton(
                    onClick = { GestoRuntime.executeCopyOrCapture() },
                    modifier = Modifier.weight(1f)
                ) {
                    Text("✊ Acción de Puño")
                }
                OutlinedButton(
                    onClick = { GestoRuntime.executePaste() },
                    modifier = Modifier.weight(1f)
                ) {
                    Text(if (pendingInbox != null) "🖐️ Pegar (${pendingInbox.name})" else "🖐️ Pegar recibido")
                }
            }

            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("Botón Flotante Rápido (🖐️)", fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = Color.White)
                    Text("Dispara el gesto o envío rápido sobre cualquier aplicación", fontSize = 12.sp, color = Color(0xFFA1A1AA))
                }
                Switch(
                    checked = floatingActive,
                    onCheckedChange = { want ->
                        if (!Settings.canDrawOverlays(context)) {
                            context.startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}")))
                        } else {
                            if (want) FloatingButtonService.start(context) else FloatingButtonService.stop(context)
                            floatingActive = want
                        }
                    }
                )
            }
        }
    }
}

@Composable
private fun PeersTabContent(
    pairedPeers: List<com.gestoclip.net.PairedPeer>,
    discoveredPeers: List<com.gestoclip.net.DiscoveredPeer>,
    scope: CoroutineScope
) {
    val context = LocalContext.current
    var showQrScanner by remember { mutableStateOf(false) }
    var manualQrUri by remember { mutableStateOf("") }
    val hasCameraPerm = ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED

    Card(
        colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Emparejar Nuevo Dispositivo por QR", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)
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
                Text("🔗 Conectar y Emparejar")
            }
        }
    }

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
                Text("Equipos Emparejados y Red LAN", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)
                OutlinedButton(onClick = { GestoRuntime.refreshLists() }) { Text("Actualizar") }
            }

            Text(
                "Si un equipo ya emparejado vuelve a conectarse o cambia de IP, GestoClip lo reconoce automáticamente.",
                fontSize = 11.5.sp,
                color = Color(0xFFA1A1AA)
            )

            if (pairedPeers.isEmpty()) {
                Text("Ningún equipo emparejado todavía.", fontSize = 12.sp, color = Color(0xFF71717A))
            } else {
                for (p in pairedPeers) {
                    val online = discoveredPeers.firstOrNull { it.pubKeyFp.equals(p.fingerprint, ignoreCase = true) }
                    Row(
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text("${p.name} [${p.platform}]", fontSize = 13.5.sp, fontWeight = FontWeight.SemiBold, color = Color.White)
                            Text(
                                if (online != null) "🟢 En línea (${online.ipAddress}:${online.port}) • FP: ${p.fingerprint.take(10)}…"
                                else "⚪ Desconectado • FP: ${p.fingerprint.take(10)}…",
                                fontSize = 11.5.sp,
                                color = Color(0xFFA1A1AA),
                                fontFamily = FontFamily.Monospace
                            )
                        }
                        OutlinedButton(onClick = {
                            GestoRuntime.pairedStore.remove(p.fingerprint)
                            GestoRuntime.refreshLists()
                            GestoRuntime.setStatus("🗑️ Dispositivo eliminado: ${p.name}")
                        }) { Text("Eliminar", fontSize = 11.sp) }
                    }
                }
            }

            Spacer(modifier = Modifier.height(6.dp))
            Text("📡 Detectados ahora en Wi-Fi (${discoveredPeers.size}):", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF60A5FA))
            for (d in discoveredPeers) {
                val paired = GestoRuntime.pairedStore.isPaired(d.pubKeyFp)
                Text(
                    "• ${d.name} [${d.platform}] — ${d.ipAddress}:${d.port} (${if (paired) "✅ Reconocido" else "Sin emparejar"})",
                    fontSize = 12.sp,
                    color = Color(0xFFE4E4E7)
                )
            }
        }
    }
}

@Composable
private fun SettingsTabContent(
    userSettings: com.gestoclip.AndroidUserSettings,
    onAddFolder: () -> Unit
) {
    val context = LocalContext.current
    var hasCameraPerm by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        )
    }
    val permLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { res ->
        hasCameraPerm = res[Manifest.permission.CAMERA] == true ||
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
    }

    Card(
        colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("¿Qué debe hacer el gesto de Puño Cerrado (✊)?", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)

            Row(verticalAlignment = Alignment.CenterVertically) {
                RadioButton(
                    selected = userSettings.fistMode == AndroidGestureMode.CLIPBOARD_OR_SCREENSHOT,
                    onClick = { GestoRuntime.updateSettings(userSettings.copy(fistMode = AndroidGestureMode.CLIPBOARD_OR_SCREENSHOT)) }
                )
                Text("Copiar texto seleccionado o tomar captura de pantalla (y enviar cola si hay archivos elegidos)", fontSize = 12.5.sp, color = Color.White)
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                RadioButton(
                    selected = userSettings.fistMode == AndroidGestureMode.QUEUED_SHARE_ITEMS,
                    onClick = { GestoRuntime.updateSettings(userSettings.copy(fistMode = AndroidGestureMode.QUEUED_SHARE_ITEMS)) }
                )
                Text("Priorizar envío de archivos seleccionados en el Centro para Compartir", fontSize = 12.5.sp, color = Color.White)
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                RadioButton(
                    selected = userSettings.fistMode == AndroidGestureMode.PRESELECTED_FOLDERS,
                    onClick = { GestoRuntime.updateSettings(userSettings.copy(fistMode = AndroidGestureMode.PRESELECTED_FOLDERS)) }
                )
                Text("Enviar los archivos más recientes de mis carpetas preseleccionadas:", fontSize = 12.5.sp, color = Color.White)
            }

            for (folderUri in userSettings.preselectedFolderUris) {
                Row(
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("📁 ${Uri.decode(folderUri).takeLast(42)}", fontSize = 11.5.sp, color = Color(0xFFA1A1AA), modifier = Modifier.weight(1f))
                    OutlinedButton(onClick = {
                        val next = userSettings.preselectedFolderUris.filterNot { it == folderUri }
                        GestoRuntime.updateSettings(userSettings.copy(preselectedFolderUris = next))
                    }) { Text("Quitar", fontSize = 11.sp) }
                }
            }
            OutlinedButton(onClick = onAddFolder) {
                Text("➕ Agregar carpeta preseleccionada...")
            }
        }
    }

    Card(
        colors = CardDefaults.cardColors(containerColor = Color(0xFF1E1E26)),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Permisos del Sistema", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color.White)

            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                Text(if (hasCameraPerm) "✅ Cámara y Notificaciones" else "🔸 Permiso de Cámara", fontSize = 13.sp, color = Color.White)
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
                val canOverlay = Settings.canDrawOverlays(context)
                Text(if (canOverlay) "✅ Superposición (Botón Flotante)" else "🔸 Permiso de Superposición", fontSize = 13.sp, color = Color.White)
                OutlinedButton(onClick = {
                    context.startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}")))
                }) { Text("Configurar") }
            }

            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                val isAcc = GestoAccessibilityService.instance != null
                Text(if (isAcc) "✅ Accesibilidad activa" else "🔸 Servicio de Accesibilidad", fontSize = 13.sp, color = Color.White)
                OutlinedButton(onClick = {
                    context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                }) { Text("Ajustes") }
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
