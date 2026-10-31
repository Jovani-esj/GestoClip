package com.gestoclip

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import androidx.documentfile.provider.DocumentFile
import com.gestoclip.access.GestoAccessibilityService
import com.gestoclip.net.DeviceCert
import com.gestoclip.net.DeviceIdentity
import com.gestoclip.net.DiscoveredPeer
import com.gestoclip.net.PairedPeer
import com.gestoclip.net.PairedStore
import com.gestoclip.net.PairingService
import com.gestoclip.net.QrPairInvite
import com.gestoclip.net.SecureChannel
import com.gestoclip.net.UdpDiscovery
import com.gestoclip.protocol.Beacon
import com.gestoclip.protocol.Kinds
import com.gestoclip.protocol.PairAckMessage
import com.gestoclip.protocol.Payload
import com.gestoclip.protocol.ProtocolJson
import com.gestoclip.storage.MediaStoreSaver
import com.gestoclip.storage.SharedItemCandidate
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.Serializable
import java.io.File
import java.io.IOException
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.UUID

@Serializable
enum class AndroidGestureMode {
    CLIPBOARD_OR_SCREENSHOT,
    QUEUED_SHARE_ITEMS,
    PRESELECTED_FOLDERS
}

@Serializable
data class AndroidUserSettings(
    val fistMode: AndroidGestureMode = AndroidGestureMode.CLIPBOARD_OR_SCREENSHOT,
    val preselectedFolderUris: List<String> = emptyList(),
    val autoSaveReceivedFiles: Boolean = true
)

data class TransferActivityEntry(
    val id: String,
    val fileName: String,
    val kind: String,
    val sizeBytes: Long,
    val isSent: Boolean,
    val peerName: String,
    val status: String, // "IN_PROGRESS", "COMPLETED", "FAILED", "CANCELLED"
    val savedUriString: String? = null,
    val errorMessage: String? = null,
    val timestamp: String = SimpleDateFormat("HH:mm:ss", Locale.getDefault()).format(Date())
)

object GestoRuntime {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mainHandler = Handler(Looper.getMainLooper())

    private var initialized = false
    lateinit var appContext: Context
        private set

    lateinit var deviceId: String
        private set
    lateinit var deviceName: String
        private set
    lateinit var identity: DeviceIdentity
        private set
    lateinit var pairedStore: PairedStore
        private set
    lateinit var pairingService: PairingService
        private set
    lateinit var discovery: UdpDiscovery
        private set
    lateinit var channel: SecureChannel
        private set

    private lateinit var settingsFile: File

    private val _settings = MutableStateFlow(AndroidUserSettings())
    val settings: StateFlow<AndroidUserSettings> = _settings

    private val _statusMessage = MutableStateFlow("Listo para compartir archivos o gestos.")
    val statusMessage: StateFlow<String> = _statusMessage

    private val _pairedPeers = MutableStateFlow<List<PairedPeer>>(emptyList())
    val pairedPeers: StateFlow<List<PairedPeer>> = _pairedPeers

    private val _discoveredPeers = MutableStateFlow<List<DiscoveredPeer>>(emptyList())
    val discoveredPeers: StateFlow<List<DiscoveredPeer>> = _discoveredPeers

    private val _pendingInbox = MutableStateFlow<Payload?>(null)
    val pendingInbox: StateFlow<Payload?> = _pendingInbox

    private val _shareQueue = MutableStateFlow<List<SharedItemCandidate>>(emptyList())
    val shareQueue: StateFlow<List<SharedItemCandidate>> = _shareQueue

    private val _selectedPeerFps = MutableStateFlow<Set<String>>(emptySet())
    val selectedPeerFps: StateFlow<Set<String>> = _selectedPeerFps

    private val _activityLog = MutableStateFlow<List<TransferActivityEntry>>(emptyList())
    val activityLog: StateFlow<List<TransferActivityEntry>> = _activityLog

