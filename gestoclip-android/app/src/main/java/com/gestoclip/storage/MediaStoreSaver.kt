package com.gestoclip.storage

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import com.gestoclip.protocol.Kinds
import com.gestoclip.protocol.Payload

object MediaStoreSaver {
    fun save(context: Context, payload: Payload): Uri? {
        val resolver = context.contentResolver
        return if (payload.kind == Kinds.IMAGE || payload.kind == Kinds.SCREENSHOT) {
            val values = ContentValues().apply {
                put(MediaStore.Images.Media.DISPLAY_NAME, payload.name.ifBlank { "gestoclip_${System.currentTimeMillis()}.png" })
                put(MediaStore.Images.Media.MIME_TYPE, payload.mime.ifBlank { "image/png" })
                put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/GestoClip")
                put(MediaStore.Images.Media.IS_PENDING, 1)
            }
            val uri = resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values) ?: return null
            resolver.openOutputStream(uri)?.use { it.write(payload.data) }
            values.clear()
            values.put(MediaStore.Images.Media.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
            uri
        } else {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, payload.name.ifBlank { "archivo_${System.currentTimeMillis()}" })
                put(MediaStore.Downloads.MIME_TYPE, payload.mime.ifBlank { "application/octet-stream" })
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/GestoClip")
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
            resolver.openOutputStream(uri)?.use { it.write(payload.data) }
            values.clear()
            values.put(MediaStore.Downloads.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
            uri
        }
    }
}
