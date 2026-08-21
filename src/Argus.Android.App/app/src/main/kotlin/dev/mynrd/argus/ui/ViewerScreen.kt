package dev.mynrd.argus.ui

import android.app.Activity
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshots.SnapshotStateMap
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.input.pointer.PointerId
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.mynrd.argus.AppContainer
import dev.mynrd.argus.data.ArgusSettings
import dev.mynrd.argus.net.InjectionMode
import dev.mynrd.argus.net.KeyEventDto
import dev.mynrd.argus.net.MouseAction
import dev.mynrd.argus.net.MouseButton
import dev.mynrd.argus.net.QualityLevel
import dev.mynrd.argus.net.WindowStatus
import dev.mynrd.argus.ui.theme.LocalArgusColors
import dev.mynrd.argus.ui.theme.TapTarget
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.hypot
import kotlin.math.min
import kotlin.math.roundToInt

private const val MIN_ZOOM = 0.25f
private const val MAX_ZOOM = 4f

/** Hold this long without moving and a touch becomes a right-click. */
private const val LONG_PRESS_MS = 500L

/** Movement past this many pixels turns a touch into a drag rather than a tap. */
private const val DRAG_THRESHOLD_DP = 10

/** Floor on how often a drag reports a new position to the host. */
private const val MOVE_INTERVAL_MS = 30L

/** How long a pad key is held before it repeats, and how fast it repeats after that. */
private const val KEY_REPEAT_DELAY_MS = 400L
private const val KEY_REPEAT_MS = 90L

/** How far one wheel notch turns the app being driven, in the units Windows expects. */
private const val WHEEL_DELTA = 120

/** Notches per press of a scroll button, and how often a held button sends another. */
private const val BUTTON_SCROLL_NOTCHES = 3
private const val SCROLL_REPEAT_MS = 200L

/** A key on the floating pad: what it says, and the KeyboardEvent.code it sends. */
private data class PadKey(val label: String, val code: String, val wide: Boolean = false)

/** The four the pad can hold down. Sent as ordinary key presses around the key they modify. */
private enum class PadModifier(val label: String, val code: String) {
    Ctrl("Ctrl", "ControlLeft"),
    Alt("Alt", "AltLeft"),
    Shift("Shift", "ShiftLeft"),
    Meta("Win", "MetaLeft"),
}

private val EDIT_KEYS = listOf(
    PadKey("Esc", "Escape"),
    PadKey("Tab", "Tab"),
    PadKey("Enter", "Enter", wide = true),
    PadKey("Bksp", "Backspace"),
    PadKey("Del", "Delete"),
    PadKey("Ins", "Insert"),
)

private val NAV_KEYS = listOf(
    PadKey("Home", "Home"),
    PadKey("End", "End"),
    PadKey("PgUp", "PageUp"),
    PadKey("PgDn", "PageDown"),
    PadKey("Menu", "ContextMenu"),
    PadKey("PrtSc", "PrintScreen"),
)

private val ARROW_KEYS = listOf(
    PadKey("←", "ArrowLeft"),
    PadKey("↑", "ArrowUp"),
    PadKey("↓", "ArrowDown"),
    PadKey("→", "ArrowRight"),
)

/** Two rows of six rather than one of twelve, which is wider than the rest of the pad. */
private val FUNCTION_ROWS = listOf(
    (1..6).map { PadKey("F$it", "F$it") },
    (7..12).map { PadKey("F$it", "F$it") },
)

/** Sizes the host window can be snapped to. The value is what the stream ends up being. */
private val SIZES = listOf(
    "1920 × 1080" to (1920 to 1080),
    "1360 × 768" to (1360 to 768),
    // Phone-shaped, so a desktop app with a responsive layout shows its narrow one.
    "Mobile · 412 × 915" to (412 to 915),
)

/** 0 off, 1 latched (released after the next key), 2 locked (held until tapped off). */
private const val MOD_OFF = 0
private const val MOD_LATCHED = 1
private const val MOD_LOCKED = 2

