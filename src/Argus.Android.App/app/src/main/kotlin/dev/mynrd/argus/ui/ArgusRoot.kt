package dev.mynrd.argus.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavHostController
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import dev.mynrd.argus.AppContainer
import dev.mynrd.argus.ui.theme.LocalArgusColors
import dev.mynrd.argus.ui.theme.TapTarget
import kotlinx.coroutines.launch

private const val ROUTE_DASHBOARD = "dashboard"
private const val ROUTE_EXPLORER = "explorer"
private const val ROUTE_SETTINGS = "settings"
private const val ROUTE_VIEW = "view"


/** Below this the tabs move to a row of their own and the agent wording drops. */
private const val NARROW_DP = 560

@Composable
fun ArgusRoot(container: AppContainer) {
    val colors = LocalArgusColors.current
    val session = container.session
    val scope = rememberCoroutineScope()

    val locked by session.locked.collectAsStateWithLifecycle()
    val required by session.required.collectAsStateWithLifecycle()
    val reachable by session.reachable.collectAsStateWithLifecycle()
    val sessionError by session.lastError.collectAsStateWithLifecycle()

    // Once the hub is up its heartbeat is the better answer: it says the agent is alive, not just
    // that something answered an HTTP call two minutes ago.
    val agentOnline by container.argus.agentOnline.collectAsStateWithLifecycle()
    val connected by container.argus.connected.collectAsStateWithLifecycle()
    val hubError by container.argus.lastError.collectAsStateWithLifecycle()

    val online = if (connected) agentOnline else reachable
    val lastError = hubError ?: sessionError

    val navController = rememberNavController()

    // The viewer is a live desktop on a phone screen; the shell's chrome above it is the first
    // thing worth giving back. It has a Back button of its own, so nothing becomes unreachable.
    val backStack by navController.currentBackStackEntryAsState()
    val onViewer = backStack?.destination?.route?.startsWith(ROUTE_VIEW) == true

    Box(
        Modifier
            .fillMaxSize()
            .background(colors.bg)
            // Capture pass: a touch counts as the user being here even when a child consumes it,
            // which is the whole point of the idle lock.
            .pointerInput(Unit) {
                awaitPointerEventScope {
                    while (true) {
                        awaitPointerEvent(PointerEventPass.Initial)
                        session.noteActivity()
                    }
                }
            },
    ) {
        Column(Modifier.fillMaxSize()) {
            if (!onViewer) {
                TopBar(
                    navController = navController,
                    reachable = online,
                    showLock = required,
                    onLock = { scope.launch { session.lock() } },
                )
            }

            if (lastError != null && !locked && !onViewer) {
                Banner(
                    text = lastError.orEmpty(),
                    kind = BannerKind.Bad,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                )
            }

            NavHost(
                navController = navController,
                startDestination = ROUTE_DASHBOARD,
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
            ) {
                composable(ROUTE_DASHBOARD) {
                    DashboardScreen(container) { handle ->
                        navController.navigate("$ROUTE_VIEW/$handle")
                    }
                }
                composable(ROUTE_EXPLORER) {
                    ExplorerScreen(container)
                }
                composable(ROUTE_SETTINGS) {
                    SettingsScreen(container)
                }
                composable(
                    route = "$ROUTE_VIEW/{handle}",
                    arguments = listOf(navArgument("handle") { type = NavType.LongType }),
                ) { entry ->
                    ViewerScreen(
                        container = container,
                        handle = entry.arguments?.getLong("handle") ?: 0L,
                        onBack = { navController.popBackStack() },
                    )
                }
            }
        }

        // Drawn over the shell rather than routed to, so unlocking puts the user back on the page
        // they were already looking at instead of bouncing them to the dashboard.
        if (locked) {
            LockScreen(container)
        }
    }
}

@Composable
private fun TopBar(
    navController: NavHostController,
    reachable: Boolean?,
    showLock: Boolean,
    onLock: () -> Unit,
) {
    val colors = LocalArgusColors.current
    val backStack by navController.currentBackStackEntryAsState()
    val current = backStack?.destination?.route ?: ROUTE_DASHBOARD

    BoxWithConstraints(
        Modifier
            .fillMaxWidth()
            .background(colors.surface)
            .windowInsetsPadding(WindowInsets.statusBars),
    ) {
        // Brand, three tabs, the agent state and Lock do not fit across a phone. Rather than shrink
        // all of them, the tabs drop to their own row below - they are the only part that is a list.
        val narrow = maxWidth.value < NARROW_DP

        val tabs: @Composable RowScope.() -> Unit = {
            Tab("Dashboard", current == ROUTE_DASHBOARD, narrow) { navigate(navController, ROUTE_DASHBOARD) }
            Tab("Explorer", current == ROUTE_EXPLORER, narrow) { navigate(navController, ROUTE_EXPLORER) }
            Tab("Settings", current == ROUTE_SETTINGS, narrow) { navigate(navController, ROUTE_SETTINGS) }
        }

        Column {
            Row(
                Modifier
                    .fillMaxWidth()
                    .heightIn(min = TapTarget)
                    .padding(horizontal = if (narrow) 10.dp else 16.dp, vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(if (narrow) 8.dp else 16.dp),
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    modifier = Modifier.clickable { navigate(navController, ROUTE_DASHBOARD) },
                ) {
                    ArgusEye(14.dp)
                    Text(
                        "Argus",
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.SemiBold,
                        color = colors.text,
                    )
                }

                Box(Modifier.weight(1f))

                if (!narrow) tabs()

                AgentStatus(reachable, narrow)

                if (showLock) {
                    Text(
                        text = "Lock",
                        style = MaterialTheme.typography.labelLarge,
                        color = colors.text,
                        modifier = Modifier
                            .clip(RoundedCornerShape(6.dp))
                            .background(colors.surfaceRaised)
                            .clickable { onLock() }
                            .padding(horizontal = 10.dp, vertical = 8.dp),
                    )
                }
            }

            if (narrow) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 10.dp)
                        .padding(bottom = 6.dp),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    content = tabs,
                )
            }

            Box(Modifier.fillMaxWidth().height(1.dp).background(colors.border))
        }
    }
}

@Composable
private fun RowScope.Tab(label: String, active: Boolean, fill: Boolean, onClick: () -> Unit) {
    val colors = LocalArgusColors.current

    Text(
        text = label,
        style = MaterialTheme.typography.labelLarge,
        color = if (active) colors.text else colors.textMuted,
        textAlign = TextAlign.Center,
        modifier = Modifier
            .then(if (fill) Modifier.weight(1f) else Modifier)
            .clip(RoundedCornerShape(6.dp))
            .background(if (active) colors.surfaceRaised else colors.surface)
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 8.dp),
    )
}

/**
 * Until the hub exists this reports whether the last /api/session call got an answer, which is a
 * weaker claim than the browser's heartbeat and is worded accordingly.
 */
@Composable
private fun AgentStatus(reachable: Boolean?, narrow: Boolean) {
    val colors = LocalArgusColors.current

    val (dot, label) = when (reachable) {
        true -> colors.ok to "Agent online"
        false -> colors.bad to "Agent offline"
        null -> colors.idle to "No agent set"
    }

    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        StatusDot(dot)
        if (!narrow) {
            Text(label, style = MaterialTheme.typography.bodySmall, color = colors.textMuted)
        }
    }
}

private fun navigate(navController: NavHostController, route: String) {
    if (navController.currentDestination?.route == route) return

    navController.navigate(route) {
        popUpTo(ROUTE_DASHBOARD) { inclusive = route == ROUTE_DASHBOARD }
        launchSingleTop = true
    }
}
