package com.gestoclip

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.widget.Toast
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
import com.gestoclip.storage.MediaStoreSaver
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.io.File
import java.util.UUID

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

    private val _statusMessage = MutableStateFlow("Listo para enviar o recibir gestos.")
    val statusMessage: StateFlow<String> = _statusMessage

    private val _pairedPeers = MutableStateFlow<List<PairedPeer>>(emptyList())
    val pairedPeers: StateFlow<List<PairedPeer>> = _pairedPeers

    private val _discoveredPeers = MutableStateFlow<List<DiscoveredPeer>>(emptyList())
    val discoveredPeers: StateFlow<List<DiscoveredPeer>> = _discoveredPeers

    private val _pendingInbox = MutableStateFlow<Payload?>(null)
    val pendingInbox: StateFlow<Payload?> = _pendingInbox

    @Volatile
    var pendingClipboardDeferred: CompletableDeferred<String?>? = null

    @Synchronized
    fun ensureInitialized(context: Context) {
        if (initialized) return
        appContext = context.applicationContext
        val dir = File(appContext.filesDir, "gestoclip").apply { mkdirs() }

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

        channel.onReceived = { payload, _ ->
            _pendingInbox.value = payload
            val kb = (payload.data.size / 1024).coerceAtLeast(1)
            setStatus("📥 Recibido: ${payload.name} (${payload.kind}, $kb KB). Abre la palma (🖐️) para pegar.")
            toast("Recibido: ${payload.name}. Abre la palma para pegar.")
        }
        channel.onRejected = { fp ->
            setStatus("⚠️ Conexión rechazada (par no emparejado: ${fp.take(8)}…)")
        }
        pairingService.onDevicePaired = { peer ->
            refreshLists()
            setStatus("🔒 Dispositivo emparejado: ${peer.name}")
            toast("Emparejado con ${peer.name}")
        }
        discovery.onPeerSeen = {
            _discoveredPeers.value = discovery.activePeers()
        }

        channel.startListening()
        discovery.start()
        refreshLists()
        initialized = true
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
            // También registramos al servidor en los pares descubiertos inmediatos para enviar sin esperar la siguiente baliza UDP
            refreshLists()
            setStatus("✅ Emparejado con ${ack.serverName} (${invite.ip}:${invite.port})")
        }
        return ack
    }

    fun executeCopyOrCapture() {
        scope.launch {
            try {
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

                val sent = channel.sendToPeers(payload)
                val kb = (payload.data.size / 1024).coerceAtLeast(1)
                if (sent > 0) {
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