/**
 * Full-screen view of one window, with the pointer and the keyboard pointed at it.
 *
 * A port of viewer.component.ts control for control: the same zoom range and pinch arithmetic, the
 * same long-press-is-a-right-click rules, the same three-state modifiers on the key pad, and the
 * same icon-only toolbar - so what you learn on the browser carries to the phone.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun ViewerScreen(container: AppContainer, handle: Long, onBack: () -> Unit) {
    val colors = LocalArgusColors.current
    val argus = container.argus
    val store = container.settings
    val scope = rememberCoroutineScope()
    val keyboard = LocalSoftwareKeyboardController.current
    val view = LocalView.current
    val window = (LocalContext.current as? Activity)?.window
    val density = LocalDensity.current

    val settings by store.settings.collectAsStateWithLifecycle(initialValue = ArgusSettings())
    val statuses by argus.statuses.collectAsStateWithLifecycle()
    val connected by argus.connected.collectAsStateWithLifecycle()
    val status = statuses[handle]

    var quality by remember { mutableStateOf(QualityLevel.High) }
    var menu by remember { mutableStateOf<String?>(null) }

    /**
     * What a one-finger drag means. Clicking never depends on it: a tap is a left click and a hold
     * is a right click either way. A finger just cannot mean both "move the picture" and "drag
     * something on the host", so dragging something over there is the one thing that needs saying
     * out loud.
     */
    var dragToHost by remember { mutableStateOf(false) }
    var fullscreen by remember { mutableStateOf(false) }
    var fitted by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }

    var zoom by remember { mutableFloatStateOf(1f) }
    var panX by remember { mutableFloatStateOf(0f) }
    var panY by remember { mutableFloatStateOf(0f) }
    var metrics by remember { mutableStateOf(FrameMetrics()) }

    var sendKeys by remember { mutableStateOf(false) }
    val keyboardSink = remember { FocusRequester() }
    var sink by remember { mutableStateOf(TextFieldValue("")) }

    val modifiers = remember { SnapshotStateMap<PadModifier, Int>() }
    val heldModifiers = remember { mutableSetOf<PadModifier>() }
    var showFunctionKeys by remember { mutableStateOf(false) }
    var padMinimized by remember { mutableStateOf(false) }
    var padOffsetX by remember { mutableFloatStateOf(0f) }
    var padOffsetY by remember { mutableFloatStateOf(0f) }
    var padSize by remember { mutableStateOf(IntSize.Zero) }
    var stageSize by remember { mutableStateOf(IntSize.Zero) }

    val showKeyPad = settings.showKeyPad
    val showScrollButtons = settings.showScrollButtons

    // ------------------------------------------------------------- key plumbing

    /**
     * Key events have to reach the host in the order they were made. Each one is a hub round trip,
     * so a fast tap could otherwise have its key-up overtake its key-down and strand the key down.
     */
    val keyQueue = remember { Channel<Pair<String, Int>>(Channel.UNLIMITED) }

    LaunchedEffect(handle) {
        for ((code, type) in keyQueue) {
            // Key is the code, not a character: the server treats a one-character key as printable
            // text, so "Enter" as a key would type a glyph instead of pressing the virtual key.
            val result = argus.sendKey(
                KeyEventDto(windowId = handle.toString(), type = type, code = code, key = code),
                InjectionMode.Focus,
            )
            problem = if (!result.delivered) result.reason else null
        }
    }

    fun queueKey(code: String, type: Int) {
        keyQueue.trySend(code to type)
    }

    fun sendCharacter(character: Char) {
        scope.launch {
            val result = argus.sendKey(
                KeyEventDto(
                    windowId = handle.toString(),
                    type = 0,
                    code = codeForCharacter(character),
                    key = character.toString(),
                ),
                InjectionMode.Focus,
            )
            if (!result.delivered && result.reason != null) problem = result.reason
        }
    }

    /**
     * Brings the window to the front on the host and arms typing here, so the next keystroke lands
     * in the app rather than nowhere. Reached through the key pad, which is the switch for typing.
     */
    fun focusApp() {
        sendKeys = true
        problem = null
        runCatching { keyboardSink.requestFocus() }
        keyboard?.show()
        scope.launch {
            val result = argus.focusWindow(handle)
            if (!result.focused && result.reason != null) problem = result.reason
        }
    }

    /** Lifts any locked modifier off the host and blanks the row. */
    fun clearModifiers() {
        for (modifier in heldModifiers) queueKey(modifier.code, 1)
        heldModifiers.clear()
        modifiers.clear()
    }

    // ----------------------------------------------------------- mouse plumbing

    /** True when the point is on the frame itself rather than the letterboxing around it. */
    fun contains(point: Offset): Boolean {
        if (!metrics.ready) return false
        val drawnW = metrics.contentW * zoom
        val drawnH = metrics.contentH * zoom
        val left = metrics.containerW / 2f + panX - drawnW / 2f
        val top = metrics.containerH / 2f + panY - drawnH / 2f
        return point.x in left..(left + drawnW) && point.y in top..(top + drawnH)
    }

    /** Where a point on screen falls on the captured frame, as a 0..1 fraction. */
    fun normalise(point: Offset): Pair<Float, Float> {
        val drawnW = metrics.contentW * zoom
        val drawnH = metrics.contentH * zoom
        if (drawnW <= 0f || drawnH <= 0f) return 0f to 0f

        val left = metrics.containerW / 2f + panX - drawnW / 2f
        val top = metrics.containerH / 2f + panY - drawnH / 2f
        return ((point.x - left) / drawnW).coerceIn(0f, 1f) to
            ((point.y - top) / drawnH).coerceIn(0f, 1f)
    }

    fun sendMouse(action: MouseAction, button: MouseButton, x: Float, y: Float, delta: Int = 0) {
        scope.launch {
            val result = argus.sendMouse(handle, action, button, x, y, delta)
            problem = if (!result.delivered) result.reason else null
        }
    }

    fun sendMouseAt(action: MouseAction, button: MouseButton, point: Offset) {
        val (x, y) = normalise(point)
        sendMouse(action, button, x, y)
    }

    /** A button press has no cursor of its own, so it scrolls the middle of the frame. */
    fun scrollHost(direction: Int) {
        sendMouse(
            MouseAction.Scroll,
            MouseButton.Left,
            0.5f,
            0.5f,
            direction * BUTTON_SCROLL_NOTCHES * WHEEL_DELTA,
        )
    }

    // ------------------------------------------------------------- zoom and pan

    /** Stops the image being dragged off into empty space, in either axis independently. */
    fun clampPan() {
        if (!metrics.ready) return
        val maxX = ((metrics.contentW * zoom - metrics.containerW) / 2f).coerceAtLeast(0f)
        val maxY = ((metrics.contentH * zoom - metrics.containerH) / 2f).coerceAtLeast(0f)
        panX = panX.coerceIn(-maxX, maxX)
        panY = panY.coerceIn(-maxY, maxY)
    }

    /**
     * Zooms about a fixed point rather than the stage centre, so whatever is between the fingers
     * stays put. Without this, zooming in always dives at the middle of the window and the edges
     * are unreachable no matter how you drag afterwards.
     */
    fun zoomAbout(point: Offset, next: Float) {
        val target = next.coerceIn(MIN_ZOOM, MAX_ZOOM)
        if (target == zoom) return

        fitted = false
        val dx = point.x - metrics.containerW / 2f
        val dy = point.y - metrics.containerH / 2f
        val scale = target / zoom

        panX = dx - (dx - panX) * scale
        panY = dy - (dy - panY) * scale
        zoom = target
        clampPan()
    }

    fun panBy(dx: Float, dy: Float) {
        if (dx != 0f || dy != 0f) fitted = false
        panX += dx
        panY += dy
        clampPan()
    }

    fun resetZoom() {
        fitted = false
        zoom = 1f
        panX = 0f
        panY = 0f
    }

    /** Scales the stream to this phone's screen, one factor for both axes so nothing stretches. */
    fun fitToScreen() {
        if (!metrics.ready) return
        zoom = min(metrics.containerW / metrics.contentW, metrics.containerH / metrics.contentH)
            .coerceIn(MIN_ZOOM, MAX_ZOOM)
        panX = 0f
        panY = 0f
        fitted = true
    }

    var autoFitted by remember { mutableStateOf(false) }

    // A quality switch or a host resize changes the frame's pixel size, which changes what fits.
    //
    // The first frame fits itself, which the browser build does not do. There, an unfitted viewer
    // is a window at its own pixel size on a monitor, which is usually about right; on a phone in
    // portrait it is a postage stamp in the middle of a black screen.
    LaunchedEffect(metrics) {
        if (!metrics.ready) return@LaunchedEffect

        if (!autoFitted) {
            autoFitted = true
            fitToScreen()
        } else if (fitted) {
            fitToScreen()
        }
    }

    // ------------------------------------------------------------------ lifecycle

    // Subscribing is reactive rather than one-shot: opening the viewer races the hub connection,
    // and a reconnect issues a new connection id that needs the subscription re-established.
    LaunchedEffect(handle, quality, connected) {
        if (connected) argus.subscribe(handle, quality)
    }

    // The pad is remembered per device, so a viewer can open with it already showing. Arm typing to
    // match rather than leaving it dead until the pad is touched.
    LaunchedEffect(Unit) {
        if (showKeyPad) focusApp()
    }

    fun setSystemBars(visible: Boolean) {
        val target = window ?: return
        val controller: WindowInsetsControllerCompat = WindowCompat.getInsetsController(target, view)
        if (visible) {
            controller.show(WindowInsetsCompat.Type.systemBars())
        } else {
            controller.systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            controller.hide(WindowInsetsCompat.Type.systemBars())
        }
    }

    DisposableEffect(handle) {
        onDispose {
            // A stuck Ctrl on the host is worse than a stuck click: every keystroke on the machine
            // becomes a shortcut afterwards.
            for (modifier in heldModifiers) keyQueue.trySend(modifier.code to 1)
            setSystemBars(true)
            container.scope.launch { argus.unsubscribe(handle) }
        }
    }

    val dragThreshold = with(density) { DRAG_THRESHOLD_DP.dp.toPx() }
    val padGap = with(density) { 8.dp.toPx() }

    Column(
        Modifier
            .fillMaxSize()
            .background(colors.bg)
            .then(
                if (fullscreen) Modifier else Modifier.windowInsetsPadding(WindowInsets.safeDrawing),
            ),
    ) {
        // -------------------------------------------------------------------- bar

        Row(
            Modifier
                .fillMaxWidth()
                .background(colors.surface)
                .padding(horizontal = 6.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            ArgusIconButton(IconBack, "Back to dashboard", onClick = onBack)

            StatusDot(statusColourOf(status?.status))

            Column(Modifier.weight(1f)) {
                Text(
                    status?.title ?: "Live view",
                    style = MaterialTheme.typography.bodyMedium,
                    color = colors.text,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    buildString {
                        append(status?.status?.label ?: "Not attached")
                        val fps = status?.actualFps ?: 0.0
                        if (fps > 0) append(" · $fps fps")
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.textMuted,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }

        // Wraps rather than squashing: on a narrow phone the controls drop to a second line.
        FlowRow(
            Modifier
                .fillMaxWidth()
                .background(colors.surface)
                .padding(horizontal = 6.dp, vertical = 2.dp),
            horizontalArrangement = Arrangement.spacedBy(2.dp),
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                Slider(
                    value = zoom,
                    onValueChange = {
                        fitted = false
                        zoom = it.coerceIn(MIN_ZOOM, MAX_ZOOM)
                        clampPan()
                    },
                    valueRange = MIN_ZOOM..MAX_ZOOM,
                    colors = SliderDefaults.colors(
                        thumbColor = colors.accent,
                        activeTrackColor = colors.accent,
                        inactiveTrackColor = colors.borderStrong,
                    ),
                    modifier = Modifier.width(120.dp),
                )
                Text(
                    "${(zoom * 100).roundToInt()}%",
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.textMuted,
                    modifier = Modifier.widthIn(min = 42.dp),
                )
            }

            ArgusIconButton(IconQuality, "Quality: ${quality.label}") { menu = "quality" }
            ArgusIconButton(IconSize, "Resize the window") { menu = "size" }
            ArgusIconButton(IconReset, "Reset zoom") { resetZoom() }
            ArgusIconButton(IconFit, "Fit to screen", active = fitted) { fitToScreen() }

            // A phone has no wheel, so these are the only way to scroll the app being watched.
            ArgusIconButton(
                IconScroll,
                if (showScrollButtons) "Hide scroll buttons" else "Show scroll buttons",
                active = showScrollButtons,
            ) {
                scope.launch { store.setShowScrollButtons(!showScrollButtons) }
            }

            ArgusIconButton(
                IconKeyPad,
                if (showKeyPad) "Hide the key pad and stop typing" else "Show the key pad and type",
                active = showKeyPad,
            ) {
                val next = !showKeyPad
                scope.launch { store.setShowKeyPad(next) }

                if (next) {
                    focusApp()
                } else {
                    // Hiding the pad takes its keys away, so anything held would never get its
                    // key-up. Release before it disappears.
                    clearModifiers()
                    sendKeys = false
                    keyboard?.hide()
                }
            }

            ArgusIconButton(
                IconDrag,
                if (dragToHost) "Dragging drags the app" else "Dragging moves the picture",
                active = dragToHost,
            ) {
                dragToHost = !dragToHost
            }

            ArgusIconButton(
                if (fullscreen) IconFullscreenExit else IconFullscreenEnter,
                if (fullscreen) "Exit full screen" else "Full screen",
                active = fullscreen,
            ) {
                fullscreen = !fullscreen
                setSystemBars(!fullscreen)
                if (fullscreen) fitToScreen()
            }
        }

        problem?.let {
            Banner(it, BannerKind.Bad, Modifier.padding(horizontal = 8.dp, vertical = 4.dp))
        }

        // ------------------------------------------------------------------ stage

        Box(
            Modifier
                .fillMaxSize()
                .background(Color.Black)
                .onSizeChanged { stageSize = it }
                .pointerInput(dragToHost, metrics, dragThreshold) {
                    awaitEachGesture {
                        val positions = mutableMapOf<PointerId, Offset>()
                        var lastDistance = 0f
                        var lastMidpoint = Offset.Zero
                        var pressedButton = false
                        var longPressFired = false
                        var moved = false
                        /** A control on top of the stage took this gesture; the stage keeps out. */
                        var foreign = false
                        var startPoint = Offset.Zero
                        var longPress: Job? = null
                        var lastMoveAt = 0L
                        var lastPoint = Offset.Zero

                        while (true) {
                            val event = awaitPointerEvent()
                            for (change in event.changes) {
                                if (change.pressed) positions[change.id] = change.position
                                else positions.remove(change.id)
                            }

                            val points = positions.values.toList()

                            if (points.size >= 2) {
                                // A second finger means pinch-zoom, not a drag on the host - undo
                                // anything already pressed. Marking it moved is what stops the
                                // last finger of the pinch clicking the app as it lifts.
                                longPress?.cancel()
                                longPress = null
                                moved = true
                                if (pressedButton) {
                                    sendMouseAt(MouseAction.Up, MouseButton.Left, startPoint)
                                    pressedButton = false
                                }

                                val a = points[0]
                                val b = points[1]
                                val distance = hypot(a.x - b.x, a.y - b.y)
                                val midpoint = Offset((a.x + b.x) / 2f, (a.y + b.y) / 2f)

                                if (lastDistance > 0f && distance > 0f) {
                                    zoomAbout(midpoint, zoom * (distance / lastDistance))
                                    // Movement of the midpoint itself is a two-finger drag.
                                    panBy(midpoint.x - lastMidpoint.x, midpoint.y - lastMidpoint.y)
                                }

                                lastDistance = distance
                                lastMidpoint = midpoint
                                event.changes.forEach { it.consume() }
                                continue
                            }

                            if (points.size == 1) {
                                val change = event.changes.firstOrNull { it.pressed } ?: continue
                                lastPoint = change.position

                                if (!change.previousPressed) {
                                    // Fresh single finger. Nothing is sent yet: a finger does not
                                    // say which button it means until it moves or is held.
                                    startPoint = change.position
                                    lastDistance = 0f
                                    pressedButton = false
                                    longPressFired = false
                                    moved = false
                                    // The key pad and the scroll buttons sit on top of the stage
                                    // and consume their own presses in the Main pass, which runs
                                    // child-first. Without this, tapping a key would also click
                                    // the app at the point the key happens to cover.
                                    foreign = change.isConsumed

                                    if (!foreign && contains(change.position)) {
                                        longPress = scope.launch {
                                            delay(LONG_PRESS_MS)
                                            if (!pressedButton) {
                                                longPressFired = true
                                                sendMouseAt(
                                                    MouseAction.Click,
                                                    MouseButton.Right,
                                                    startPoint,
                                                )
                                            }
                                        }
                                    }
                                    if (!foreign) change.consume()
                                    continue
                                }

                                if (foreign) continue

                                if (lastDistance > 0f) {
                                    // The other finger of a pinch has just lifted; do not let the
                                    // survivor jump the image.
                                    lastDistance = 0f
                                    startPoint = change.position
                                    change.consume()
                                    continue
                                }

                                val travelled = hypot(
                                    change.position.x - startPoint.x,
                                    change.position.y - startPoint.y,
                                )

                                if (travelled >= dragThreshold && !moved) {
                                    // Past the threshold this is a drag, not a tap - so it must
                                    // not click on release, and the pending hold must not fire a
                                    // right-click into the middle of it.
                                    moved = true
                                    if (!pressedButton) {
                                        longPress?.cancel()
                                        longPress = null
                                    }
                                }

                                if (dragToHost) {
                                    if (moved && !pressedButton && !longPressFired) {
                                        // Press where the finger started, not where it is now, or
                                        // the first part of the drag is lost.
                                        pressedButton = true
                                        sendMouseAt(MouseAction.Down, MouseButton.Left, startPoint)
                                    }

                                    if (pressedButton) {
                                        // Throttled: the hub call is a round trip, and a phone
                                        // emits moves far faster than the host needs them.
                                        val now = System.currentTimeMillis()
                                        if (now - lastMoveAt >= MOVE_INTERVAL_MS) {
                                            lastMoveAt = now
                                            sendMouseAt(MouseAction.Move, MouseButton.Left, change.position)
                                        }
                                    }
                                } else if (zoom > 1f) {
                                    val delta = change.position - change.previousPosition
                                    panBy(delta.x, delta.y)
                                }

                                change.consume()
                                continue
                            }

                            // Everything is up.
                            longPress?.cancel()
                            longPress = null

                            if (pressedButton) {
                                sendMouseAt(MouseAction.Up, MouseButton.Left, lastPoint)
                            } else if (!foreign && !longPressFired && !moved && contains(lastPoint)) {
                                // Released quickly without moving: a tap, which is a left click.
                                sendMouseAt(MouseAction.Click, MouseButton.Left, lastPoint)
                            }
                            break
                        }
                    }
                },
        ) {
            FrameView(
                argus = argus,
                handle = handle,
                placeholder = if (connected) "Waiting for frames…" else "Connecting…",
                contentScale = ContentScale.Inside,
                zoom = zoom,
                panX = panX,
                panY = panY,
                onMetrics = { metrics = it },
                modifier = Modifier.fillMaxSize(),
            )

            if (showScrollButtons) {
                Column(
                    Modifier
                        .align(Alignment.CenterEnd)
                        .padding(8.dp),
                    verticalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    HoldButton(IconChevronUp, "Scroll up") { scrollHost(1) }
                    HoldButton(IconChevronDown, "Scroll down") { scrollHost(-1) }
                }
            }

            if (showKeyPad) {
                KeyPad(
                    available = with(density) { stageSize.width.toDp() },
                    modifiers = modifiers,
                    minimized = padMinimized,
                    showFunctionKeys = showFunctionKeys,
                    offsetX = padOffsetX,
                    offsetY = padOffsetY,
                    onSized = { padSize = it },
                    onDrag = { dx, dy ->
                        // Measured against the stage, so the pad cannot be dragged off it.
                        val restLeft = (stageSize.width - padSize.width) / 2f
                        val restTop = stageSize.height - padSize.height - padGap
                        val minX = -restLeft
                        val maxX = (stageSize.width - padSize.width - restLeft).coerceAtLeast(minX)
                        val minY = -restTop
                        val maxY = (stageSize.height - padSize.height - restTop).coerceAtLeast(minY)

                        padOffsetX = (padOffsetX + dx).coerceIn(minX, maxX)
                        padOffsetY = (padOffsetY + dy).coerceIn(minY, maxY)
                    },
                    onTouched = { if (!sendKeys) focusApp() },
                    onCycleModifier = { modifier ->
                        // Off - latched - locked. Latched applies to the next key, which is what
                        // Ctrl+C wants; locked holds the key down, which is what Alt+Tab+Tab wants.
                        val current = modifiers[modifier] ?: MOD_OFF
                        val next = when (current) {
                            MOD_OFF -> MOD_LATCHED
                            MOD_LATCHED -> MOD_LOCKED
                            else -> MOD_OFF
                        }
                        modifiers[modifier] = next

                        if (next == MOD_LOCKED) {
                            heldModifiers.add(modifier)
                            queueKey(modifier.code, 0)
                        } else if (current == MOD_LOCKED) {
                            heldModifiers.remove(modifier)
                            queueKey(modifier.code, 1)
                        }
                    },
                    onToggleFunctionKeys = { showFunctionKeys = !showFunctionKeys },
                    onToggleMinimize = {
                        padMinimized = !padMinimized
                        // A collapsed pad has no modifier keys to tap off, and a locked one is
                        // physically down on the host until something releases it.
                        if (padMinimized) clearModifiers()
                        focusApp()
                    },
                    onHide = {
                        clearModifiers()
                        sendKeys = false
                        keyboard?.hide()
                        scope.launch { store.setShowKeyPad(false) }
                    },
                    onKeyDown = { key ->
                        // Locked modifiers are already down on the host; only the latched ones are
                        // wrapped around this press, and they come back up with it.
                        val latched = PadModifier.entries.filter { modifiers[it] == MOD_LATCHED }
                        for (modifier in latched) queueKey(modifier.code, 0)
                        queueKey(key.code, 0)
                        latched
                    },
                    onKeyRepeat = { key -> queueKey(key.code, 0) },
                    onKeyUp = { key, latched ->
                        queueKey(key.code, 1)
                        for (modifier in latched.reversed()) queueKey(modifier.code, 1)
                        for (modifier in latched) modifiers[modifier] = MOD_OFF
                    },
                )
            }

            // A real input, kept to one pixel: focusing it is what raises the soft keyboard, and
            // its text changes are how a phone keyboard reports what was typed.
            BasicTextField(
                value = sink,
                onValueChange = { next ->
                    if (sendKeys) for (character in next.text) sendCharacter(character)
                    if (next.text.isNotEmpty()) sink = TextFieldValue("")
                },
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Go),
                keyboardActions = KeyboardActions(
                    onGo = {
                        queueKey("Enter", 0)
                        queueKey("Enter", 1)
                    },
                ),
                modifier = Modifier
                    .size(1.dp)
                    .focusRequester(keyboardSink)
                    .onKeyEvent { event ->
                        // Backspace never reaches onValueChange, because the box is always empty.
                        val backspace = event.key == Key.Backspace
                        if (sendKeys && backspace && event.type == KeyEventType.KeyDown) {
                            queueKey("Backspace", 0)
                            queueKey("Backspace", 1)
                            true
                        } else {
                            false
                        }
                    },
            )
        }
    }

    when (menu) {
        "quality" -> ChoiceSheet(
            title = "Quality",
            options = QualityLevel.entries.map { it.label },
            chosen = quality.label,
            onDismiss = { menu = null },
            onPick = { index ->
                quality = QualityLevel.entries[index]
                menu = null
            },
        )

        // Nothing is marked as the current size on purpose - the app is free to clamp the request,
        // so a tick next to a preset could be a lie.
        "size" -> ChoiceSheet(
            title = "Window size",
            options = SIZES.map { it.first },
            chosen = null,
            onDismiss = { menu = null },
            onPick = { index ->
                menu = null
                val (width, height) = SIZES[index].second
                scope.launch {
                    val result = argus.resizeWindow(handle, width, height)
                    problem = if (result.resized) null else result.reason ?: "Could not resize that window"
                }
            },
        )
    }
}

// -------------------------------------------------------------------- key pad

/**
 * The keys a phone's own keyboard has no way to produce, floating over the stream.
 *
 * It is the switch for typing rather than just a row of extra keys: showing it foregrounds the app
 * on the host and arms the keyboard, hiding it puts everything back down.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun BoxScope.KeyPad(
    available: Dp,
    modifiers: SnapshotStateMap<PadModifier, Int>,
    minimized: Boolean,
    showFunctionKeys: Boolean,
    offsetX: Float,
    offsetY: Float,
    onSized: (IntSize) -> Unit,
    onDrag: (Float, Float) -> Unit,
    onTouched: () -> Unit,
    onCycleModifier: (PadModifier) -> Unit,
    onToggleFunctionKeys: () -> Unit,
    onToggleMinimize: () -> Unit,
    onHide: () -> Unit,
    onKeyDown: (PadKey) -> List<PadModifier>,
    onKeyRepeat: (PadKey) -> Unit,
    onKeyUp: (PadKey, List<PadModifier>) -> Unit,
) {
    val colors = LocalArgusColors.current

    // Sized to the screen rather than to fixed key widths. A phone is narrower than the eight
    // controls on the top row and the six on every other one, and a pad that wraps is a wall of
    // buttons in a different place on every device.
    val gap = 5.dp
    val inner = (available - 24.dp).coerceAtLeast(240.dp)
    val topWidth = ((inner - gap * 7) / 8).coerceIn(34.dp, 64.dp)
    val keyWidth = ((inner - gap * 5) / 6).coerceIn(40.dp, 76.dp)
    val fnWidth = ((inner - gap * 5) / 6).coerceIn(38.dp, 60.dp)
    val arrowWidth = ((inner - gap * 3) / 4).coerceIn(48.dp, 76.dp)

    Column(
        Modifier
            .align(Alignment.BottomCenter)
            .offset { IntOffset(offsetX.roundToInt(), offsetY.roundToInt()) }
            .padding(bottom = 8.dp)
            .onSizeChanged(onSized)
            .clip(RoundedCornerShape(10.dp))
            // Translucent because it is on top of the picture.
            .background(colors.surface.copy(alpha = 0.92f))
            .border(1.dp, colors.borderStrong, RoundedCornerShape(10.dp))
            .padding(6.dp)
            // Touching the pad at all is a request for the app: with the pad open on a viewer that
            // has not armed typing yet, this is what arms it.
            .pointerInput(Unit) { detectTapGestures(onPress = { onTouched() }) },
        verticalArrangement = Arrangement.spacedBy(5.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        FlowRow(horizontalArrangement = Arrangement.spacedBy(5.dp)) {
            Box(
                Modifier
                    .size(width = topWidth, height = TapTarget)
                    .clip(RoundedCornerShape(6.dp))
                    .background(colors.surfaceRaised)
                    .border(1.dp, colors.borderStrong, RoundedCornerShape(6.dp))
                    .pointerInput(Unit) {
                        detectDragGestures { change, dragAmount ->
                            change.consume()
                            onDrag(dragAmount.x, dragAmount.y)
                        }
                    },
                contentAlignment = Alignment.Center,
            ) {
                Canvas(Modifier.size(18.dp)) { IconGrip(colors.textMuted) }
            }

            if (!minimized) {
                for (modifier in PadModifier.entries) {
                    val state = modifiers[modifier] ?: MOD_OFF
                    PadButton(
                        label = modifier.label,
                        width = topWidth,
                        // Latched is "applies to the next key", locked is "held down on the host":
                        // two states, so two weights of the same colour.
                        background = when (state) {
                            MOD_LOCKED -> colors.accent
                            MOD_LATCHED -> colors.accentDim
                            else -> colors.surfaceRaised
                        },
                        foreground = if (state == MOD_LOCKED) colors.bg else colors.text,
                        bold = state == MOD_LOCKED,
                        onClick = { onCycleModifier(modifier) },
                    )
                }

                PadButton(
                    label = "Fn",
                    width = topWidth,
                    background = if (showFunctionKeys) colors.accentDim else colors.surfaceRaised,
                    foreground = colors.text,
                    onClick = onToggleFunctionKeys,
                )
            }

            PadIcon(
                if (minimized) IconKeyPad else IconMinimize,
                if (minimized) "Show the keys" else "Minimize the key pad",
                topWidth,
                onToggleMinimize,
            )
            PadIcon(IconClose, "Hide the key pad", topWidth, onHide)
        }

        if (!minimized) {
            PadKeyRow(EDIT_KEYS, onKeyDown, onKeyRepeat, onKeyUp, width = keyWidth)
            PadKeyRow(NAV_KEYS, onKeyDown, onKeyRepeat, onKeyUp, width = keyWidth)

            if (showFunctionKeys) {
                for (row in FUNCTION_ROWS) {
                    PadKeyRow(row, onKeyDown, onKeyRepeat, onKeyUp, width = fnWidth)
                }
            }

            PadKeyRow(ARROW_KEYS, onKeyDown, onKeyRepeat, onKeyUp, width = arrowWidth)
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun PadKeyRow(
    keys: List<PadKey>,
    onKeyDown: (PadKey) -> List<PadModifier>,
    onKeyRepeat: (PadKey) -> Unit,
    onKeyUp: (PadKey, List<PadModifier>) -> Unit,
    width: Dp = 58.dp,
) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(5.dp)) {
        for (key in keys) {
            HeldKey(
                key = key,
                width = width,
                onKeyDown = onKeyDown,
                onKeyRepeat = onKeyRepeat,
                onKeyUp = onKeyUp,
            )
        }
    }
}

/**
 * Press is separate from release so holding a key repeats, the way holding a real key does - one
 * Backspace per tap is not a way to clear a line of text.
 */
@Composable
private fun HeldKey(
    key: PadKey,
    width: Dp,
    onKeyDown: (PadKey) -> List<PadModifier>,
    onKeyRepeat: (PadKey) -> Unit,
    onKeyUp: (PadKey, List<PadModifier>) -> Unit,
) {
    val colors = LocalArgusColors.current
    val scope = rememberCoroutineScope()
    var pressed by remember { mutableStateOf(false) }

    Box(
        Modifier
            .size(width = width, height = TapTarget)
            .clip(RoundedCornerShape(6.dp))
            .background(if (pressed) colors.accentDim else colors.surfaceRaised)
            .border(
                1.dp,
                if (pressed) colors.accent else colors.borderStrong,
                RoundedCornerShape(6.dp),
            )
            .pointerInput(key) {
                detectTapGestures(
                    onPress = {
                        pressed = true
                        val latched = onKeyDown(key)

                        // Auto-repeat is just more key-downs with no key-up between them.
                        val repeat = scope.launch {
                            delay(KEY_REPEAT_DELAY_MS)
                            while (true) {
                                onKeyRepeat(key)
                                delay(KEY_REPEAT_MS)
                            }
                        }

                        tryAwaitRelease()
                        repeat.cancel()
                        pressed = false
                        onKeyUp(key, latched)
                    },
                )
            },
        contentAlignment = Alignment.Center,
    ) {
        Text(
            key.label,
            style = MaterialTheme.typography.bodySmall,
            color = colors.text,
            textAlign = TextAlign.Center,
        )
    }
}

@Composable
private fun PadButton(
    label: String,
    width: Dp,
    background: Color,
    foreground: Color,
    bold: Boolean = false,
    onClick: () -> Unit,
) {
    val colors = LocalArgusColors.current

    Box(
        Modifier
            .size(width = width, height = TapTarget)
            .clip(RoundedCornerShape(6.dp))
            .background(background)
            .border(1.dp, colors.borderStrong, RoundedCornerShape(6.dp))
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            label,
            style = MaterialTheme.typography.bodySmall,
            color = foreground,
            fontWeight = if (bold) FontWeight.SemiBold else FontWeight.Normal,
        )
    }
}