    private val _isSending = MutableStateFlow(false)
    val isSending: StateFlow<Boolean> = _isSending

    private var activeSendJob: Job? = null

    @Volatile
    var pendingClipboardDeferred: CompletableDeferred<String?>? = null

    @Synchronized
    fun ensureInitialized(context: Context) {
        if (initialized) return
        appContext = context.applicationContext
        val dir = File(appContext.filesDir, "gestoclip").apply { mkdirs() }

        settingsFile = File(dir, "settings.json")
        if (settingsFile.exists()) {
            try {
                _settings.value = ProtocolJson.deserialize<AndroidUserSettings>(settingsFile.readText())
            } catch (_: Exception) {}
        }

        val idFile = File(dir, "device.id")
        deviceId = if (idFile.exists()) {
            idFile.readText().trim()
        } else {
            UUID.randomUUID().toString().replace("-", "").take(12).also { idFile.writeText(it) }
        }
        deviceName = "${Build.MANUFACTURER} ${Build.MODEL}".trim()

        identity = DeviceCert.loadOrCreate(File(dir, "device.p12"))
        pairedStore = PairedStore(File(dir, "paired.json"))
        pairingService = PairingService(pairedStore, deviceId, deviceName, "ANDROID")

        channel = SecureChannel(
            identity = identity,
            peersProvider = { discovery.activePeers() },
            isPaired = { fp -> pairedStore.isPaired(fp) },
            port = SecureChannel.DEFAULT_PORT,
            pairing = pairingService
        )

        val beacon = Beacon(
            v = 1,
            deviceId = deviceId,
            name = deviceName,
            platform = "ANDROID",
            port = SecureChannel.DEFAULT_PORT,
            pubKeyFp = identity.fingerprint,
            sentAt = System.currentTimeMillis()
        )
        discovery = UdpDiscovery(appContext, beacon)

        channel.onReceived = { payload, remoteFp ->
            _pendingInbox.value = payload
            val peerName = pairedStore.list().firstOrNull { it.fingerprint.equals(remoteFp, ignoreCase = true) }?.name
                ?: remoteFp.take(8)
            val kb = (payload.data.size / 1024).coerceAtLeast(1)

            if (payload.kind != Kinds.TEXT && _settings.value.autoSaveReceivedFiles) {
                try {
                    val savedUri = MediaStoreSaver.save(appContext, payload)
                    val destFolder = if (payload.kind == Kinds.IMAGE || payload.kind == Kinds.SCREENSHOT) "Imágenes/GestoClip" else "Descargas/GestoClip"
                    recordActivity(
                        TransferActivityEntry(
                            id = UUID.randomUUID().toString().take(8),
                            fileName = payload.name,
                            kind = payload.kind,
                            sizeBytes = payload.data.size.toLong(),
                            isSent = false,
                            peerName = peerName,
                            status = "COMPLETED",
                            savedUriString = savedUri?.toString()
                        )
                    )
                    setStatus("📥 Recibido y guardado en $destFolder: ${payload.name} ($kb KB)")
                    toast("Guardado en $destFolder: ${payload.name}")
                } catch (e: Exception) {
                    recordActivity(
                        TransferActivityEntry(
                            id = UUID.randomUUID().toString().take(8),
                            fileName = payload.name,
                            kind = payload.kind,
                            sizeBytes = payload.data.size.toLong(),
                            isSent = false,
                            peerName = peerName,
                            status = "FAILED",
                            errorMessage = e.message
                        )
                    )
                    setStatus("❌ Error al guardar ${payload.name}: ${e.message}")
                }
            } else {
                recordActivity(
                    TransferActivityEntry(
                        id = UUID.randomUUID().toString().take(8),
                        fileName = payload.name,
                        kind = payload.kind,
                        sizeBytes = payload.data.size.toLong(),
                        isSent = false,
                        peerName = peerName,
                        status = "COMPLETED"
                    )
                )
                setStatus("📥 Recibido de $peerName: ${payload.name} ($kb KB). Abre la palma (🖐️) para pegar.")
                toast("Recibido: ${payload.name}. Abre la palma para pegar.")
            }
        }

        channel.onRejected = { fp ->
            setStatus("⚠️ Conexión rechazada (par no emparejado: ${fp.take(8)}…)")
        }

        pairingService.onDevicePaired = { peer ->
            refreshLists()
            setStatus("🔒 Dispositivo emparejado: ${peer.name}")
            toast("Emparejado con ${peer.name}")
        }

        discovery.onPeerSeen = { peer ->
            pairedStore.reconcileDiscoveredPeer(peer.pubKeyFp, peer.deviceId, peer.name, peer.platform)
            _discoveredPeers.value = discovery.activePeers()
            _pairedPeers.value = pairedStore.list()
        }

        channel.startListening()
        discovery.start()
        refreshLists()
        initialized = true
    }

