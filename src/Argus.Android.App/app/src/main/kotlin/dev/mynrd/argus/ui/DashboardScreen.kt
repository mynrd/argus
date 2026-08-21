package dev.mynrd.argus.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.mynrd.argus.AppContainer
import dev.mynrd.argus.net.BrowseEntry
import dev.mynrd.argus.net.BrowseListing
import dev.mynrd.argus.net.QualityLevel
import dev.mynrd.argus.net.WindowListItem
import dev.mynrd.argus.net.WindowStatus
import dev.mynrd.argus.net.WindowStatusUpdate
import dev.mynrd.argus.ui.theme.LocalArgusColors
import dev.mynrd.argus.ui.theme.TapTarget
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Narrowest a tile is allowed to be before the grid drops a column. A tablet held sideways fits
 * three or four across; a phone fits one, which is the whole width and looks right there.
 */
private val TileMinWidth = 320.dp

/**
 * Tile grid of every attached window, and the pick list of everything else that is open.
 *
 * Every tile subscribes at Preview quality - small and slow on purpose, because all of them stream
 * at once and the agent captures once per window however many people are watching.
 */
@Composable
fun DashboardScreen(container: AppContainer, onOpen: (Long) -> Unit) {
    val colors = LocalArgusColors.current
    val argus = container.argus
    val scope = rememberCoroutineScope()

    val statuses by argus.statuses.collectAsStateWithLifecycle()
    val windows by argus.windows.collectAsStateWithLifecycle()
    val connected by argus.connected.collectAsStateWithLifecycle()

    val tiles = remember(statuses) {
        statuses.values.sortedWith(compareBy({ it.processName }, { it.title }))
    }

    val available = remember(windows) {
        windows.filter { !it.selected }.sortedWith(compareBy({ it.processName }, { it.title }))
    }

    var command by rememberSaveable { mutableStateOf("") }
    var running by rememberSaveable { mutableStateOf(false) }
    var problem by rememberSaveable { mutableStateOf<String?>(null) }
    var killTarget by remember { mutableStateOf<AppRef?>(null) }
    var browsing by remember { mutableStateOf(false) }

    // Keep subscriptions in step with whatever is attached, including windows the watchdog
    // re-attaches under a new handle while this screen is open.
    val subscribed = remember { mutableSetOf<Long>() }
    val attachedHandles = remember(tiles) { tiles.map { it.handle } }

    LaunchedEffect(attachedHandles, connected) {
        if (!connected) {
            subscribed.clear()
            return@LaunchedEffect
        }

        for (handle in attachedHandles) {
            if (subscribed.add(handle)) argus.subscribe(handle, QualityLevel.Preview)
        }

        for (handle in subscribed.toList()) {
            if (handle !in attachedHandles) {
                subscribed.remove(handle)
                argus.unsubscribe(handle)
            }
        }
    }

    DisposableEffect(Unit) {
        onDispose {
            val leaving = subscribed.toList()
            subscribed.clear()
            container.scope.launch { for (handle in leaving) argus.unsubscribe(handle) }
        }
    }

    // Adaptive reflows on its own - one column on a phone, three or four across a tablet held
    // sideways - with no breakpoint list to keep in sync. Same 320 as the web build's grid, so a
    // tile is the same size on both.
    LazyVerticalGrid(
        columns = GridCells.Adaptive(TileMinWidth),
        modifier = Modifier
            .fillMaxSize()
            .windowInsetsPadding(WindowInsets.navigationBars),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item(span = { GridItemSpan(maxLineSpan) }) {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(
                    value = command,
                    onValueChange = { command = it },
                    label = { Text("Run Application") },
                    placeholder = { Text("chrome") },
                    singleLine = true,
                    colors = argusFieldColors(),
                    shape = RoundedCornerShape(6.dp),
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Text,
                        imeAction = ImeAction.Done,
                        autoCorrectEnabled = false,
                    ),
                    modifier = Modifier.fillMaxWidth(),
                )

                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        onClick = {
                            val wanted = command.trim()
                            if (wanted.isNotEmpty() && !running) {
                                running = true
                                problem = null
                                scope.launch {
                                    val result = argus.runApplication(wanted)
                                    running = false
                                    if (result.started) {
                                        command = ""
                                        // The window does not exist the instant the process does.
                                        delay(1_500)
                                        argus.refreshWindows()
                                    } else {
                                        problem = result.reason ?: "Could not run that."
                                    }
                                }
                            }
                        },
                        enabled = !running && command.isNotBlank(),
                        shape = RoundedCornerShape(6.dp),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = colors.accentDim,
                            contentColor = colors.text,
                        ),
                        modifier = Modifier.heightIn(min = TapTarget),
                    ) {
                        Text("Run")
                    }

                    OutlinedButton(
                        onClick = { browsing = true },
                        shape = RoundedCornerShape(6.dp),
                        modifier = Modifier.heightIn(min = TapTarget),
                    ) {
                        Text("Browse…", color = colors.text)
                    }

                    Box(Modifier.weight(1f))

                    OutlinedButton(
                        onClick = { scope.launch { argus.refreshWindows() } },
                        shape = RoundedCornerShape(6.dp),
                        modifier = Modifier.heightIn(min = TapTarget),
                    ) {
                        Text("Refresh", color = colors.text)
                    }
                }

                if (problem != null) {
                    Banner(problem.orEmpty(), BannerKind.Bad)
                }
            }
        }

        if (tiles.isEmpty()) {
            item(span = { GridItemSpan(maxLineSpan) }) {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(10.dp))
                        .background(colors.surface)
                        .border(1.dp, colors.border, RoundedCornerShape(10.dp))
                        .padding(24.dp),
                    verticalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    Text(
                        if (connected) "Nothing attached yet" else "Connecting to the agent…",
                        style = MaterialTheme.typography.titleMedium,
                        color = colors.text,
                    )
                    Text(
                        "Pick an app from the list below and it appears here as a live tile.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = colors.textMuted,
                    )
                }
            }
        }

        items(tiles, key = { it.handle }) { tile ->
            Tile(
                container = container,
                tile = tile,
                onOpen = { onOpen(tile.handle) },
                onClose = { scope.launch { problem = closeApp(container, tile.ref()) } },
                onKill = { killTarget = tile.ref() },
                onDetach = { scope.launch { argus.detach(tile.handle) } },
            )
        }

        item(span = { GridItemSpan(maxLineSpan) }) {
            Row(
                Modifier.fillMaxWidth().padding(top = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text(
                    "Available applications",
                    style = MaterialTheme.typography.titleMedium,
                    color = colors.text,
                )
                Text(
                    "${available.size} not being watched",
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.textMuted,
                )
            }
        }

        items(
            available,
            key = { it.matchKey },
            span = { GridItemSpan(maxLineSpan) },
        ) { window ->
            AvailableRow(
                window = window,
                onWatch = { scope.launch { argus.attach(window.handle) } },
                onClose = { scope.launch { problem = closeApp(container, window.ref()) } },
                onKill = { killTarget = window.ref() },
            )
        }

        if (available.isEmpty()) {
            item(span = { GridItemSpan(maxLineSpan) }) {
                Text(
                    "Every open window is already on the dashboard.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = colors.textMuted,
                )
            }
        }
    }

    killTarget?.let { target ->
        AlertDialog(
            onDismissRequest = { killTarget = null },
            containerColor = colors.surface,
            titleContentColor = colors.text,
            textContentColor = colors.textMuted,
            title = { Text("Force kill ${target.processName}?") },
            text = {
                Text(
                    "${target.title} is terminated immediately, along with any child processes. " +
                        "Anything unsaved in it is lost.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    killTarget = null
                    scope.launch {
                        problem = killApp(container, target)
                        argus.refreshWindows()
                    }
                }) {
                    Text("Force kill", color = colors.bad)
                }
            },
            dismissButton = {
                TextButton(onClick = { killTarget = null }) { Text("Cancel", color = colors.text) }
            },
        )
    }

    if (browsing) {
        HostPicker(
            container = container,
            runnableOnly = true,
            onDismiss = { browsing = false },
            onPick = { entry ->
                // Quoted because Program Files and friends contain spaces, and the server splits
                // the command into program and arguments on the first unquoted space.
                command = if (entry.path.contains(' ')) "\"${entry.path}\"" else entry.path
                problem = null
                browsing = false
            },
        )
    }
}

