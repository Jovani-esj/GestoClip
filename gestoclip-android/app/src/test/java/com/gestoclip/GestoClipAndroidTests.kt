package com.gestoclip

import com.gestoclip.net.DeviceCert
import com.gestoclip.net.DiscoveredPeer
import com.gestoclip.net.PairedStore
import com.gestoclip.net.PairingService
import com.gestoclip.net.QrPairInvite
import com.gestoclip.net.SecureChannel
import com.gestoclip.protocol.Framing
import com.gestoclip.protocol.Kinds
import com.gestoclip.protocol.PairRequestMessage
import com.gestoclip.protocol.Payload
import com.gestoclip.vision.GestureEvent
import com.gestoclip.vision.GestureStateMachine
import com.gestoclip.vision.HandPose
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.nio.file.Files

class GestoClipAndroidTests {

    @Test
    fun framing_roundTrip_and_shaVerification() {
        val original = Payload(Kinds.IMAGE, "foto.png", "image/png", ByteArray(4096) { (it % 251).toByte() })
        val bos = ByteArrayOutputStream()
        Framing.write(bos, original)

        val read = Framing.read(ByteArrayInputStream(bos.toByteArray()))
        assertEquals(original.kind, read.kind)
        assertEquals(original.name, read.name)
        assertArrayEquals(original.data, read.data)

        val corrupted = bos.toByteArray()
        corrupted[corrupted.lastIndex] = (corrupted.last().toInt() xor 0xFF).toByte()
        var threw = false
        try {
            Framing.read(ByteArrayInputStream(corrupted))
        } catch (_: IOException) {
            threw = true
        }
        assertTrue(threw)
    }

    @Test
    fun gestureStateMachine_recognizesCopyAndPaste_andFiltersJitter() {
        assertEquals(HandPose.FIST, GestureStateMachine.mapCategoryToPose("Closed_Fist", 0.85f))
        assertEquals(HandPose.PALM, GestureStateMachine.mapCategoryToPose("Open_Palm", 0.85f))
        assertEquals(HandPose.UNKNOWN, GestureStateMachine.mapCategoryToPose("Open_Palm", 0.20f))

        val sm = GestureStateMachine(stableFrames = 4)
        repeat(5) { assertEquals(GestureEvent.NONE, sm.push(HandPose.PALM)) }
        repeat(3) { assertEquals(GestureEvent.NONE, sm.push(HandPose.FIST)) }
        assertEquals(GestureEvent.COPY, sm.push(HandPose.FIST))

        repeat(3) { assertEquals(GestureEvent.NONE, sm.push(HandPose.PALM)) }
        assertEquals(GestureEvent.PASTE, sm.push(HandPose.PALM))
    }

    @Test
    fun qrInvite_parsesWindowsFormat_andEnforcesOneTimeToken() {
        val dir = Files.createTempDirectory("gestoclip-and-test").toFile()
        try {
            val store = PairedStore(dir.resolve("paired.json"))
            val svc = PairingService(store, "and1", "Pixel 8", "ANDROID")
            val invite = svc.createInvite("192.168.1.79", 8765, "abcdef1234567890")
            val parsed = QrPairInvite.tryParse(invite.toUri())
            assertNotNull(parsed)
            assertEquals("192.168.1.79", parsed!!.ip)
            assertEquals(invite.token, parsed.token)

            val (ack1, peer1) = svc.tryAcceptPairRequest("fp_win_12345678", PairRequestMessage(invite.token, "win1", "Laptop", "WINDOWS"))
            assertTrue(ack1.accepted)
            assertNotNull(peer1)
            assertTrue(store.isPaired("fp_win_12345678"))

            val (ack2, _) = svc.tryAcceptPairRequest("fp_evil_1234567", PairRequestMessage(invite.token, "evil", "Hacker", "WINDOWS"))
            assertFalse(ack2.accepted)
            assertFalse(store.isPaired("fp_evil_1234567"))
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test
    fun endToEnd_qrPairingOverTls_thenBidirectionalTransfer() = runBlocking {
        val dir = Files.createTempDirectory("gestoclip-tls-test").toFile()
        try {
            val serverStore = PairedStore(dir.resolve("server-paired.json"))
            val clientStore = PairedStore(dir.resolve("client-paired.json"))

            val serverId = DeviceCert.loadOrCreate(dir.resolve("server.p12"))
            val clientId = DeviceCert.loadOrCreate(dir.resolve("client.p12"))

            val serverPairing = PairingService(serverStore, "win-pc", "Laptop Windows", "WINDOWS")

            lateinit var serverChannel: SecureChannel
            lateinit var clientChannel: SecureChannel

            serverChannel = SecureChannel(
                identity = serverId,
                peersProvider = {
                    listOf(
                        DiscoveredPeer("and-1", "Pixel 8", "ANDROID", "127.0.0.1", clientChannel.listenPort, clientId.fingerprint, System.currentTimeMillis())
                    )
                },
                isPaired = serverStore::isPaired,
                port = 0,
                pairing = serverPairing
            )
            serverChannel.startListening()

            clientChannel = SecureChannel(
                identity = clientId,
                peersProvider = {
                    listOf(
                        DiscoveredPeer("win-pc", "Laptop Windows", "WINDOWS", "127.0.0.1", serverChannel.listenPort, serverId.fingerprint, System.currentTimeMillis())
                    )
                },
                isPaired = clientStore::isPaired,
                port = 0
            )
            clientChannel.startListening()

            try {
                val inviteUri = serverPairing.createInvite("127.0.0.1", serverChannel.listenPort, serverChannel.localFingerprint).toUri()
                val scanned = QrPairInvite.tryParse(inviteUri)!!
                val ack = clientChannel.pairWithServer(scanned, "and-1", "Pixel 8", "ANDROID", clientStore)
                assertTrue(ack.accepted)
                assertTrue(serverStore.isPaired(clientChannel.localFingerprint))
                assertTrue(clientStore.isPaired(serverChannel.localFingerprint))

                val gotOnServer = CompletableDeferred<Payload>()
                serverChannel.onReceived = { p, _ -> gotOnServer.complete(p) }

                val sent1 = clientChannel.sendToPeers(Payload(Kinds.TEXT, "clip.txt", "text/plain", "Hola desde Android".toByteArray()))
                assertEquals(1, sent1)
                val p1 = withTimeout(5000L) { gotOnServer.await() }
                assertEquals("Hola desde Android", String(p1.data))

                val gotOnClient = CompletableDeferred<Payload>()
                clientChannel.onReceived = { p, _ -> gotOnClient.complete(p) }

                val sent2 = serverChannel.sendToPeers(Payload(Kinds.TEXT, "clip.txt", "text/plain", "Hola desde Windows".toByteArray()))
                assertEquals(1, sent2)
                val p2 = withTimeout(5000L) { gotOnClient.await() }
                assertEquals("Hola desde Windows", String(p2.data))
            } finally {
                serverChannel.close()
                clientChannel.close()
            }
        } finally {
            dir.deleteRecursively()
        }
    }
}
