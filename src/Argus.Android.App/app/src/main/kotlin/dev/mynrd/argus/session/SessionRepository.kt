package dev.mynrd.argus.session

import android.os.SystemClock
import dev.mynrd.argus.data.SettingsStore
import dev.mynrd.argus.net.ArgusApi
import dev.mynrd.argus.net.ServerUrl
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * How often the idle check runs. One ticker beats resetting a timer on every touch, and it keeps
 * working while the app is in the background, where a posted callback would be throttled.
 */
private const val IDLE_TICK_MS = 5_000L

/**
 * Whether this phone is allowed to reach the agent, and the idle clock that takes it away again.
 *
 * The server is the authority - it rejects the hub, the frame socket and every API call without a
 * session cookie. What lives here is only what the UI needs in order to show the right screen, plus
 * the one thing the browser build never had to think about: which agent we are talking to.
 */
class SessionRepository(
    private val api: ArgusApi,
    private val settings: SettingsStore,
    private val scope: CoroutineScope,
) {

    private val _ready = MutableStateFlow(false)
    private val _required = MutableStateFlow(false)
    private val _unlocked = MutableStateFlow(false)
    private val _reachable = MutableStateFlow<Boolean?>(null)
    private val _lastError = MutableStateFlow<String?>(null)
    private val _unlocking = MutableStateFlow(false)
    private val _unlockError = MutableStateFlow<String?>(null)

    /** True once /api/session has answered, so the shell does not flash the lock screen on launch. */
    val ready: StateFlow<Boolean> = _ready.asStateFlow()
    val required: StateFlow<Boolean> = _required.asStateFlow()
    val unlocked: StateFlow<Boolean> = _unlocked.asStateFlow()

    /** Null until the first attempt. Drives the dot in the top bar until the hub exists. */
    val reachable: StateFlow<Boolean?> = _reachable.asStateFlow()
    val lastError: StateFlow<String?> = _lastError.asStateFlow()

    /** The lock screen's own state, kept here so an unlock survives that screen going away. */
    val unlocking: StateFlow<Boolean> = _unlocking.asStateFlow()
    val unlockError: StateFlow<String?> = _unlockError.asStateFlow()

    val serverUrl: StateFlow<String> = settings.settings
        .map { it.serverUrl }
        .distinctUntilChanged()
        .stateIn(scope, SharingStarted.Eagerly, "")

    /**
     * An agent with no address is as unreachable as a locked one, and the lock screen is where the
     * address field lives - so first run lands there rather than on an empty dashboard.
     */
    val locked: StateFlow<Boolean> = combine(_ready, _required, _unlocked, serverUrl) { ready, required, unlocked, url ->
        ready && (url.isEmpty() || (required && !unlocked))
    }.stateIn(scope, SharingStarted.Eagerly, false)

    /** The address every call is built from. Kept here so unlock can set it without a store round trip. */
    @Volatile
    private var base: String = ""

    private var lastActivity = SystemClock.elapsedRealtime()
    private val refreshLock = Mutex()

    init {
        scope.launch {
            serverUrl.collect { url ->
                if (url == base) return@collect
                applyUrl(url)
                refresh()
            }
        }

        scope.launch {
            while (true) {
                delay(IDLE_TICK_MS)
                checkIdle()
            }
        }
    }

    /** Counts the user as still being here. Called from a pointer listener over the whole shell. */
    fun noteActivity() {
        lastActivity = SystemClock.elapsedRealtime()
    }

    suspend fun refresh() {
        refreshLock.withLock {
            val target = base
            if (target.isEmpty()) {
                _ready.value = true
                _reachable.value = null
                return@withLock
            }

            try {
                val state = api.session(target)
                _required.value = state.required
                _unlocked.value = state.unlocked
                _reachable.value = true
                _lastError.value = null
                if (state.unlocked) noteActivity()
            } catch (_: Exception) {
                // Cannot reach the agent at all. Claiming it is unlocked would be a lie, but claiming
                // it is locked would hide the connection error behind a password box - so leave the
                // last known state and say what actually went wrong.
                _reachable.value = false
                _lastError.value = "Cannot reach the Argus agent at $target"
            } finally {
                _ready.value = true
            }
        }
    }

    /**
     * Unlocks against the address on screen, which may not be the saved one yet - the lock screen
     * carries an editable address field, and typing a new one then pressing Unlock has to work
     * without a separate Save step.
     *
     * Runs on the repository's own scope rather than the caller's. The lock screen is composed only
     * while `locked` is true, so a request owned by it would be cancelled the instant the state it
     * is trying to produce arrives - the password check would be abandoned in flight.
     */
    fun requestUnlock(rawUrl: String, password: String) {
        if (_unlocking.value) return

        _unlockError.value = null
        _unlocking.value = true

        scope.launch {
            _unlockError.value = attemptUnlock(rawUrl, password)
            _unlocking.value = false
        }
    }

    fun clearUnlockError() {
        _unlockError.value = null
    }

    /** Null on success, or the reason to show under the password box. */
    private suspend fun attemptUnlock(rawUrl: String, password: String): String? {
        val target = ServerUrl.normalise(rawUrl)
            ?: return "That does not look like an address the agent could be on"

        if (target != base) {
            // Point at the new agent without going through applyUrl. Clearing `required` here would
            // report the app as unlocked before the password had been checked at all.
            base = target
            api.forget()
            settings.setServerUrl(target)
        }

        return try {
            val result = api.unlock(target, password)
            if (!result.unlocked) return result.reason ?: "Wrong password"

            noteActivity()
            _required.value = true
            _unlocked.value = true
            _reachable.value = true
            _lastError.value = null
            _ready.value = true
            null
        } catch (_: Exception) {
            _reachable.value = false
            "Cannot reach the Argus agent at $target"
        }
    }

    suspend fun lock() {
        // Locked locally first and regardless of the response: if the request failed, the last thing
        // to do is leave the desktop on screen while waiting to find out why.
        _unlocked.value = false
        val target = base
        if (target.isNotEmpty()) api.lock(target)
        api.forget()
    }

    /** Settings changes the address here; the lock screen goes through unlock(). */
    suspend fun setServerUrl(rawUrl: String): String? {
        val target = ServerUrl.normalise(rawUrl)
            ?: return "That does not look like an address the agent could be on"

        if (target != base) {
            applyUrl(target)
            settings.setServerUrl(target)
            refresh()
        }
        return null
    }

    /** The address the UI should show before the store has been read once. */
    suspend fun savedUrl(): String = serverUrl.first()

    private fun applyUrl(url: String) {
        base = url
        // The cookie belongs to the host it came from; carrying it to a different agent would only
        // produce a 401 that looks like a wrong password.
        api.forget()
        _unlocked.value = false
        _required.value = false
        _reachable.value = null
        _lastError.value = null
        _ready.value = url.isEmpty()
    }

    private suspend fun checkIdle() {
        if (!_unlocked.value || !_required.value) return

        val current = settings.settings.first()
        if (!current.autoLock) return

        val idleFor = SystemClock.elapsedRealtime() - lastActivity
        if (idleFor >= current.idleMinutes * 60_000L) lock()
    }
}
