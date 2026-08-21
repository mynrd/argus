package dev.mynrd.argus.net

import android.os.SystemClock
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.OkHttpClient
import java.util.concurrent.ConcurrentHashMap

/** Missed heartbeats before the agent is treated as offline. The server beats every 2s. */
private const val HEARTBEAT_TIMEOUT_MS = 7_000L

/** How long to wait before trying the hub again after it drops. */
private const val RECONNECT_DELAY_MS = 2_000L

/**
 * Owns both connections to the agent and everything they say - the port of argus.service.ts.
 *
 * The hub carries control, the frame socket carries JPEGs, and the frame socket cannot be opened
 * until the hub has told us its connection id, so the two are started together and torn down
 * together.
 */
class ArgusClient(
    http: OkHttpClient,
    private val scope: CoroutineScope,
) {

    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
    private val hub = HubConnection(http, scope, json)
    private val frames = FrameSocket(http)

    private val _connected = MutableStateFlow(false)
    private val _framesOpen = MutableStateFlow(false)
    private val _agentOnline = MutableStateFlow(false)
    private val _windows = MutableStateFlow<List<WindowListItem>>(emptyList())
    private val _statuses = MutableStateFlow<Map<Long, WindowStatusUpdate>>(emptyMap())
    private val _lastError = MutableStateFlow<String?>(null)

    val connected: StateFlow<Boolean> = _connected.asStateFlow()
    val framesOpen: StateFlow<Boolean> = _framesOpen.asStateFlow()
    val agentOnline: StateFlow<Boolean> = _agentOnline.asStateFlow()
    val windows: StateFlow<List<WindowListItem>> = _windows.asStateFlow()
    val statuses: StateFlow<Map<Long, WindowStatusUpdate>> = _statuses.asStateFlow()
    val lastError: StateFlow<String?> = _lastError.asStateFlow()

    private val frameFlows = ConcurrentHashMap<Long, MutableSharedFlow<Frame>>()

    @Volatile
    private var base: String = ""

    @Volatile
    private var lastHeartbeat = 0L

    private var runner: Job? = null
    private var watchdog: Job? = null

    init {
        hub.onMessage = ::dispatch
        hub.onClosed = { reason ->
            _connected.value = false
            _agentOnline.value = false
            frames.close()
            if (reason != null) _lastError.value = "Lost the agent: $reason"
        }

        frames.onOpenChanged = { open -> _framesOpen.value = open }
        frames.onFrame = { frame -> frameFlows[frame.handle]?.tryEmit(frame) }
    }

    /**
     * Connects, and keeps trying until [stop].
     *
     * The retry loop lives here rather than in a reconnect option on the socket because a dropped
     * hub means a new connection id, which means the frame socket and every subscription have to be
     * rebuilt anyway.
     */
    fun start(serverUrl: String) {
        if (runner != null && base == serverUrl) return

        stop()
        base = serverUrl
        if (serverUrl.isEmpty()) return

        runner = scope.launch {
            while (true) {
                try {
                    hub.start(base)
                    _connected.value = true
                    _lastError.value = null

                    frames.open(base, hub.connectionId)
                    refreshWindows()
                    refreshStatuses()
                } catch (cause: Exception) {
                    _connected.value = false
                    _agentOnline.value = false
                    _lastError.value = "Cannot reach the Argus agent: ${cause.message ?: cause}"
                }

                // Idle here while the connection is up; the listener flips connected on the way out.
                while (_connected.value) delay(500)
                delay(RECONNECT_DELAY_MS)
            }
        }

        watchdog = scope.launch {
            while (true) {
                delay(2_000)
                val last = lastHeartbeat
                if (last > 0 && SystemClock.elapsedRealtime() - last > HEARTBEAT_TIMEOUT_MS) {
                    _agentOnline.value = false
                }
            }
        }
    }

    /**
     * Drops both connections and forgets what they said.
     *
     * Locking has to do this rather than only hiding the UI: a live hub is a live keyboard and mouse
     * on the host, and the frame socket keeps the desktop on screen behind whatever is drawn on top.
     */
    fun stop() {
        runner?.cancel()
        runner = null
        watchdog?.cancel()
        watchdog = null

        frames.close()
        hub.stop()

        base = ""
        lastHeartbeat = 0
        _connected.value = false
        _framesOpen.value = false
        _agentOnline.value = false
        _windows.value = emptyList()
        _statuses.value = emptyMap()
        _lastError.value = null
    }

    /** Frames for one window. Slow consumers drop frames rather than queue them. */
    fun framesFor(handle: Long): SharedFlow<Frame> = frameFlows.getOrPut(handle) {
        MutableSharedFlow(replay = 1, extraBufferCapacity = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    }

    // ------------------------------------------------------------------ control

    suspend fun refreshWindows() {
        val result = invoke("ListWindows") ?: return
        _windows.value = runCatching { json.decodeFromJsonElement<List<WindowListItem>>(result) }
            .getOrDefault(emptyList())
    }

    suspend fun refreshStatuses() {
        val result = invoke("ListStatuses") ?: return
        val list = runCatching { json.decodeFromJsonElement<List<WindowStatusUpdate>>(result) }
            .getOrNull() ?: return

        _statuses.value = list.associateBy { it.handle }
    }

    suspend fun attach(handle: Long) {
        val result = invoke("Attach", JsonPrimitive(handle))
        if (result != null && result != JsonNull) {
            decode<WindowStatusUpdate>(result)?.let { applyStatus(it) }
        }
        refreshWindows()
    }

    suspend fun detach(handle: Long) {
        invoke("Detach", JsonPrimitive(handle))
        _statuses.update { it - handle }
        refreshWindows()
    }

    suspend fun subscribe(handle: Long, quality: QualityLevel): Boolean {
        val result = invoke("Subscribe", JsonPrimitive(handle), JsonPrimitive(quality.wire))
        return result?.jsonPrimitive?.booleanOrNull ?: false
    }

    suspend fun unsubscribe(handle: Long) {
        invoke("Unsubscribe", JsonPrimitive(handle))
    }

    suspend fun sendKey(event: KeyEventDto, mode: InjectionMode): SendResult {
        val result = invoke("SendKey", json.encodeToJsonElement(KeyEventDto.serializer(), event), JsonPrimitive(mode.wire))
        return result?.let { decode<SendResult>(it) } ?: SendResult(reason = "Not connected")
    }

    suspend fun sendMouse(
        handle: Long,
        action: MouseAction,
        button: MouseButton,
        x: Float,
        y: Float,
        delta: Int? = null,
    ): SendResult {
        val payload = buildMouse(handle, action, button, x, y, delta)
        val result = invoke("SendMouse", payload)
        return result?.let { decode<SendResult>(it) } ?: SendResult(reason = "Not connected")
    }

    suspend fun runApplication(command: String): StartResult {
        val result = invoke("RunApplication", JsonPrimitive(command))
        return result?.let { decode<StartResult>(it) } ?: StartResult(reason = "Not connected")
    }

    suspend fun browse(path: String?): BrowseListing = listing("Browse", path)

    suspend fun explore(path: String?): BrowseListing = listing("Explore", path)

    suspend fun openWithApps(): List<OpenWithApp> {
        val result = invoke("OpenWithApps") ?: return emptyList()
        return runCatching { json.decodeFromJsonElement<List<OpenWithApp>>(result) }
            .getOrDefault(emptyList())
    }

    suspend fun openWith(path: String, app: String?): StartResult {
        val result = invoke("OpenWith", JsonPrimitive(path), app?.let { JsonPrimitive(it) } ?: JsonNull)
        return result?.let { decode<StartResult>(it) } ?: StartResult(reason = "Not connected")
    }

    suspend fun closeWindow(handle: Long): CloseResult {
        val result = invoke("CloseWindow", JsonPrimitive(handle))
        return result?.let { decode<CloseResult>(it) } ?: CloseResult(reason = "Not connected")
    }

    suspend fun killWindow(handle: Long): CloseResult {
        val result = invoke("KillWindow", JsonPrimitive(handle))
        return result?.let { decode<CloseResult>(it) } ?: CloseResult(reason = "Not connected")
    }

    suspend fun focusWindow(handle: Long): FocusResult {
        val result = invoke("FocusWindow", JsonPrimitive(handle))
        return result?.let { decode<FocusResult>(it) } ?: FocusResult(reason = "Not connected")
    }

    suspend fun resizeWindow(handle: Long, width: Int, height: Int): ResizeResult {
        val result = invoke(
            "ResizeWindow",
            JsonPrimitive(handle),
            JsonPrimitive(width),
            JsonPrimitive(height),
        )
        return result?.let { decode<ResizeResult>(it) } ?: ResizeResult(reason = "Not connected")
    }

    /**
     * Drops a window from the local list without waiting for the server to be re-enumerated. A
     * closed app should leave the screen the moment it is gone, and ListWindows is pulled.
     */
    fun forgetWindow(handle: Long) {
        _windows.update { list -> list.filter { it.handle != handle } }
    }

    fun statusFor(handle: Long): WindowStatusUpdate? = _statuses.value[handle]

    fun clearError() {
        _lastError.value = null
    }

    // ------------------------------------------------------------------ plumbing

    private suspend fun listing(target: String, path: String?): BrowseListing {
        val result = invoke(target, path?.let { JsonPrimitive(it) } ?: JsonNull)
        return result?.let { decode<BrowseListing>(it) }
            ?: BrowseListing(error = "Not connected")
    }

    private suspend fun invoke(target: String, vararg arguments: JsonElement): JsonElement? {
        if (!hub.isOpen) return null

        return try {
            hub.invoke(target, arguments.toList())
        } catch (cause: Exception) {
            _lastError.value = "$target failed: ${cause.message ?: cause}"
            null
        }
    }

    private inline fun <reified T> decode(element: JsonElement): T? =
        runCatching { json.decodeFromJsonElement<T>(element) }.getOrNull()

    private fun buildMouse(
        handle: Long,
        action: MouseAction,
        button: MouseButton,
        x: Float,
        y: Float,
        delta: Int?,
    ): JsonElement = buildJsonObject {
        put("windowId", JsonPrimitive(handle.toString()))
        put("action", JsonPrimitive(action.wire))
        put("button", JsonPrimitive(button.wire))
        put("x", JsonPrimitive(x))
        put("y", JsonPrimitive(y))
        if (delta != null) put("delta", JsonPrimitive(delta))
    }

    private fun dispatch(target: String, arguments: JsonArray) {
        when (target) {
            "Hello" -> {
                val payload = arguments.firstOrNull()?.jsonObject ?: return
                val statuses = payload["statuses"]?.jsonArray ?: return
                val list = statuses.mapNotNull { decode<WindowStatusUpdate>(it) }
                _statuses.value = list.associateBy { it.handle }
            }

            "WindowStatus" -> {
                val update = arguments.firstOrNull()?.let { decode<WindowStatusUpdate>(it) } ?: return
                applyStatus(update)
            }

            "Heartbeat" -> {
                lastHeartbeat = SystemClock.elapsedRealtime()
                _agentOnline.value = true
            }
        }
    }

    private fun applyStatus(update: WindowStatusUpdate) {
        _statuses.update { it + (update.handle to update) }
    }
}
