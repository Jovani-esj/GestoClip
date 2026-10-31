package com.gestoclip.net

import com.gestoclip.protocol.Framing
import com.gestoclip.protocol.Kinds
import com.gestoclip.protocol.PairAckMessage
import com.gestoclip.protocol.PairRequestMessage
import com.gestoclip.protocol.Payload
import com.gestoclip.protocol.ProtocolJson
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.jce.provider.BouncyCastleProvider
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.IOException
import java.math.BigInteger
import java.net.InetSocketAddress
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.Security
import java.security.cert.Certificate
import java.security.cert.X509Certificate
import java.util.Date
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

data class DeviceIdentity(
    val keyStore: KeyStore,
    val certificate: X509Certificate,
    val fingerprint: String
)

object DeviceCert {
    private const val ALIAS = "gestoclip"
    private val PASSWORD = "gestoclip".toCharArray()

    init {
        if (Security.getProvider(BouncyCastleProvider.PROVIDER_NAME) == null) {
            Security.addProvider(BouncyCastleProvider())
        }
    }

    fun fingerprint(cert: Certificate): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(cert.encoded)
        return digest.joinToString("") { "%02x".format(it) }
    }

    fun loadOrCreate(file: File): DeviceIdentity {
        val ks = KeyStore.getInstance("PKCS12")
        if (file.exists()) {
            try {
                FileInputStream(file).use { ks.load(it, PASSWORD) }
                val cert = ks.getCertificate(ALIAS) as? X509Certificate
                if (cert != null) {
                    return DeviceIdentity(ks, cert, fingerprint(cert))
                }
            } catch (_: Exception) {}
        }

        val kpg = KeyPairGenerator.getInstance("RSA")
        kpg.initialize(2048, SecureRandom())
        val kp = kpg.generateKeyPair()

        val now = System.currentTimeMillis()
        val notBefore = Date(now - 86_400_000L)
        val notAfter = Date(now + 10L * 365 * 86_400_000L)
        val subject = X500Name("CN=GestoClip")
        val builder = JcaX509v3CertificateBuilder(
            subject,
            BigInteger.valueOf(now),
            notBefore,
            notAfter,
            subject,
            kp.public
        )
        val signer = JcaContentSignerBuilder("SHA256WithRSA").build(kp.private)
        val holder = builder.build(signer)
        val cert = JcaX509CertificateConverter().getCertificate(holder)

        ks.load(null, PASSWORD)
        ks.setKeyEntry(ALIAS, kp.private, PASSWORD, arrayOf(cert))
        file.parentFile?.mkdirs()
        FileOutputStream(file).use { ks.store(it, PASSWORD) }
        return DeviceIdentity(ks, cert, fingerprint(cert))
    }

    fun keyManagers(identity: DeviceIdentity) =
        KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply {
            init(identity.keyStore, PASSWORD)
        }.keyManagers
}