@Composable
private fun PadIcon(icon: IconPainter, description: String, width: Dp, onClick: () -> Unit) {
    val colors = LocalArgusColors.current

    Box(
        Modifier
            .size(width = width, height = TapTarget)
            .clip(RoundedCornerShape(6.dp))
            .background(colors.surfaceRaised)
            .border(1.dp, colors.borderStrong, RoundedCornerShape(6.dp)),
        contentAlignment = Alignment.Center,
    ) {
        ArgusIconButton(
            icon = icon,
            contentDescription = description,
            box = TapTarget,
            glyph = 16.dp,
            tint = colors.text,
            onClick = onClick,
        )
    }
}

/** A button that fires once on press and keeps firing while it is held. */
@Composable
private fun HoldButton(icon: IconPainter, description: String, onFire: () -> Unit) {
    val colors = LocalArgusColors.current
    val scope = rememberCoroutineScope()
    var pressed by remember { mutableStateOf(false) }

    Box(
        Modifier
            .size(TapTarget)
            .clip(RoundedCornerShape(6.dp))
            // Translucent rather than solid: these sit on top of the stream and hide part of it.
            .background(if (pressed) colors.accentDim else colors.surface.copy(alpha = 0.75f))
            .border(
                1.dp,
                if (pressed) colors.accent else colors.borderStrong,
                RoundedCornerShape(6.dp),
            )
            .pointerInput(Unit) {
                detectTapGestures(
                    onPress = {
                        pressed = true
                        // The first notch goes immediately, so a quick tap still does something.
                        onFire()
                        val repeat = scope.launch {
                            delay(SCROLL_REPEAT_MS)
                            while (true) {
                                onFire()
                                delay(SCROLL_REPEAT_MS)
                            }
                        }
                        tryAwaitRelease()
                        repeat.cancel()
                        pressed = false
                    },
                )
            },
        contentAlignment = Alignment.Center,
    ) {
        Canvas(Modifier.size(22.dp)) { icon(colors.text) }
    }
}

