package com.gestoclip.net

import android.content.Context
import android.net.wifi.WifiManager
import com.gestoclip.protocol.Beacon
import com.gestoclip.protocol.ProtocolJson
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface

data class DiscoveredPeer(
    val deviceId: String,
    val name: String,
    val platform: String,
    val ipAddress: String,
    val port: Int,
    val pubKeyFp: String,
    val lastSeenAtMs: Long
)

class UdpDiscovery(
    private val context: Context?,
    private val localBeacon: Beacon,
    private val broadcastPort: Int = 8766
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var listenJob: Job? = null
    private var broadcastJob: Job? = null
    private var listenSocket: DatagramSocket? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    private val lock = Any()
    private val peers = mutableMapOf<String, DiscoveredPeer>()

    var onPeerSeen: ((DiscoveredPeer) -> Unit)? = null

    fun activePeers(): List<DiscoveredPeer> = synchronized(lock) {
        val now = System.currentTimeMillis()
        peers.values.filter { now - it.lastSeenAtMs < 12_000L }.sortedByDescending { it.lastSeenAtMs }
    }

    fun start() {
        stop()
        try {
            val wifi = context?.applicationContext?.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            multicastLock = wifi?.createMulticastLock("GestoClipMulticastLock")?.apply {
                setReferenceCounted(true)
                acquire()
            }
        } catch (_: Exception) {}

        listenJob = scope.launch {
            try {
                val socket = DatagramSocket(null).apply {
                    reuseAddress = true
                    bind(InetSocketAddress(broadcastPort))
                }
                listenSocket = socket
                val buf = ByteArray(2048)
                while (isActive && !socket.isClosed) {
                    try {
                        val packet = DatagramPacket(buf, buf.size)
                        socket.receive(packet)
                        val json = String(packet.data, 0, packet.length, Charsets.UTF_8)
                        val beacon = ProtocolJson.deserialize<Beacon>(json)
                        if (beacon.deviceId != localBeacon.deviceId) {
                            val senderIp = packet.address.hostAddress ?: "127.0.0.1"
                            val peer = DiscoveredPeer(
                                deviceId = beacon.deviceId,
                                name = beacon.name,
                                platform = beacon.platform,
                                ipAddress = senderIp,
                                port = beacon.port,
                                pubKeyFp = beacon.pubKeyFp.lowercase(),
                                lastSeenAtMs = System.currentTimeMillis()
                            )
                            synchronized(lock) {
                                peers[beacon.deviceId] = peer
                            }
                            onPeerSeen?.invoke(peer)
                        }
                    } catch (_: Exception) {}
                }
            } catch (_: Exception) {}
        }

        broadcastJob = scope.launch {
            while (isActive) {
                try {
                    val data = ProtocolJson.serialize(
                        localBeacon.copy(sentAt = System.currentTimeMillis())
                    ).toByteArray(Charsets.UTF_8)

                    DatagramSocket().use { bcast ->
                        bcast.broadcast = true
                        try {
                            bcast.send(DatagramPacket(data, data.size, InetAddress.getByName("255.255.255.255"), broadcastPort))
                        } catch (_: Exception) {}

                        try {
                            val ifaces = NetworkInterface.getNetworkInterfaces()
                            while (ifaces.hasMoreElements()) {
                                val iface = ifaces.nextElement()
                                if (iface.isLoopback || !iface.isUp) continue
                                for (ia in iface.interfaceAddresses) {
                                    val b = ia.broadcast ?: continue
                                    try {
                                        bcast.send(DatagramPacket(data, data.size, b, broadcastPort))
                                    } catch (_: Exception) {}
                                }
                            }
                        } catch (_: Exception) {}
                    }
                } catch (_: Exception) {}
                delay(2000L)
            }
        }
    }

    fun stop() {
        try {
            listenJob?.cancel()
            broadcastJob?.cancel()
            listenSocket?.close()
            if (multicastLock?.isHeld == true) {
                multicastLock?.release()
            }
        } catch (_: Exception) {}
    }

    fun close() {
        stop()
        scope.cancel()
    }

    companion object {
        fun getLocalIpAddress(): String {
            try {
                val ifaces = NetworkInterface.getNetworkInterfaces()
                while (ifaces.hasMoreElements()) {
                    val iface = ifaces.nextElement()
                    if (iface.isLoopback || !iface.isUp) continue
                    val addrs = iface.inetAddresses
                    while (addrs.hasMoreElements()) {
                        val a = addrs.nextElement()
                        if (a is Inet4Address && !a.isLoopbackAddress) {
                            return a.hostAddress ?: "127.0.0.1"
                        }
                    }
                }
            } catch (_: Exception) {}
            return "127.0.0.1"
        }
    }
}
