package dev.mynrd.argus.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.mynrd.argus.AppContainer
import dev.mynrd.argus.BuildConfig
import dev.mynrd.argus.data.ArgusSettings
import dev.mynrd.argus.data.SettingsStore
import dev.mynrd.argus.net.ServerUrl
import dev.mynrd.argus.ui.theme.LocalArgusColors
import dev.mynrd.argus.ui.theme.TapTarget
import kotlinx.coroutines.launch

@Composable
fun SettingsScreen(container: AppContainer) {
    val colors = LocalArgusColors.current
    val session = container.session
    val store = container.settings
    val scope = rememberCoroutineScope()

    val settings by store.settings.collectAsStateWithLifecycle(initialValue = ArgusSettings())
    val required by session.required.collectAsStateWithLifecycle()
    val reachable by session.reachable.collectAsStateWithLifecycle()

    var urlText by rememberSaveable { mutableStateOf("") }
    var urlEdited by rememberSaveable { mutableStateOf(false) }
    var urlError by rememberSaveable { mutableStateOf<String?>(null) }
    var saving by rememberSaveable { mutableStateOf(false) }

    LaunchedEffect(settings.serverUrl) {
        if (!urlEdited) urlText = settings.serverUrl
    }

    val normalised = ServerUrl.normalise(urlText)
    val urlChanged = normalised != null && normalised != settings.serverUrl

    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .windowInsetsPadding(WindowInsets.navigationBars)
            .imePadding()
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Text("Settings", style = MaterialTheme.typography.headlineSmall, color = colors.text)

        SectionCard(
            title = "Agent",
            subtitle = "Where Argus is running. The agent prints this address when it starts. " +
                "It is remembered on this phone only.",
        ) {
            OutlinedTextField(
                value = urlText,
                onValueChange = {
                    urlEdited = true
                    urlText = it
                    urlError = null
                },
                label = { Text("Agent address") },
                placeholder = { Text("100.84.12.3:5227") },
                singleLine = true,
                colors = argusFieldColors(),
                shape = RoundedCornerShape(6.dp),
                keyboardOptions = KeyboardOptions(
                    keyboardType = KeyboardType.Uri,
                    imeAction = ImeAction.Done,
                    autoCorrectEnabled = false,
                ),
                modifier = Modifier.fillMaxWidth(),
            )

            Text(
                text = when {
                    settings.serverUrl.isEmpty() -> "No agent set."
                    reachable == true -> "Reachable: ${settings.serverUrl}"
                    reachable == false -> "Not answering: ${settings.serverUrl}"
                    else -> settings.serverUrl
                },
                style = MaterialTheme.typography.bodySmall,
                color = if (reachable == false) colors.bad else colors.textMuted,
            )

            if (urlError != null) {
                Banner(urlError.orEmpty(), BannerKind.Bad)
            }

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = {
                        saving = true
                        scope.launch {
                            // Changing the address drops the session with the old one, so this
                            // lands back on the lock screen unless the new agent has no password.
                            urlError = session.setServerUrl(urlText)
                            if (urlError == null) urlEdited = false
                            saving = false
                        }
                    },
                    enabled = !saving && urlChanged,
                    shape = RoundedCornerShape(6.dp),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = colors.accentDim,
                        contentColor = colors.text,
                    ),
                    modifier = Modifier.heightIn(min = TapTarget),
                ) {
                    Text("Save")
                }

                OutlinedButton(
                    onClick = { scope.launch { session.refresh() } },
                    enabled = settings.serverUrl.isNotEmpty(),
                    shape = RoundedCornerShape(6.dp),
                    modifier = Modifier.heightIn(min = TapTarget),
                ) {
                    Text("Test", color = colors.text)
                }
            }
        }

        SectionCard(
            title = "Security",
            subtitle = "The password is set on the agent, in appsettings.json or the ARGUS_PASSWORD " +
                "environment variable. These settings belong to this phone only.",
        ) {
            if (reachable == true && !required) {
                Banner(
                    "The agent has no password configured, so nothing is locked. Anyone who can " +
                        "reach its port can drive this desktop.",
                    BannerKind.Warn,
                )
            }

            ToggleRow(
                name = "Auto Lock",
                hint = "Locks this app after a spell with no typing or pointing.",
                checked = settings.autoLock,
                onChange = { scope.launch { store.setAutoLock(it) } },
            )

            IdleMinutesRow(
                minutes = settings.idleMinutes,
                enabled = settings.autoLock,
                onChange = { scope.launch { store.setIdleMinutes(it) } },
            )

            OutlinedButton(
                onClick = { scope.launch { session.lock() } },
                enabled = required,
                shape = RoundedCornerShape(6.dp),
                modifier = Modifier.heightIn(min = TapTarget),
            ) {
                Text("Lock now", color = colors.text)
            }
        }

        SectionCard(
            title = "Viewer",
            subtitle = "Overlays on the full-screen view. Both cover part of the stream, so they " +
                "are a per-device call.",
        ) {
            ToggleRow(
                name = "Scroll buttons",
                hint = "A pair of buttons that scroll the app you are watching.",
                checked = settings.showScrollButtons,
                onChange = { scope.launch { store.setShowScrollButtons(it) } },
            )

            ToggleRow(
                name = "Key pad",
                hint = "Ctrl, Alt, arrows and the function keys, which a phone keyboard has not got.",
                checked = settings.showKeyPad,
                onChange = { scope.launch { store.setShowKeyPad(it) } },
            )
        }

        SectionCard(
            title = "About",
            subtitle = "Every build gets its own number, so an APK on a phone can be matched to " +
                "the code it came from.",
        ) {
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text("Version", style = MaterialTheme.typography.bodyLarge, color = colors.text)
                Text(
                    "${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})",
                    style = MaterialTheme.typography.bodyLarge,
                    color = colors.textMuted,
                )
            }
        }
    }
}