    fun updateSettings(newSettings: AndroidUserSettings) {
        _settings.value = newSettings
        try {
            settingsFile.writeText(ProtocolJson.serialize(newSettings))
        } catch (_: Exception) {}
    }

    fun togglePeerSelection(fp: String) {
        val norm = fp.lowercase()
        val cur = _selectedPeerFps.value.toMutableSet()
        if (!cur.add(norm)) cur.remove(norm)
        _selectedPeerFps.value = cur
    }

    fun addUrisToShareQueue(uris: List<Uri>) {
        val current = _shareQueue.value.toMutableList()
        var added = 0
        for (uri in uris) {
            val cand = MediaStoreSaver.inspectUri(appContext, uri) ?: continue
            if (current.none { it.uriString == cand.uriString }) {
                current.add(cand)
                added++
            }
        }
        _shareQueue.value = current
        if (added > 0) {
            setStatus("📎 $added archivo(s) listos para enviar en el Centro para Compartir.")
        }
    }

    fun clearShareQueue() {
        _shareQueue.value = emptyList()
    }

    fun sendShareQueueNow() {
        val items = _shareQueue.value
        if (items.isEmpty()) {
            setStatus("ℹ️ Primero selecciona al menos una foto o archivo para compartir.")
            return
        }
        val targets = _selectedPeerFps.value.takeIf { it.isNotEmpty() }

        activeSendJob?.cancel()
        activeSendJob = scope.launch {
            _isSending.value = true
            try {
                for ((idx, item) in items.withIndex()) {
                    ensureActive()
                    val actId = UUID.randomUUID().toString().take(8)
                    setStatus("⏳ Enviando (${idx + 1}/${items.size}): ${item.name} (${item.sizeFormatted})...")
                    recordActivity(
                        TransferActivityEntry(
                            id = actId,
                            fileName = item.name,
                            kind = item.kind,
                            sizeBytes = item.sizeBytes,
                            isSent = true,
                            peerName = "Equipos seleccionados",
                            status = "IN_PROGRESS",
                            savedUriString = item.uriString
                        )
                    )
                    try {
                        val payload = MediaStoreSaver.readCandidateToPayload(appContext, item)
                        val sentCount = channel.sendToPeers(payload, targets)
                        if (sentCount == 0) {
                            throw IOException("Ningún equipo emparejado seleccionado respondió en la red Wi-Fi.")
                        }
                        recordActivity(
                            TransferActivityEntry(
                                id = actId,
                                fileName = item.name,
                                kind = item.kind,
                                sizeBytes = payload.data.size.toLong(),
                                isSent = true,
                                peerName = "$sentCount equipo(s)",
                                status = "COMPLETED",
                                savedUriString = item.uriString
                            )
                        )
                    } catch (ce: CancellationException) {
                        recordActivity(
                            TransferActivityEntry(
                                id = actId,
                                fileName = item.name,
                                kind = item.kind,
                                sizeBytes = item.sizeBytes,
                                isSent = true,
                                peerName = "Cancelado",
                                status = "CANCELLED",
                                errorMessage = "Cancelado por el usuario"
                            )
                        )
                        throw ce
                    } catch (e: Exception) {
                        recordActivity(
                            TransferActivityEntry(
                                id = actId,
                                fileName = item.name,
                                kind = item.kind,
                                sizeBytes = item.sizeBytes,
                                isSent = true,
                                peerName = "Error",
                                status = "FAILED",
                                errorMessage = e.message
                            )
                        )
                        throw e
                    }
                }
                _shareQueue.value = emptyList()
                setStatus("✅ Todos los archivos (${items.size}) se enviaron correctamente.")
                toast("Envío completado (${items.size} archivos)")
            } catch (_: CancellationException) {
                setStatus("🚫 Envío cancelado.")
            } catch (e: Exception) {
                setStatus("❌ Error en envío: ${e.message}")
                toast("Error: ${e.message}")
            } finally {
                _isSending.value = false
            }
        }
    }

