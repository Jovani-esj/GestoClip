package com.gestoclip.ui

import android.service.quicksettings.TileService
import com.gestoclip.GestoRuntime
import com.gestoclip.vision.GestureForegroundService

class QuickTileService : TileService() {
    override fun onClick() {
        super.onClick()
        GestoRuntime.ensureInitialized(applicationContext)
        GestureForegroundService.start(applicationContext)
    }
}
