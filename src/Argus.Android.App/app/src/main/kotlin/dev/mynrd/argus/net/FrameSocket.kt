package dev.mynrd.argus.net

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** int64 handle, int32 sequence, uint16 width, uint16 height, then the JPEG. */
private const val FRAME_HEADER_SIZE = 16

/**
 * The binary half of the connection: /ws/frames, one message per JPEG.
 *
 * Separate from the hub for the reason the server splits them - a burst of frames must not delay a
 * keystroke, and SignalR's JSON protocol would base64 every frame at a third again the bytes.
 */
class FrameSocket(private val http: OkHttpClient) {

    var onFrame: (Frame) -> Unit = {}
    var onOpenChanged: (Boolean) -> Unit = {}

    @Volatile
    private var socket: WebSocket? = null

    @Volatile
    private var openForClient: String = ""

    /**
     * Opens the socket for a hub connection id, or does nothing if it is already open for that id.
     *
     * The guard matters: the server keys subscriptions by client id, and a second socket for the
     * same id makes it tear the first one's subscriptions down - a viewer that looks connected and
     * never receives another frame.
     */
    fun open(base: String, clientId: String) {
        if (clientId.isEmpty()) return
        if (openForClient == clientId && socket != null) return

        close()

        val url = "${ServerUrl.toWebSocket(base)}/ws/frames?clientId=$clientId"
        openForClient = clientId
        socket = http.newWebSocket(Request.Builder().url(url).build(), Listener())
    }

    fun close() {
        val open = socket
        socket = null
        openForClient = ""
        open?.close(1000, null)
        onOpenChanged(false)
    }

    private inner class Listener : WebSocketListener() {

        override fun onOpen(webSocket: WebSocket, response: Response) {
            if (webSocket === socket) onOpenChanged(true)
        }

        override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
            if (webSocket !== socket || bytes.size < FRAME_HEADER_SIZE) return

            val raw = bytes.toByteArray()
            val header = ByteBuffer.wrap(raw, 0, FRAME_HEADER_SIZE).order(ByteOrder.LITTLE_ENDIAN)

            onFrame(
                Frame(
                    handle = header.getLong(0),
                    sequence = header.getInt(8),
                    // Kotlin has no unsigned short read; the mask is the uint16 the server wrote.
                    width = header.getShort(12).toInt() and 0xFFFF,
                    height = header.getShort(14).toInt() and 0xFFFF,
                    jpeg = raw.copyOfRange(FRAME_HEADER_SIZE, raw.size),
                ),
            )
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            if (webSocket !== socket) return
            socket = null
            openForClient = ""
            onOpenChanged(false)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            if (webSocket !== socket) return
            socket = null
            openForClient = ""
            onOpenChanged(false)
        }
    }
}
