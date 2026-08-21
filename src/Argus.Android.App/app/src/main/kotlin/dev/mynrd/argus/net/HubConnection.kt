package dev.mynrd.argus.net

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/** SignalR frames its JSON messages with this byte, not with the WebSocket message boundary. */
private const val RECORD_SEPARATOR = '\u001E'

/** The server drops a client it has not heard from for 30s. Ping well inside that. */
private const val PING_INTERVAL_MS = 10_000L

private const val HANDSHAKE_TIMEOUT_MS = 10_000L
private const val INVOKE_TIMEOUT_MS = 20_000L

/**
 * The SignalR JSON hub protocol, written out by hand over an OkHttp WebSocket.
 *
 * The official Java client would do this too, and pull in RxJava and Gson to do it. The protocol is
 * a negotiate POST, a one-line handshake and four message types, and writing it here means the
 * session cookie rides the same OkHttpClient - and therefore the same cookie jar - as every other
 * call, rather than being threaded through a second library's own header API.
 *
 * One connection at a time. [start] fully replaces whatever was open before.
 */
class HubConnection(
    private val http: OkHttpClient,
    private val scope: CoroutineScope,
    private val json: Json,
) {

    /** Server-to-client invocations: target name and the raw argument array. */
    var onMessage: (String, JsonArray) -> Unit = { _, _ -> }

    /** Fires once per closed connection, whatever closed it. */
    var onClosed: (String?) -> Unit = {}

    private val pending = ConcurrentHashMap<String, CompletableDeferred<JsonElement?>>()
    private val nextInvocationId = AtomicInteger(0)

    @Volatile
    private var socket: WebSocket? = null

    @Volatile
    private var handshake: CompletableDeferred<Unit>? = null

    @Volatile
    private var pingJob: Job? = null

    /** Blank unless connected. This is what /ws/frames wants as its clientId. */
    @Volatile
    var connectionId: String = ""
        private set

    val isOpen: Boolean get() = socket != null && connectionId.isNotEmpty()

    /**
     * Negotiates, opens the socket and completes the handshake. Throws if any of that fails, so the
     * caller's retry loop is the only place that decides what to do about it.
     */
    suspend fun start(base: String) {
        stop()

        val negotiated = negotiate(base)
        val pendingHandshake = CompletableDeferred<Unit>()
        handshake = pendingHandshake

        // connectionToken is what identifies the socket to the server; connectionId is what the
        // frame socket and the capture subscriptions are keyed by. With negotiateVersion=1 they are
        // two different strings, and using the wrong one produces a socket that never gets a frame.
        val query = "id=" + (negotiated.connectionToken ?: negotiated.connectionId)
        val request = Request.Builder()
            .url("${ServerUrl.toWebSocket(base)}/hubs/argus?$query")
            .build()

        val opened = http.newWebSocket(request, Listener())
        socket = opened

        try {
            withTimeout(HANDSHAKE_TIMEOUT_MS) { pendingHandshake.await() }
        } catch (cause: Exception) {
            stop()
            throw IOException("Hub handshake failed", cause)
        }

        connectionId = negotiated.connectionId
        pingJob = scope.launch {
            while (true) {
                delay(PING_INTERVAL_MS)
                send(buildJsonObject { put("type", 6) })
            }
        }
    }

    fun stop() {
        pingJob?.cancel()
        pingJob = null

        val open = socket
        socket = null
        connectionId = ""
        open?.close(1000, null)

        failPending("Connection closed")
    }

    /** Calls a hub method and waits for its completion message. Null for the void ones. */
    suspend fun invoke(target: String, arguments: List<JsonElement>): JsonElement? {
        val live = socket ?: throw IOException("Not connected")

        val id = nextInvocationId.incrementAndGet().toString()
        val result = CompletableDeferred<JsonElement?>()
        pending[id] = result

        val message = buildJsonObject {
            put("type", 1)
            put("invocationId", id)
            put("target", target)
            put("arguments", JsonArray(arguments))
        }

        if (!live.send(message.toString() + RECORD_SEPARATOR)) {
            pending.remove(id)
            throw IOException("Could not send $target")
        }

        return try {
            withTimeout(INVOKE_TIMEOUT_MS) { result.await() }
        } finally {
            pending.remove(id)
        }
    }

    private fun send(message: JsonObject) {
        socket?.send(message.toString() + RECORD_SEPARATOR)
    }

    private suspend fun negotiate(base: String): Negotiated = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url("$base/hubs/argus/negotiate?negotiateVersion=1")
            .post(ByteArray(0).toRequestBody())
            .build()

        http.newCall(request).execute().use { response ->
            if (!response.isSuccessful) {
                throw IOException("Negotiate refused with ${response.code}")
            }

            val body = json.parseToJsonElement(response.body.string()).jsonObject
            val id = body["connectionId"]?.jsonPrimitive?.content
                ?: throw IOException("Negotiate returned no connectionId")

            Negotiated(id, body["connectionToken"]?.jsonPrimitive?.content)
        }
    }

    private fun handle(text: String) {
        // One WebSocket message can carry several records, and the last one is always terminated,
        // so splitting leaves a trailing empty string to drop.
        for (record in text.split(RECORD_SEPARATOR)) {
            if (record.isEmpty()) continue

            val message = runCatching { json.parseToJsonElement(record).jsonObject }.getOrNull()
                ?: continue

            // The handshake response is the one message with no type field: {} or {"error": "..."}.
            val type = message["type"]?.jsonPrimitive?.int
            if (type == null) {
                val error = message["error"]?.jsonPrimitive?.content
                if (error == null) handshake?.complete(Unit) else handshake?.completeExceptionally(IOException(error))
                continue
            }

            when (type) {
                1 -> {
                    val target = message["target"]?.jsonPrimitive?.content ?: continue
                    val args = message["arguments"] as? JsonArray ?: JsonArray(emptyList())
                    onMessage(target, args)
                }

                3 -> {
                    val id = message["invocationId"]?.jsonPrimitive?.content ?: continue
                    val waiting = pending.remove(id) ?: continue
                    val error = message["error"]?.jsonPrimitive?.content

                    if (error != null) waiting.completeExceptionally(IOException(error))
                    else waiting.complete(message["result"])
                }

                // 6 is a ping and 7 a close; the close reason arrives again through onClosed.
                7 -> failPending(message["error"]?.jsonPrimitive?.content ?: "Server closed the connection")
            }
        }
    }

    private fun failPending(reason: String) {
        val waiting = pending.values.toList()
        pending.clear()
        for (call in waiting) call.completeExceptionally(IOException(reason))
    }

    private inner class Listener : WebSocketListener() {

        override fun onOpen(webSocket: WebSocket, response: Response) {
            webSocket.send("""{"protocol":"json","version":1}""" + RECORD_SEPARATOR)
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (webSocket === socket || handshake?.isCompleted == false) handle(text)
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            handshake?.completeExceptionally(t)
            if (webSocket !== socket) return

            socket = null
            connectionId = ""
            failPending(t.message ?: "Connection failed")
            onClosed(t.message)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            if (webSocket !== socket) return

            socket = null
            connectionId = ""
            failPending("Connection closed")
            this@HubConnection.onClosed(reason.ifEmpty { null })
        }
    }

    private data class Negotiated(val connectionId: String, val connectionToken: String?)
}