    fun cancelActiveSend() {
        activeSendJob?.cancel()
    }

    fun recordActivity(entry: TransferActivityEntry) {
        val current = _activityLog.value.toMutableList()
        val idx = current.indexOfFirst { it.id == entry.id }
        if (idx >= 0) current[idx] = entry else current.add(0, entry)
        if (current.size > 80) current.removeAt(current.lastIndex)
        _activityLog.value = current
    }

    fun refreshLists() {
        if (!initialized) return
        _pairedPeers.value = pairedStore.list()
        _discoveredPeers.value = discovery.activePeers()
    }

    suspend fun pairWithQrUri(rawUri: String): PairAckMessage {
        val invite = QrPairInvite.tryParse(rawUri)
            ?: throw IllegalArgumentException("El código QR no es un enlace gestoclip://pair válido")
        val ack = channel.pairWithServer(invite, deviceId, deviceName, "ANDROID", pairedStore)
        if (ack.accepted) {
            refreshLists()
            setStatus("✅ Emparejado con ${ack.serverName} (${invite.ip}:${invite.port})")
        }
        return ack
    }

    /**
     * Ejecuta la acción del puño cerrado (✊) según el modo configurado por el usuario:
     * 1) Si hay archivos en la cola o el modo es QUEUED_SHARE_ITEMS, envía la cola.
     * 2) Si el modo es PRESELECTED_FOLDERS, toma los archivos más recientes de las carpetas SAF configuradas.
     * 3) Por defecto, captura texto del portapapeles o toma captura de pantalla.
     */
    fun executeCopyOrCapture() {
        scope.launch {
            try {
                val cfg = _settings.value
                if ((cfg.fistMode == AndroidGestureMode.QUEUED_SHARE_ITEMS || _shareQueue.value.isNotEmpty()) && _shareQueue.value.isNotEmpty()) {
                    sendShareQueueNow()
                    return@launch
                }

                if (cfg.fistMode == AndroidGestureMode.PRESELECTED_FOLDERS && cfg.preselectedFolderUris.isNotEmpty()) {
                    val folderItems = mutableListOf<SharedItemCandidate>()
                    for (treeStr in cfg.preselectedFolderUris) {
                        val docDir = DocumentFile.fromTreeUri(appContext, Uri.parse(treeStr)) ?: continue
                        val recent = docDir.listFiles()
                            .filter { it.isFile }
                            .sortedByDescending { it.lastModified() }
                            .take(3)
                        for (f in recent) {
                            MediaStoreSaver.inspectUri(appContext, f.uri)?.let { folderItems.add(it) }
                        }
                    }
                    if (folderItems.isNotEmpty()) {
                        _shareQueue.value = folderItems
                        sendShareQueueNow()
                        return@launch
                    }
                }

                val acc = GestoAccessibilityService.instance
                val payload: Payload = if (acc != null) {
                    setStatus("✊ Capturando selección o pantalla...")
                    acc.captureSelectionOrScreenshot()
                } else {
                    val text = readForegroundClipboardDirect()
                    if (!text.isNullOrBlank()) {
                        Payload(Kinds.TEXT, "clip.txt", "text/plain", text.toByteArray(Charsets.UTF_8))
                    } else {
                        setStatus("⚠️ Activa el Servicio de Accesibilidad de GestoClip para capturar pantalla desde cualquier app.")
                        toast("Activa Accesibilidad de GestoClip para capturar fuera de la app.")
                        return@launch
                    }
                }

                val targets = _selectedPeerFps.value.takeIf { it.isNotEmpty() }
                val sent = channel.sendToPeers(payload, targets)
                val kb = (payload.data.size / 1024).coerceAtLeast(1)
                if (sent > 0) {
                    recordActivity(
                        TransferActivityEntry(
                            id = UUID.randomUUID().toString().take(8),
                            fileName = payload.name,
                            kind = payload.kind,
                            sizeBytes = payload.data.size.toLong(),
                            isSent = true,
                            peerName = "$sent equipo(s)",
                            status = "COMPLETED"
                        )
                    )
                    setStatus("📤 Enviado: ${payload.name} (${payload.kind}, $kb KB) a $sent dispositivo(s)")
                    toast("Enviado: ${payload.name}")
                } else {
                    setStatus("⚠️ Capturado (${payload.name}), pero no se encontró ningún equipo emparejado activo en LAN.")
                    toast("Sin par emparejado activo en la red")
                }
            } catch (e: Exception) {
                setStatus("❌ Error al copiar/enviar: ${e.message}")
                toast("Error: ${e.message}")
            }
        }
    }

