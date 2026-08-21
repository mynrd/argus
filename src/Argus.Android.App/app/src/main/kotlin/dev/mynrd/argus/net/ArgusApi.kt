package dev.mynrd.argus.net

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.Cookie
import okhttp3.CookieJar
import okhttp3.HttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.util.concurrent.TimeUnit

/** Mirrors the body of GET /api/session. */
data class SessionState(val required: Boolean, val unlocked: Boolean)

/** Null reason means it worked. Anything else is what to show under the password box. */
data class UnlockResult(val unlocked: Boolean, val reason: String?)

private val JSON = "application/json; charset=utf-8".toMediaType()

/**
 * The plain HTTP half of the agent: the three endpoints that decide whether this phone may reach it
 * at all. The hub and the frame socket arrive in the next phase and ride the same cookie.
 *
 * Note what an installed app can do that the page cannot. The agent marks the session cookie
 * HttpOnly precisely so nothing running in the browser can read it; here, OkHttp receives it in a
 * normal response header, which is what lets the SignalR connection and the frame socket be
 * authenticated by hand later.
 */
class ArgusApi {

    private val cookies = SessionCookieJar()

    private val json = Json { ignoreUnknownKeys = true }

    /** Shared with the hub and the frame socket, so all three ride the same cookie jar. */
    val client: OkHttpClient = OkHttpClient.Builder()
        .cookieJar(cookies)
        // A Tailscale address that is not up answers nothing at all, so the connect timeout is what
        // the user actually waits through. Keep it short enough to feel like a failure, not a hang.
        .connectTimeout(6, TimeUnit.SECONDS)
        .readTimeout(15, TimeUnit.SECONDS)
        .build()

    /** For the hub and frame socket, which are not OkHttp calls and cannot use the jar. */
    val cookieHeader: String? get() = cookies.header()

    /** Drops the session token. Used when the address changes and when the user locks. */
    fun forget() = cookies.clear()

    suspend fun session(base: String): SessionState = withContext(Dispatchers.IO) {
        val request = Request.Builder().url("$base/api/session").get().build()
        client.newCall(request).execute().use { response ->
            val body = response.body.string()
            val dto = json.decodeFromString<SessionDto>(body)
            SessionState(dto.required, dto.unlocked)
        }
    }

    suspend fun unlock(base: String, password: String): UnlockResult = withContext(Dispatchers.IO) {
        val payload = json.encodeToString(UnlockRequest(password)).toRequestBody(JSON)
        val request = Request.Builder().url("$base/api/unlock").post(payload).build()

        client.newCall(request).execute().use { response ->
            // 401 and 429 carry the reason to show; the status alone would lose "wait 30s".
            val body = response.body.string()
            val dto = runCatching { json.decodeFromString<UnlockDto>(body) }.getOrNull()

            when {
                dto == null -> UnlockResult(false, "Unexpected reply from ${response.code}")
                dto.unlocked -> UnlockResult(true, null)
                else -> UnlockResult(false, dto.reason ?: "Wrong password")
            }
        }
    }

    suspend fun lock(base: String) = withContext(Dispatchers.IO) {
        val request = Request.Builder().url("$base/api/lock").post(ByteArray(0).toRequestBody()).build()
        runCatching { client.newCall(request).execute().close() }
        cookies.clear()
        Unit
    }

    @Serializable
    private data class SessionDto(val required: Boolean = false, val unlocked: Boolean = false)

    @Serializable
    private data class UnlockDto(val unlocked: Boolean = false, val reason: String? = null)

    @Serializable
    private data class UnlockRequest(val password: String)
}

/**
 * One session cookie, in memory, for one host at a time.
 *
 * In memory on purpose: the browser gets a session cookie with no expiry, so closing the tab ends
 * the session. Writing this to disk would quietly turn that into "unlocked forever on a phone that
 * anyone can pick up", which is the opposite of what the password is for.
 */
private class SessionCookieJar : CookieJar {

    private var host: String? = null
    private var cookie: Cookie? = null

    @Synchronized
    override fun saveFromResponse(url: HttpUrl, cookies: List<Cookie>) {
        for (fresh in cookies) {
            if (fresh.name != COOKIE_NAME) continue

            // Deleting the cookie is how /api/lock ends a session; the server sends it back empty.
            if (fresh.value.isEmpty()) {
                clear()
            } else {
                host = url.host
                cookie = fresh
            }
        }
    }

    @Synchronized
    override fun loadForRequest(url: HttpUrl): List<Cookie> {
        val current = cookie ?: return emptyList()
        return if (url.host == host) listOf(current) else emptyList()
    }

    @Synchronized
    fun header(): String? = cookie?.let { "${it.name}=${it.value}" }

    @Synchronized
    fun clear() {
        host = null
        cookie = null
    }

    private companion object {
        const val COOKIE_NAME = "argus.session"
    }
}
