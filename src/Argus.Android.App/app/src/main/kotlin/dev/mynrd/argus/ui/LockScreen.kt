package dev.mynrd.argus.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.mynrd.argus.AppContainer
import dev.mynrd.argus.ui.theme.LocalArgusColors
import dev.mynrd.argus.ui.theme.TapTarget

/**
 * The password screen, and the only place a first-run install can be pointed at an agent.
 *
 * It covers the shell completely rather than dimming it: whatever was on screen is a live view of
 * someone's desktop, and a translucent lock would still show it.
 */
@Composable
fun LockScreen(container: AppContainer) {
    val colors = LocalArgusColors.current
    val session = container.session

    val savedUrl by session.serverUrl.collectAsStateWithLifecycle()
    val required by session.required.collectAsStateWithLifecycle()
    val busy by session.unlocking.collectAsStateWithLifecycle()
    val error by session.unlockError.collectAsStateWithLifecycle()

    var url by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }
    var edited by rememberSaveable { mutableStateOf(false) }

    // The store is read asynchronously, so the saved address lands a frame or two after this first
    // composes. Filling it in then would wipe whatever the user had already started typing.
    LaunchedEffect(savedUrl) {
        if (!edited) url = savedUrl
    }

    // A wrong password leaves the box to be retyped, as the browser build does. A right one takes
    // this whole screen away, so there is nothing to clear.
    LaunchedEffect(error) {
        if (error != null) password = ""
    }

    val submit: () -> Unit = {
        if (!busy && url.isNotBlank()) session.requestUnlock(url, password)
    }

    Box(
        Modifier
            .fillMaxSize()
            .background(colors.bg)
            // Swallows anything that misses the panel, so the shell underneath stays untouchable.
            .selectable(
                selected = false,
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
                onClick = {},
            )
            .verticalScroll(rememberScrollState())
            .imePadding()
            .padding(24.dp),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            modifier = Modifier
                .widthIn(max = 384.dp)
                .fillMaxWidth()
                .clip(RoundedCornerShape(10.dp))
                .background(colors.surface)
                .border(1.dp, colors.border, RoundedCornerShape(10.dp))
                .padding(horizontal = 24.dp, vertical = 28.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            ArgusEye(26.dp)

            Text(
                text = if (savedUrl.isEmpty()) "Point Argus at an agent" else "Argus is locked",
                style = MaterialTheme.typography.titleMedium,
                color = colors.text,
                textAlign = TextAlign.Center,
            )

            Text(
                text = if (savedUrl.isEmpty()) {
                    "Type the address the agent prints when it starts, then the password."
                } else {
                    "Enter the password to reach this desktop."
                },
                style = MaterialTheme.typography.bodySmall,
                color = colors.textMuted,
                textAlign = TextAlign.Center,
            )

            OutlinedTextField(
                value = url,
                onValueChange = {
                    edited = true
                    url = it
                    session.clearUnlockError()
                },
                label = { Text("Agent address") },
                placeholder = { Text("100.84.12.3:5227") },
                singleLine = true,
                colors = argusFieldColors(),
                shape = RoundedCornerShape(6.dp),
                keyboardOptions = KeyboardOptions(
                    keyboardType = KeyboardType.Uri,
                    imeAction = ImeAction.Next,
                    autoCorrectEnabled = false,
                ),
                modifier = Modifier.fillMaxWidth(),
            )

            OutlinedTextField(
                value = password,
                onValueChange = {
                    password = it
                    session.clearUnlockError()
                },
                label = { Text("Password") },
                singleLine = true,
                visualTransformation = PasswordVisualTransformation(),
                colors = argusFieldColors(),
                shape = RoundedCornerShape(6.dp),
                keyboardOptions = KeyboardOptions(
                    keyboardType = KeyboardType.Password,
                    imeAction = ImeAction.Go,
                ),
                keyboardActions = KeyboardActions(onGo = { submit() }),
                modifier = Modifier.fillMaxWidth(),
            )

            // An agent with no password configured answers /api/unlock with an open session
            // whatever is typed, so an empty box is a valid answer there rather than a mistake.
            if (!required && savedUrl.isNotEmpty()) {
                Text(
                    text = "This agent has no password set - leave the box empty.",
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.textMuted,
                    textAlign = TextAlign.Center,
                )
            }

            if (error != null) {
                Banner(error.orEmpty(), BannerKind.Bad)
            }

            Button(
                onClick = submit,
                enabled = !busy && url.isNotBlank(),
                shape = RoundedCornerShape(6.dp),
                colors = ButtonDefaults.buttonColors(
                    containerColor = colors.accentDim,
                    contentColor = colors.text,
                ),
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = TapTarget),
            ) {
                Text(if (busy) "Checking…" else "Unlock")
            }
        }
    }
}
