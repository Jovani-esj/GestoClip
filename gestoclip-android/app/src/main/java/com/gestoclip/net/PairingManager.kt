package com.gestoclip.net

import com.gestoclip.protocol.PairAckMessage
import com.gestoclip.protocol.PairRequestMessage
import com.gestoclip.protocol.ProtocolJson
import kotlinx.serialization.Serializable
import java.io.File
import java.net.URI
import java.net.URLDecoder
import java.net.URLEncoder
import java.security.MessageDigest
import java.security.SecureRandom

@Serializable
data class PairedPeer(
    val fingerprint: String,
    val deviceId: String,
    val name: String,
    val platform: String,
    val pairedAtMs: Long = System.currentTimeMillis()
)

@Serializable
data class QrPairInvite(
    val v: Int = 1,
    val ip: String,
    val port: Int,
    val fp: String,
    val token: String,
    val deviceId: String,
    val name: String,
    val platform: String,
    val expiresAtUnixMs: Long = 0L
) {
    fun toUri(): String {
        fun enc(s: String) = URLEncoder.encode(s, "UTF-8")
        return "gestoclip://pair?v=$v&ip=${enc(ip)}&port=$port&fp=${enc(fp)}&token=${enc(token)}&id=${enc(deviceId)}&name=${enc(name)}&platform=${enc(platform)}&exp=$expiresAtUnixMs"
    }

    companion object {
        fun tryParse(rawInput: String): QrPairInvite? {
            val raw = rawInput.trim()
            if (raw.isEmpty()) return null
            if (raw.startsWith("{")) {
                return try { ProtocolJson.deserialize<QrPairInvite>(raw) } catch (_: Exception) { null }
            }
            return try {
                val uri = URI(raw)
                if (!uri.scheme.equals("gestoclip", ignoreCase = true) ||
                    !uri.host.equals("pair", ignoreCase = true)
                ) return null

                val map = mutableMapOf<String, String>()
                val query = uri.rawQuery ?: return null
                for (part in query.split("&")) {
                    if (part.isBlank()) continue
                    val kv = part.split("=", limit = 2)
                    if (kv.size == 2) {
                        val k = URLDecoder.decode(kv[0], "UTF-8").lowercase()
                        val v = URLDecoder.decode(kv[1], "UTF-8")
                        map[k] = v
                    }
                }
                val ip = map["ip"]?.takeIf { it.isNotBlank() } ?: return null
                val fp = map["fp"]?.takeIf { it.isNotBlank() }?.lowercase() ?: return null
                val token = map["token"]?.takeIf { it.isNotBlank() } ?: return null
                val v = map["v"]?.toIntOrNull() ?: 1
                val port = map["port"]?.toIntOrNull() ?: 8765
                val id = map["id"] ?: "peer"
                val name = map["name"] ?: "Dispositivo"
                val platform = map["platform"] ?: "WINDOWS"
                val exp = map["exp"]?.toLongOrNull() ?: 0L
                QrPairInvite(v, ip, port, fp, token, id, name, platform, exp)
            } catch (_: Exception) {
                null
            }
        }
    }
}

class PairedStore(private val storageFile: File) {
    private val lock = Any()
    private val peers = mutableMapOf<String, PairedPeer>()

    init {
        load()
    }

    private fun load() {
        synchronized(lock) {
            peers.clear()
            if (!storageFile.exists()) return
            try {
                val text = storageFile.readText().trim()
                if (text.startsWith("[")) {
                    val list = ProtocolJson.deserialize<List<PairedPeer>>(text)
                    for (p in list) {
                        if (p.fingerprint.isNotBlank()) {
                            peers[p.fingerprint.lowercase()] = p
                        }
                    }
                }
            } catch (_: Exception) {}
        }
    }

    private fun saveLocked() {
        storageFile.parentFile?.mkdirs()
        storageFile.writeText(ProtocolJson.serialize(peers.values.toList()))
    }

    fun isPaired(fp: String): Boolean {
        if (fp.isBlank()) return false
        synchronized(lock) {
            return peers.containsKey(fp.lowercase())
        }
    }

    fun list(): List<PairedPeer> = synchronized(lock) {
        peers.values.sortedByDescending { it.pairedAtMs }
    }

    fun addOrUpdate(fp: String, deviceId: String, name: String, platform: String): PairedPeer {
        val norm = fp.trim().lowercase()
        val peer = PairedPeer(norm, deviceId, name, platform, System.currentTimeMillis())
        synchronized(lock) {
            peers[norm] = peer
            saveLocked()
        }
        return peer
    }

    fun remove(fp: String): Boolean = synchronized(lock) {
        val removed = peers.remove(fp.trim().lowercase()) != null
        if (removed) saveLocked()
        removed
    }
}

class PairingService(
    private val store: PairedStore,
    private val localDeviceId: String,
    private val localName: String,
    private val localPlatform: String = "ANDROID"
) {
    companion object {
        const val MAX_FAILED_ATTEMPTS = 3
        const val DEFAULT_TTL_MS = 120_000L
    }

    private val lock = Any()
    private val rng = SecureRandom()
    private var activeInvite: QrPairInvite? = null
    private var failedAttempts = 0

    var onDevicePaired: ((PairedPeer) -> Unit)? = null

    fun createInvite(localIp: String, port: Int, localFingerprint: String, ttlMs: Long = DEFAULT_TTL_MS): QrPairInvite {
        val token = "%06d".format(100000 + rng.nextInt(900000))
        val exp = System.currentTimeMillis() + ttlMs
        val invite = QrPairInvite(1, localIp, port, localFingerprint.lowercase(), token, localDeviceId, localName, localPlatform, exp)
        synchronized(lock) {
            activeInvite = invite
            failedAttempts = 0
        }
        return invite
    }

    fun getActiveInvite(): QrPairInvite? = synchronized(lock) {
        val cur = activeInvite ?: return null
        if (System.currentTimeMillis() > cur.expiresAtUnixMs) {
            activeInvite = null
            return null
        }
        cur
    }

    fun tryAcceptPairRequest(remoteFp: String, req: PairRequestMessage): Pair<PairAckMessage, PairedPeer?> {
        var paired: PairedPeer? = null
        val ack: PairAckMessage
        synchronized(lock) {
            val cur = activeInvite
            val now = System.currentTimeMillis()
            if (cur == null || now > cur.expiresAtUnixMs) {
                activeInvite = null
                return PairAckMessage(false, localDeviceId, localName, localPlatform, "Invitación expirada o inexistente") to null
            }

            val expected = cur.token.toByteArray(Charsets.UTF_8)
            val actual = req.token.toByteArray(Charsets.UTF_8)
            val match = MessageDigest.isEqual(expected, actual)

            if (!match) {
                failedAttempts++
                if (failedAttempts >= MAX_FAILED_ATTEMPTS) {
                    activeInvite = null
                }
                return PairAckMessage(false, localDeviceId, localName, localPlatform, "Token inválido") to null
            }

            activeInvite = null
            failedAttempts = 0
            paired = store.addOrUpdate(
                remoteFp,
                req.deviceId.ifBlank { remoteFp.take(8) },
                req.name.ifBlank { "Dispositivo" },
                req.platform.ifBlank { "WINDOWS" }
            )
            ack = PairAckMessage(true, localDeviceId, localName, localPlatform)
        }
        paired?.let { onDevicePaired?.invoke(it) }
        return ack to paired
    }
}
