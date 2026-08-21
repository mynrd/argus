package dev.mynrd.argus.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.mynrd.argus.AppContainer
import dev.mynrd.argus.net.BrowseEntry
import dev.mynrd.argus.net.BrowseListing
import dev.mynrd.argus.net.OpenWithApp
import dev.mynrd.argus.ui.theme.LocalArgusColors
import dev.mynrd.argus.ui.theme.TapTarget
import kotlinx.coroutines.launch

/**
 * Files on the watched PC, and a way to hand one to an app over there.
 *
 * The listing has to come from the agent: a file picker on the phone would list the phone's own
 * files and hand back a path the host has never heard of.
 */
@Composable
fun ExplorerScreen(container: AppContainer) {
    val colors = LocalArgusColors.current
    val argus = container.argus
    val scope = rememberCoroutineScope()

    val connected by argus.connected.collectAsStateWithLifecycle()

    var listing by remember { mutableStateOf<BrowseListing?>(null) }
    var loading by remember { mutableStateOf(false) }
    var openTarget by remember { mutableStateOf<BrowseEntry?>(null) }
    var apps by remember { mutableStateOf<List<OpenWithApp>>(emptyList()) }
    var problem by remember { mutableStateOf<String?>(null) }

    fun load(path: String?) {
        loading = true
        scope.launch {
            listing = argus.explore(path)
            loading = false
        }
    }

    LaunchedEffect(connected) {
        if (connected) {
            listing = argus.explore(null)
            apps = argus.openWithApps()
        }
    }

    Column(
        Modifier
            .fillMaxSize()
            .windowInsetsPadding(WindowInsets.navigationBars),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("Explorer", style = MaterialTheme.typography.headlineSmall, color = colors.text)
            Text(
                "Files on the watched PC. Open one in an app over there.",
                style = MaterialTheme.typography.bodySmall,
                color = colors.textMuted,
            )
        }

        Row(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            OutlinedButton(
                onClick = { load(listing?.parent) },
                enabled = listing?.parent != null,
                shape = RoundedCornerShape(6.dp),
                modifier = Modifier.heightIn(min = TapTarget),
            ) {
                Text("↑ Up", color = colors.text)
            }

            Text(
                listing?.label ?: if (connected) "Loading…" else "Waiting for the agent…",
                style = MaterialTheme.typography.bodyMedium,
                color = colors.textMuted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )

            OutlinedButton(
                onClick = { load(listing?.path?.ifEmpty { null }) },
                shape = RoundedCornerShape(6.dp),
                modifier = Modifier.heightIn(min = TapTarget),
            ) {
                Text("Refresh", color = colors.text)
            }
        }

        problem?.let {
            Banner(it, BannerKind.Bad, Modifier.padding(16.dp))
        }

        listing?.error?.let {
            Banner(it, BannerKind.Bad, Modifier.padding(16.dp))
        }

        LazyColumn(
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            items(listing?.entries.orEmpty(), key = { it.path }) { entry ->
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(10.dp))
                        .background(colors.surface)
                        .border(1.dp, colors.border, RoundedCornerShape(10.dp))
                        .padding(horizontal = 12.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    Text(if (entry.isDirectory) "📁" else "📄")

                    Text(
                        entry.name,
                        style = MaterialTheme.typography.bodyLarge,
                        color = colors.text,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier
                            .weight(1f)
                            .clickable(enabled = entry.isDirectory) { load(entry.path) }
                            .padding(vertical = 12.dp),
                    )

                    TextButton(onClick = { openTarget = entry }) {
                        Text("Open with", color = colors.accent)
                    }
                }
            }

            if (listing != null && listing?.entries.orEmpty().isEmpty() && !loading) {
                item {
                    Text("Nothing here.", color = colors.textMuted)
                }
            }
        }
    }

    openTarget?.let { entry ->
        val usable = apps.filter { if (entry.isDirectory) it.handlesFolders else it.handlesFiles }

        AlertDialog(
            onDismissRequest = { openTarget = null },
            containerColor = colors.surface,
            titleContentColor = colors.text,
            textContentColor = colors.text,
            title = { Text("Open ${entry.name}") },
            text = {
                Column {
                    // Null asks the host for the file's own association, which is what a
                    // double-click over there would do.
                    OpenRow("Default app") {
                        openTarget = null
                        scope.launch {
                            val result = argus.openWith(entry.path, null)
                            problem = if (result.started) null else result.reason
                        }
                    }

                    for (app in usable) {
                        OpenRow(app.label) {
                            openTarget = null
                            scope.launch {
                                val result = argus.openWith(entry.path, app.key)
                                problem = if (result.started) null else result.reason
                            }
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { openTarget = null }) { Text("Cancel", color = colors.text) }
            },
        )
    }
}

@Composable
private fun OpenRow(label: String, onClick: () -> Unit) {
    val colors = LocalArgusColors.current

    Box(
        Modifier
            .fillMaxWidth()
            .heightIn(min = TapTarget)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.CenterStart,
    ) {
        Text(label, style = MaterialTheme.typography.bodyLarge, color = colors.text)
    }
}