class SecureChannel(
    private val identity: DeviceIdentity,
    private val peersProvider: () -> List<DiscoveredPeer>,
    private val isPaired: (String) -> Boolean,
    port: Int = DEFAULT_PORT,
    private val pairing: PairingService? = null
) {
    companion object {
        const val DEFAULT_PORT = 8765
        private const val MAX_UNPAIRED_BYTES = 4096L
        private const val DELIVERY_ACK_BYTE = 0x06
    }

    var listenPort: Int = port
        private set

    val localFingerprint: String
        get() = identity.fingerprint

    var onReceived: ((Payload, String) -> Unit)? = null
    var onRejected: ((String) -> Unit)? = null

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var acceptJob: Job? = null
    private var serverSocket: SSLServerSocket? = null

    private fun buildSslContext(expectedPeerFp: String? = null, capturedPeerFp: ((String) -> Unit)? = null): SSLContext {
        val tm = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                if (chain.isNullOrEmpty()) throw SecurityException("Sin certificado de cliente")
                val fp = DeviceCert.fingerprint(chain[0])
                capturedPeerFp?.invoke(fp)
                if (expectedPeerFp != null && !fp.equals(expectedPeerFp, ignoreCase = true)) {
                    throw SecurityException("Fingerprint no coincide")
                }
            }

            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                if (chain.isNullOrEmpty()) throw SecurityException("Sin certificado de servidor")
                val fp = DeviceCert.fingerprint(chain[0])
                capturedPeerFp?.invoke(fp)
                if (expectedPeerFp != null && !fp.equals(expectedPeerFp, ignoreCase = true)) {
                    throw SecurityException("Fingerprint del servidor no coincide")
                }
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
        return SSLContext.getInstance("TLS").apply {
            init(DeviceCert.keyManagers(identity), arrayOf(tm), SecureRandom())
        }
    }

    fun startListening() {
        stopListening()
        val ctx = buildSslContext()
        val srv = (ctx.serverSocketFactory.createServerSocket(listenPort) as SSLServerSocket).apply {
            needClientAuth = true
        }
        listenPort = srv.localPort
        serverSocket = srv
        acceptJob = scope.launch {
            while (isActive && !srv.isClosed) {
                try {
                    val client = srv.accept() as SSLSocket
                    launch { handleClient(client) }
                } catch (_: Exception) {
                    if (srv.isClosed) break
                }
            }
        }
    }

    private fun handleClient(socket: SSLSocket) {
        socket.use { s ->
            try {
                s.soTimeout = 10_000
                s.startHandshake()
                val peerCerts = s.session.peerCertificates
                if (peerCerts.isEmpty()) return
                val remoteFp = DeviceCert.fingerprint(peerCerts[0])

                val input = s.inputStream
                val output = s.outputStream

                if (!isPaired(remoteFp)) {
                    val active = pairing?.getActiveInvite()
                    if (pairing == null || active == null) {
                        onRejected?.invoke(remoteFp)
                        return
                    }
                    val pairPayload = try {
                        Framing.read(input, MAX_UNPAIRED_BYTES)
                    } catch (_: Exception) {
                        onRejected?.invoke(remoteFp)
                        return
                    }
                    if (pairPayload.kind != Kinds.PAIR_REQUEST) {
                        onRejected?.invoke(remoteFp)
                        return
                    }
                    val req = try {
                        ProtocolJson.deserialize<PairRequestMessage>(String(pairPayload.data, Charsets.UTF_8))
                    } catch (_: Exception) {
                        onRejected?.invoke(remoteFp)
                        return
                    }
                    val (ack, _) = pairing.tryAcceptPairRequest(remoteFp, req)
                    val ackBytes = ProtocolJson.serialize(ack).toByteArray(Charsets.UTF_8)
                    Framing.write(output, Payload(Kinds.PAIR_ACK, "pair.json", "application/json", ackBytes))
                    if (!ack.accepted) onRejected?.invoke(remoteFp)
                    return
                }

                val payload = Framing.read(input)
                if (payload.kind == Kinds.PAIR_REQUEST && pairing != null) {
                    val req = ProtocolJson.deserialize<PairRequestMessage>(String(payload.data, Charsets.UTF_8))
                    val (ack, _) = pairing.tryAcceptPairRequest(remoteFp, req)
                    val ackBytes = ProtocolJson.serialize(ack).toByteArray(Charsets.UTF_8)
                    Framing.write(output, Payload(Kinds.PAIR_ACK, "pair.json", "application/json", ackBytes))
                    return
                }

                onReceived?.invoke(payload, remoteFp)
                output.write(intArrayOf(DELIVERY_ACK_BYTE)[0])
                output.flush()
            } catch (_: Exception) {}
        }
    }

    suspend fun pairWithServer(
        invite: QrPairInvite,
        clientDeviceId: String,
        clientName: String,
        clientPlatform: String = "ANDROID",
        clientStore: PairedStore? = null
    ): PairAckMessage = withContext(Dispatchers.IO) {
        val ctx = buildSslContext(expectedPeerFp = invite.fp)
        (ctx.socketFactory.createSocket() as SSLSocket).use { sock ->
            sock.connect(InetSocketAddress(invite.ip, invite.port), 6000)
            sock.soTimeout = 8000
            sock.startHandshake()

            val req = PairRequestMessage(invite.token, clientDeviceId, clientName, clientPlatform)
            val reqBytes = ProtocolJson.serialize(req).toByteArray(Charsets.UTF_8)
            Framing.write(sock.outputStream, Payload(Kinds.PAIR_REQUEST, "pair.json", "application/json", reqBytes))

            val resp = Framing.read(sock.inputStream, MAX_UNPAIRED_BYTES)
            if (resp.kind != Kinds.PAIR_ACK) {
                throw IOException("Respuesta inesperada del servidor: ${resp.kind}")
            }
            val ack = ProtocolJson.deserialize<PairAckMessage>(String(resp.data, Charsets.UTF_8))
            if (ack.accepted && clientStore != null) {
                clientStore.addOrUpdate(invite.fp, ack.serverDeviceId, ack.serverName, ack.serverPlatform)
            }
            ack
        }
    }

    suspend fun sendToPeers(payload: Payload): Int = withContext(Dispatchers.IO) {
        var sent = 0
        for (peer in peersProvider().filter { isPaired(it.pubKeyFp) }) {
            try {
                sendSingle(peer, payload)
                sent++
            } catch (_: Exception) {}
        }
        sent
    }

    private fun sendSingle(peer: DiscoveredPeer, payload: Payload) {
        val ctx = buildSslContext(expectedPeerFp = peer.pubKeyFp)
        (ctx.socketFactory.createSocket() as SSLSocket).use { sock ->
            sock.connect(InetSocketAddress(peer.ipAddress, peer.port), 5000)
            sock.soTimeout = 10_000
            sock.startHandshake()
            Framing.write(sock.outputStream, payload)
            val ack = sock.inputStream.read()
            if (ack != DELIVERY_ACK_BYTE) {
                throw IOException("El par no confirmó la recepción del paquete")
            }
        }
    }

    fun stopListening() {
        try {
            acceptJob?.cancel()
            serverSocket?.close()
        } catch (_: Exception) {}
    }

    fun close() {
        stopListening()
        scope.cancel()
    }
}