@Composable
private fun ToggleRow(name: String, hint: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    val colors = LocalArgusColors.current

    Row(
        Modifier.fillMaxWidth().heightIn(min = TapTarget),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Column(Modifier.weight(1f)) {
            Text(name, style = MaterialTheme.typography.bodyLarge, color = colors.text)
            Text(hint, style = MaterialTheme.typography.bodySmall, color = colors.textMuted)
        }

        Switch(
            checked = checked,
            onCheckedChange = onChange,
            colors = SwitchDefaults.colors(
                checkedThumbColor = colors.text,
                checkedTrackColor = colors.accentDim,
                checkedBorderColor = colors.accent,
                uncheckedThumbColor = colors.textMuted,
                uncheckedTrackColor = colors.surfaceSunken,
                uncheckedBorderColor = colors.borderStrong,
            ),
        )
    }
}

@Composable
private fun IdleMinutesRow(minutes: Int, enabled: Boolean, onChange: (Int) -> Unit) {
    val colors = LocalArgusColors.current
    var text by rememberSaveable { mutableStateOf(minutes.toString()) }

    LaunchedEffect(minutes) {
        if (text.toIntOrNull() != minutes) text = minutes.toString()
    }

    Row(
        Modifier.fillMaxWidth().heightIn(min = TapTarget),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Column(Modifier.weight(1f)) {
            Text(
                "Idle before locking",
                style = MaterialTheme.typography.bodyLarge,
                color = if (enabled) colors.text else colors.textMuted,
            )
            Text(
                "Between ${SettingsStore.IdleRange.first} and ${SettingsStore.IdleRange.last} minutes.",
                style = MaterialTheme.typography.bodySmall,
                color = colors.textMuted,
            )
        }

        OutlinedTextField(
            value = text,
            onValueChange = { raw ->
                text = raw.filter { it.isDigit() }.take(3)
                // Written through only once it is a number in range; the box keeps whatever was
                // typed until then, so backspacing to empty does not save a 1.
                text.toIntOrNull()?.let { if (it in SettingsStore.IdleRange) onChange(it) }
            },
            enabled = enabled,
            singleLine = true,
            suffix = { Text("min", color = colors.textMuted) },
            colors = argusFieldColors(),
            shape = RoundedCornerShape(6.dp),
            keyboardOptions = KeyboardOptions(
                keyboardType = KeyboardType.Number,
                imeAction = ImeAction.Done,
            ),
            modifier = Modifier.width(124.dp),
        )
    }
}