    fun executePaste() {
        val p = _pendingInbox.value
        if (p == null) {
            setStatus("ℹ️ No hay ningún contenido recibido pendiente por pegar.")
            toast("No hay nada pendiente por pegar")
            return
        }
        _pendingInbox.value = null

        if (p.kind == Kinds.TEXT) {
            val text = String(p.data, Charsets.UTF_8)
            mainHandler.post {
                val cm = appContext.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                cm.setPrimaryClip(ClipData.newPlainText("GestoClip", text))
                val pasted = GestoAccessibilityService.instance?.pasteIntoFocusedNode() ?: false
                if (pasted) {
                    setStatus("📋 Texto pegado automáticamente en el campo activo.")
                    toast("Texto pegado")
                } else {
                    setStatus("📋 Texto copiado al portapapeles de Android (listo para pegar).")
                    toast("Copiado al portapapeles de Android")
                }
            }
        } else {
            scope.launch {
                val uri = MediaStoreSaver.save(appContext, p)
                if (uri != null) {
                    setStatus("🖼️ Guardado en Galería/Descargas: ${p.name}")
                    toast("Guardado: ${p.name}")
                } else {
                    setStatus("❌ No se pudo guardar ${p.name}")
                }
            }
        }
    }

    suspend fun awaitClipboardViaTransparentActivity(): String? {
        val deferred = CompletableDeferred<String?>()
        pendingClipboardDeferred = deferred
        ClipboardReaderActivity.launch(appContext)
        return withTimeoutOrNull(1200L) { deferred.await() }.also {
            pendingClipboardDeferred = null
        }
    }

    private fun readForegroundClipboardDirect(): String? {
        return try {
            val cm = appContext.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            cm.primaryClip?.getItemAt(0)?.coerceToText(appContext)?.toString()
        } catch (_: Exception) {
            null
        }
    }

    fun setStatus(msg: String) {
        _statusMessage.value = msg
    }

    fun toast(msg: String) {
        mainHandler.post {
            Toast.makeText(appContext, msg, Toast.LENGTH_SHORT).show()
        }
    }
}
