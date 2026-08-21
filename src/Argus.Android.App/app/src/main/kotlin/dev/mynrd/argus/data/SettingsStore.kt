package dev.mynrd.argus.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.MutablePreferences
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.emptyPreferences
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.map
import java.io.IOException

/** Auto lock is on by default: the whole point of the password is that walking away is safe. */
private const val DEFAULT_AUTO_LOCK = true
private const val DEFAULT_IDLE_MINUTES = 10

private const val MIN_IDLE_MINUTES = 1
private const val MAX_IDLE_MINUTES = 240

/** The scroll buttons cover part of the stream, so whether they are worth it is a per-device call. */
private const val DEFAULT_SHOW_SCROLL_BUTTONS = true

/** The key pad covers more, so it stays off until asked for. */
private const val DEFAULT_SHOW_KEY_PAD = false

private val Context.preferences: DataStore<Preferences> by preferencesDataStore("argus.settings")

data class ArgusSettings(
    /** Empty until the user types one on the lock screen or in Settings. */
    val serverUrl: String = "",
    val autoLock: Boolean = DEFAULT_AUTO_LOCK,
    val idleMinutes: Int = DEFAULT_IDLE_MINUTES,
    val showScrollButtons: Boolean = DEFAULT_SHOW_SCROLL_BUTTONS,
    val showKeyPad: Boolean = DEFAULT_SHOW_KEY_PAD,
)

/**
 * What this phone remembers, in the place the browser build keeps localStorage.
 *
 * The server address is the one addition over the web app. There the page is served by the agent
 * itself, so window.location.origin is the answer; an installed app has to be told where to look,
 * and has to still know tomorrow.
 */
class SettingsStore(private val context: Context) {

    private object Keys {
        val ServerUrl = stringPreferencesKey("server_url")
        val AutoLock = booleanPreferencesKey("auto_lock")
        val IdleMinutes = intPreferencesKey("idle_minutes")
        val ShowScrollButtons = booleanPreferencesKey("show_scroll_buttons")
        val ShowKeyPad = booleanPreferencesKey("show_key_pad")
    }

    val settings: Flow<ArgusSettings> = context.preferences.data
        // A corrupt or unreadable store just means the defaults, exactly as a failed localStorage
        // read does in the browser. Anything else would leave the app with no screen to show.
        .catch { cause -> if (cause is IOException) emit(emptyPreferences()) else throw cause }
        .map { prefs ->
            ArgusSettings(
                serverUrl = prefs[Keys.ServerUrl] ?: "",
                autoLock = prefs[Keys.AutoLock] ?: DEFAULT_AUTO_LOCK,
                idleMinutes = clampIdle(prefs[Keys.IdleMinutes] ?: DEFAULT_IDLE_MINUTES),
                showScrollButtons = prefs[Keys.ShowScrollButtons] ?: DEFAULT_SHOW_SCROLL_BUTTONS,
                showKeyPad = prefs[Keys.ShowKeyPad] ?: DEFAULT_SHOW_KEY_PAD,
            )
        }

    /** Store the canonical form, so everything downstream can append paths to it blindly. */
    suspend fun setServerUrl(url: String) = edit { it[Keys.ServerUrl] = url }

    suspend fun setAutoLock(enabled: Boolean) = edit { it[Keys.AutoLock] = enabled }

    /** Clamped on the way in, so a stray 0 cannot turn the idle lock into an instant one. */
    suspend fun setIdleMinutes(minutes: Int) = edit { it[Keys.IdleMinutes] = clampIdle(minutes) }

    suspend fun setShowScrollButtons(show: Boolean) = edit { it[Keys.ShowScrollButtons] = show }

    suspend fun setShowKeyPad(show: Boolean) = edit { it[Keys.ShowKeyPad] = show }

    private suspend fun edit(block: (MutablePreferences) -> Unit) {
        try {
            context.preferences.edit(block)
        } catch (_: IOException) {
            // Not being able to remember a preference is not worth crashing the app over.
        }
    }

    private fun clampIdle(minutes: Int) = minutes.coerceIn(MIN_IDLE_MINUTES, MAX_IDLE_MINUTES)

    companion object {
        val IdleRange = MIN_IDLE_MINUTES..MAX_IDLE_MINUTES
    }
}