@Composable
private fun ChoiceSheet(
    title: String,
    options: List<String>,
    chosen: String?,
    onDismiss: () -> Unit,
    onPick: (Int) -> Unit,
) {
    val colors = LocalArgusColors.current

    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = colors.surface,
        titleContentColor = colors.text,
        textContentColor = colors.text,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                options.forEachIndexed { index, option ->
                    Box(
                        Modifier
                            .fillMaxWidth()
                            .heightIn(min = TapTarget)
                            .clip(RoundedCornerShape(6.dp))
                            .background(
                                if (option == chosen) colors.accentDim else colors.surfaceRaised,
                            )
                            .clickable { onPick(index) }
                            .padding(horizontal = 12.dp),
                        contentAlignment = Alignment.CenterStart,
                    ) {
                        Text(option, color = colors.text)
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text("Cancel", color = colors.text) }
        },
    )
}

@Composable
private fun statusColourOf(status: WindowStatus?): Color = with(LocalArgusColors.current) {
    when (status) {
        WindowStatus.Streaming -> ok
        WindowStatus.Starting -> accent
        WindowStatus.Stalled -> warn
        WindowStatus.Minimised -> idle
        else -> bad
    }
}

/** The KeyboardEvent.code the agent maps to a virtual key, worked back from the character. */
private fun codeForCharacter(character: Char): String = when {
    character in 'a'..'z' -> "Key${character.uppercaseChar()}"
    character in 'A'..'Z' -> "Key$character"
    character in '0'..'9' -> "Digit$character"
    else -> PUNCTUATION[character] ?: "Unidentified"
}

private val PUNCTUATION = mapOf(
    ' ' to "Space",
    '-' to "Minus",
    '=' to "Equal",
    '.' to "Period",
    ',' to "Comma",
    '/' to "Slash",
    '\\' to "Backslash",
    ';' to "Semicolon",
    '\'' to "Quote",
    '[' to "BracketLeft",
    ']' to "BracketRight",
    '`' to "Backquote",
)
