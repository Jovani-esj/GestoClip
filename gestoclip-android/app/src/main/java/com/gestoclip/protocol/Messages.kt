package com.gestoclip.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest

object Kinds {
    const val TEXT = "TEXT"
    const val IMAGE = "IMAGE"
    const val FILE = "FILE"
    const val SCREENSHOT = "SCREENSHOT"
    const val PAIR_REQUEST = "PAIR_REQUEST"
    const val PAIR_ACK = "PAIR_ACK"
}

@Serializable
data class Beacon(
    val v: Int = 1,
    val deviceId: String,
    val name: String,
    val platform: String,
    val port: Int,
    val pubKeyFp: String,
    val sentAt: Long
)

@Serializable
data class ClipMessage(
    val v: Int = 1,
    val kind: String,
    val name: String,
    val mime: String,
    val size: Long,
    val sha256: String
)

@Serializable
data class PairRequestMessage(
    val token: String,
    val deviceId: String,
    val name: String,
    val platform: String
)

@Serializable
data class PairAckMessage(
    val accepted: Boolean,
    val serverDeviceId: String,
    val serverName: String,
    val serverPlatform: String,
    val reason: String = ""
)

data class Payload(
    val kind: String,
    val name: String,
    val mime: String,
    val data: ByteArray
)

object ProtocolJson {
    val instance = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    inline fun <reified T> serialize(value: T): String = instance.encodeToString(value)
    inline fun <reified T> deserialize(raw: String): T = instance.decodeFromString(raw)
}

object Framing {
    const val VERSION = 1
    const val MAX_SIZE = 512L * 1024L * 1024L

    fun sha256Hex(bytes: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(bytes)
        return digest.joinToString("") { "%02x".format(it) }
    }

    fun write(output: OutputStream, payload: Payload) {
        val header = ClipMessage(
            v = VERSION,
            kind = payload.kind,
            name = payload.name,
            mime = payload.mime,
            size = payload.data.size.toLong(),
            sha256 = sha256Hex(payload.data)
        )
        val line = (ProtocolJson.serialize(header) + "\n").toByteArray(Charsets.UTF_8)
        output.write(line)
        output.write(payload.data)
        output.flush()
    }

    fun read(input: InputStream, maxSize: Long = MAX_SIZE): Payload {
        val line = readLineUtf8(input) ?: throw IOException("Conexión cerrada")
        val header = ProtocolJson.deserialize<ClipMessage>(line)
        if (header.size < 0 || header.size > maxSize) {
            throw IOException("Tamaño fuera de rango: ${header.size}")
        }
        val buf = ByteArray(header.size.toInt())
        var offset = 0
        while (offset < buf.size) {
            val read = input.read(buf, offset, buf.size - offset)
            if (read < 0) throw EOFException("Transferencia incompleta")
            offset += read
        }
        val actualHash = sha256Hex(buf)
        if (!actualHash.equals(header.sha256, ignoreCase = true)) {
            throw IOException("SHA-256 no coincide")
        }
        return Payload(header.kind, header.name, header.mime, buf)
    }

    private fun readLineUtf8(input: InputStream): String? {
        val bos = ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b < 0) {
                return if (bos.size() == 0) null else bos.toString(Charsets.UTF_8.name())
            }
            if (b == '\n'.code) {
                return bos.toString(Charsets.UTF_8.name()).trimEnd('\r')
            }
            bos.write(b)
            if (bos.size() > 8192) throw IOException("Encabezado demasiado largo")
        }
    }
}
