package dev.mynrd.argus.net

import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * Turns whatever the user typed into the one form the rest of the app concatenates paths onto.
 *
 * People type "100.84.12.3:5227", not "http://100.84.12.3:5227/". Both have to work, and the result
 * has to be stable, because it is the key everything else hangs off: the session cookie is scoped to
 * the host, and a trailing slash sneaking into storage would produce "http://host//api/session".
 */
object ServerUrl {

    /** Canonical "scheme://host[:port][/base]", no trailing slash, or null if it cannot be parsed. */
    fun normalise(raw: String): String? {
        val trimmed = raw.trim()
        if (trimmed.isEmpty()) return null

        // A bare host is the common case; assume http, because the agent serves plain HTTP unless
        // someone has deliberately put `tailscale serve` in front of it.
        val withScheme = if (trimmed.contains("://")) trimmed else "http://$trimmed"

        val url = withScheme.toHttpUrlOrNull() ?: return null
        if (url.host.isEmpty()) return null

        val defaultPort = if (url.scheme == "https") 443 else 80
        val port = if (url.port == defaultPort) "" else ":${url.port}"

        // A path survives, for an agent mounted under one by a reverse proxy. "/" is not a path.
        val path = url.encodedPath.trimEnd('/')

        return "${url.scheme}://${url.host}$port$path"
    }

    /** The WebSocket form of an already-normalised base. */
    fun toWebSocket(base: String): String = when {
        base.startsWith("https://") -> "wss://" + base.removePrefix("https://")
        base.startsWith("http://") -> "ws://" + base.removePrefix("http://")
        else -> base
    }
}
