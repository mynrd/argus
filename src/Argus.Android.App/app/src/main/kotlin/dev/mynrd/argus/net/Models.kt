package dev.mynrd.argus.net

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Mirrors of the server's DTOs. The names are the wire contract - SignalR's JSON protocol writes
 * camelCase and plain integers for enums, so these match Argus.Web/src/app/core/models.ts field for
 * field rather than being renamed to anything more Kotlin-shaped.
 */

/** Mirrors Argus.Server.Capture.QualityLevel. */
enum class QualityLevel(val wire: Int) {
    Preview(0),
    Low(1),
    Medium(2),
    High(3);

    val label: String get() = name
}

/** Mirrors Argus.Server.Capture.WindowStatus. */
enum class WindowStatus(val wire: Int) {
    Starting(0),
    Streaming(1),
    Stalled(2),
    Minimised(3),
    Closed(4);

    val label: String
        get() = when (this) {
            Starting -> "Starting"
            Streaming -> "Streaming"
            Stalled -> "Stalled"
            Minimised -> "Minimised"
            Closed -> "Not running"
        }

    companion object {
        fun of(wire: Int): WindowStatus = entries.firstOrNull { it.wire == wire } ?: Starting
    }
}

/** Mirrors Argus.Server.Input.InjectionMode. */
enum class InjectionMode(val wire: Int) {
    Background(0),
    Focus(1),
}

@Serializable
data class WindowListItem(
    val windowId: String = "",
    val handle: Long = 0,
    val title: String = "",
    val processName: String = "",
    val className: String = "",
    val width: Int = 0,
    val height: Int = 0,
    val isConsole: Boolean = false,
    val supportsBackgroundInput: Boolean = false,
    val backgroundInputNote: String? = null,
    val matchKey: String = "",
    val selected: Boolean = false,
)

@Serializable
data class WindowStatusUpdate(
    val windowId: String = "",
    val handle: Long = 0,
    val title: String = "",
    val processName: String = "",
    /** The wire carries the integer, not the name. Read it through [status]. */
    @SerialName("status") private val statusWire: Int = 0,
    val backend: String = "",
    val actualFps: Double = 0.0,
    val subscribers: Int = 0,
    val supportsBackgroundInput: Boolean = false,
    val note: String? = null,
) {
    val status: WindowStatus get() = WindowStatus.of(statusWire)
}

@Serializable
data class KeyEventDto(
    val windowId: String,
    /** 0 down, 1 up. */
    val type: Int,
    val code: String,
    val key: String,
    val ctrl: Boolean = false,
    val shift: Boolean = false,
    val alt: Boolean = false,
    val meta: Boolean = false,
)

/** Mirrors Argus.Server.Input.MouseAction. */
enum class MouseAction(val wire: Int) {
    Move(0),
    Down(1),
    Up(2),
    Click(3),
    Scroll(4),
}

/** Mirrors Argus.Server.Input.MouseButton. */
enum class MouseButton(val wire: Int) {
    Left(0),
    Right(1),
    Middle(2),
}

@Serializable
data class SendResult(
    val delivered: Boolean = false,
    val backend: String? = null,
    val reason: String? = null,
)

/** CloseWindow / KillWindow. `gone` means the window was already not there. */
@Serializable
data class CloseResult(
    val closed: Boolean = false,
    val gone: Boolean = false,
    val reason: String? = null,
)

@Serializable
data class StartResult(val started: Boolean = false, val reason: String? = null)

@Serializable
data class FocusResult(val focused: Boolean = false, val reason: String? = null)

@Serializable
data class ResizeResult(val resized: Boolean = false, val reason: String? = null)

@Serializable
data class BrowseEntry(
    val name: String = "",
    val path: String = "",
    val isDirectory: Boolean = false,
)

@Serializable
data class BrowseListing(
    val path: String = "",
    val label: String = "This PC",
    val parent: String? = null,
    val entries: List<BrowseEntry> = emptyList(),
    val error: String? = null,
)

@Serializable
data class OpenWithApp(
    val key: String = "",
    val label: String = "",
    val handlesFolders: Boolean = false,
    val handlesFiles: Boolean = false,
)

/** One decoded frame off /ws/frames: the 16-byte header, then the JPEG it describes. */
class Frame(
    val handle: Long,
    val sequence: Int,
    val width: Int,
    val height: Int,
    val jpeg: ByteArray,
)
