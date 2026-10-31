package com.gestoclip.storage

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import com.gestoclip.protocol.Framing
import com.gestoclip.protocol.Kinds
import com.gestoclip.protocol.Payload
import java.io.File
import java.io.FileOutputStream
import java.io.IOException

data class SharedItemCandidate(
    val uriString: String,
    val name: String,
    val sizeBytes: Long,
    val mime: String,
    val kind: String
) {
    val sizeFormatted: String
        get() = if (sizeBytes >= 1024 * 1024) {
            "%.2f MB".format(sizeBytes / (1024.0 * 1024.0))
        } else {
            "${(sizeBytes / 1024).coerceAtLeast(1)} KB"
        }
}

object MediaStoreSaver {
    /**
     * Inspecciona una URI de contenido de Android (Photo Picker, SAF o Intent.ACTION_SEND)
     * sin pedir permisos globales de galería.
     */
    fun inspectUri(context: Context, uri: Uri): SharedItemCandidate? {
        return try {
            val resolver = context.contentResolver
            val mime = resolver.getType(uri) ?: "application/octet-stream"
            var name = "archivo_${System.currentTimeMillis()}"
            var size = 0L

            resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                if (c.moveToFirst()) {
                    val nameIdx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    val sizeIdx = c.getColumnIndex(OpenableColumns.SIZE)
                    if (nameIdx >= 0 && !c.isNull(nameIdx)) name = c.getString(nameIdx)
                    if (sizeIdx >= 0 && !c.isNull(sizeIdx)) size = c.getLong(sizeIdx)
                }
            }

            if (size <= 0L) {
                resolver.openAssetFileDescriptor(uri, "r")?.use { afd ->
                    if (afd.length > 0) size = afd.length
                }
            }

            val isImg = mime.startsWith("image/", ignoreCase = true) ||
                name.lowercase().let { it.endsWith(".png") || it.endsWith(".jpg") || it.endsWith(".jpeg") || it.endsWith(".webp") || it.endsWith(".gif") }

            SharedItemCandidate(
                uriString = uri.toString(),
                name = name,
                sizeBytes = size,
                mime = mime,
                kind = if (isImg) Kinds.IMAGE else Kinds.FILE
            )
        } catch (_: Exception) {
            null
        }
    }

    /**
     * Carga el contenido de una URI en un Payload verificando el límite de 512 MiB.
     */
    fun readCandidateToPayload(context: Context, candidate: SharedItemCandidate): Payload {
        if (candidate.sizeBytes > Framing.MAX_SIZE) {
            throw IOException("El archivo '${candidate.name}' (${candidate.sizeFormatted}) supera el límite máximo de 512 MiB.")
        }
        val uri = Uri.parse(candidate.uriString)
        val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() }
            ?: throw IOException("No se pudo abrir el archivo '${candidate.name}'.")
        if (bytes.size.toLong() > Framing.MAX_SIZE) {
            throw IOException("El archivo '${candidate.name}' supera el límite máximo de 512 MiB.")
        }
        return Payload(candidate.kind, candidate.name, candidate.mime, bytes)
    }

    /**
     * Guardado atómico en Imágenes/GestoClip o Descargas/GestoClip usando archivo provisional en caché
     * e indicador IS_PENDING de MediaStore (no deja archivos corruptos ni sobrescribe nombres iguales).
     */
    fun save(context: Context, payload: Payload): Uri? {
        // 1. Escribir primero en un archivo provisional y verificar integridad de bytes
        val tempFile = File(context.cacheDir, "recv_${System.currentTimeMillis()}_${java.util.UUID.randomUUID()}.part")
        try {
            FileOutputStream(tempFile).use { out ->
                out.write(payload.data)
                out.fd.sync()
            }
            if (!tempFile.exists() || tempFile.length() != payload.data.size.toLong()) {
                throw IOException("Verificación de integridad fallida al escribir archivo temporal.")
            }

            // 2. Mover a MediaStore (MediaStore resuelve automáticamente nombres repetidos agregando ' (1)')
            val resolver = context.contentResolver
            val isImage = payload.kind == Kinds.IMAGE || payload.kind == Kinds.SCREENSHOT ||
                payload.mime.startsWith("image/", ignoreCase = true)

            if (isImage) {
                val values = ContentValues().apply {
                    put(MediaStore.Images.Media.DISPLAY_NAME, payload.name.ifBlank { "gestoclip_${System.currentTimeMillis()}.png" })
                    put(MediaStore.Images.Media.MIME_TYPE, payload.mime.ifBlank { "image/png" })
                    put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/GestoClip")
                    put(MediaStore.Images.Media.IS_PENDING, 1)
                }
                val uri = resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values) ?: return null
                try {
                    resolver.openOutputStream(uri)?.use { out ->
                        tempFile.inputStream().use { inp -> inp.copyTo(out) }
                    } ?: throw IOException("No se pudo abrir el flujo de destino en Imágenes/GestoClip")
                    values.clear()
                    values.put(MediaStore.Images.Media.IS_PENDING, 0)
                    resolver.update(uri, values, null, null)
                    return uri
                } catch (e: Exception) {
                    try { resolver.delete(uri, null, null) } catch (_: Exception) {}
                    throw e
                }
            } else {
                val values = ContentValues().apply {
                    put(MediaStore.Downloads.DISPLAY_NAME, payload.name.ifBlank { "archivo_${System.currentTimeMillis()}" })
                    put(MediaStore.Downloads.MIME_TYPE, payload.mime.ifBlank { "application/octet-stream" })
                    put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/GestoClip")
                    put(MediaStore.Downloads.IS_PENDING, 1)
                }
                val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
                try {
                    resolver.openOutputStream(uri)?.use { out ->
                        tempFile.inputStream().use { inp -> inp.copyTo(out) }
                    } ?: throw IOException("No se pudo abrir el flujo de destino en Descargas/GestoClip")
                    values.clear()
                    values.put(MediaStore.Downloads.IS_PENDING, 0)
                    resolver.update(uri, values, null, null)
                    return uri
                } catch (e: Exception) {
                    try { resolver.delete(uri, null, null) } catch (_: Exception) {}
                    throw e
                }
            }
        } finally {
            try { if (tempFile.exists()) tempFile.delete() } catch (_: Exception) {}
        }
    }
}