@Composable
private fun Tile(
    container: AppContainer,
    tile: WindowStatusUpdate,
    onOpen: () -> Unit,
    onClose: () -> Unit,
    onKill: () -> Unit,
    onDetach: () -> Unit,
) {
    val colors = LocalArgusColors.current

    Column(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(colors.surface)
            .border(1.dp, colors.border, RoundedCornerShape(10.dp)),
    ) {
        FrameView(
            argus = container.argus,
            handle = tile.handle,
            placeholder = placeholderFor(tile),
            contentScale = ContentScale.Fit,
            modifier = Modifier
                .fillMaxWidth()
                .aspectRatio(16f / 10f)
                .clickable(onClick = onOpen),
        )

        Row(
            Modifier.fillMaxWidth().padding(10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            StatusDot(statusColour(tile.status))

            Column(Modifier.weight(1f)) {
                Text(
                    tile.title,
                    style = MaterialTheme.typography.bodyLarge,
                    color = colors.text,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    buildString {
                        append(tile.processName)
                        append(" · ")
                        append(tile.status.label)
                        if (tile.status == WindowStatus.Streaming) append(" · ${tile.actualFps} fps")
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.textMuted,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }

        if (tile.note != null) {
            Text(
                tile.note.orEmpty(),
                style = MaterialTheme.typography.bodySmall,
                color = colors.textMuted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(horizontal = 10.dp),
            )
        }

        Row(
            Modifier.fillMaxWidth().padding(start = 6.dp, end = 6.dp, bottom = 6.dp),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            TextButton(onClick = onOpen) { Text("Open", color = colors.accent) }
            Box(Modifier.weight(1f))
            TextButton(onClick = onClose) { Text("Close", color = colors.text) }
            TextButton(onClick = onKill) { Text("Kill", color = colors.bad) }
            TextButton(onClick = onDetach) { Text("Unwatch", color = colors.textMuted) }
        }
    }
}

@Composable
private fun AvailableRow(
    window: WindowListItem,
    onWatch: () -> Unit,
    onClose: () -> Unit,
    onKill: () -> Unit,
) {
    val colors = LocalArgusColors.current

    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(colors.surface)
            .border(1.dp, colors.border, RoundedCornerShape(10.dp))
            .padding(horizontal = 10.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Column(
            Modifier
                .weight(1f)
                .clickable(onClick = onWatch)
                .padding(vertical = 6.dp),
        ) {
            Text(
                window.title,
                style = MaterialTheme.typography.bodyLarge,
                color = colors.text,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                "${window.processName} · ${window.width}×${window.height}",
                style = MaterialTheme.typography.bodySmall,
                color = colors.textMuted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        TextButton(onClick = onWatch) { Text("Watch", color = colors.accent) }
        TextButton(onClick = onClose) { Text("Close", color = colors.text) }
        TextButton(onClick = onKill) { Text("Kill", color = colors.bad) }
    }
}

/** The fields close and kill need; both lists on this page can supply them. */
data class AppRef(val handle: Long, val title: String, val processName: String)

private fun WindowStatusUpdate.ref() = AppRef(handle, title, processName)

private fun WindowListItem.ref() = AppRef(handle, title, processName)

/**
 * Polite close. No confirmation: the app gets to put up its own save prompt.
 *
 * Returns the problem to show, or null. No automatic re-read on success - WM_CLOSE is only posted,
 * so an app with unsaved work is still on screen with a prompt, and refreshing a second later would
 * put the row straight back and read as a bug.
 */
private suspend fun closeApp(container: AppContainer, window: AppRef): String? {
    val result = container.argus.closeWindow(window.handle)

    if (!result.closed) {
        if (!result.gone) return result.reason ?: "Could not close ${window.title}."
        forget(container, window.handle)
        container.argus.refreshWindows()
        return null
    }

    forget(container, window.handle)
    return null
}

private suspend fun killApp(container: AppContainer, window: AppRef): String? {
    val result = container.argus.killWindow(window.handle)

    if (!result.closed) {
        if (!result.gone) return result.reason ?: "Could not kill ${window.title}."
        forget(container, window.handle)
        return null
    }

    forget(container, window.handle)
    return null
}

/**
 * Takes a closed app off both lists at once.
 *
 * Detaching is not optional for a watched tile: a selection persists by process and title, so
 * without it the watchdog keeps the tile on the dashboard reading "Not running - waiting for it to
 * reopen", which is not what closing something is meant to look like.
 */
private suspend fun forget(container: AppContainer, handle: Long) {
    if (container.argus.statusFor(handle) != null) container.argus.detach(handle)
    container.argus.forgetWindow(handle)
}

private fun placeholderFor(tile: WindowStatusUpdate): String = when (tile.status) {
    WindowStatus.Closed -> "Not running - waiting for it to reopen"
    WindowStatus.Minimised -> "Minimised"
    WindowStatus.Stalled -> "Capture stalled"
    else -> "Waiting for frames…"
}

@Composable
private fun statusColour(status: WindowStatus) = with(LocalArgusColors.current) {
    when (status) {
        WindowStatus.Streaming -> ok
        WindowStatus.Starting -> accent
        WindowStatus.Stalled -> warn
        WindowStatus.Minimised -> idle
        WindowStatus.Closed -> bad
    }
}

/** The host-side file picker, shared by the dashboard's Browse and the Explorer page. */
@Composable
fun HostPicker(
    container: AppContainer,
    runnableOnly: Boolean,
    onDismiss: () -> Unit,
    onPick: (BrowseEntry) -> Unit,
) {
    val colors = LocalArgusColors.current
    val scope = rememberCoroutineScope()
    var listing by remember { mutableStateOf<BrowseListing?>(null) }

    LaunchedEffect(Unit) {
        listing = if (runnableOnly) container.argus.browse(null) else container.argus.explore(null)
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = colors.surface,
        titleContentColor = colors.text,
        textContentColor = colors.text,
        title = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    listing?.label ?: "Loading…",
                    style = MaterialTheme.typography.titleMedium,
                    color = colors.text,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                TextButton(
                    onClick = {
                        val parent = listing?.parent
                        scope.launch {
                            listing = if (runnableOnly) {
                                container.argus.browse(parent)
                            } else {
                                container.argus.explore(parent)
                            }
                        }
                    },
                ) {
                    Text("Up", color = colors.accent)
                }
            }
        },
        text = {
            val entries = listing?.entries.orEmpty()
            Column {
                listing?.error?.let { Banner(it, BannerKind.Bad) }

                if (entries.isEmpty()) {
                    Text("Nothing here.", color = colors.textMuted)
                } else {
                    LazyColumn(Modifier.heightIn(max = 420.dp)) {
                        items(entries, key = { it.path }) { entry ->
                            Row(
                                Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        if (entry.isDirectory) {
                                            scope.launch {
                                                listing = if (runnableOnly) {
                                                    container.argus.browse(entry.path)
                                                } else {
                                                    container.argus.explore(entry.path)
                                                }
                                            }
                                        } else {
                                            onPick(entry)
                                        }
                                    }
                                    .padding(vertical = 10.dp),
                                horizontalArrangement = Arrangement.spacedBy(10.dp),
                            ) {
                                Text(if (entry.isDirectory) "📁" else "⚙")
                                Text(
                                    entry.name,
                                    color = colors.text,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text("Close", color = colors.text) }
        },
    )
}
