package dev.mynrd.argus.ui.theme

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * The palette from Argus.Web/src/styles.scss, kept in the same order and with the same names.
 *
 * Dark only, deliberately: the web app is dark-first because this is a monitoring screen that sits
 * open on a phone at night, and light chrome around live screenshots is distracting. A DayNight
 * theme here would only produce a second look nobody asked for.
 */
@Immutable
data class ArgusColors(
    val bg: Color = Color(0xFF0B0E14),
    val surface: Color = Color(0xFF141922),
    val surfaceRaised: Color = Color(0xFF1C2330),
    val surfaceSunken: Color = Color(0xFF080A0F),
    val border: Color = Color(0xFF262E3D),
    val borderStrong: Color = Color(0xFF3A465C),
    val text: Color = Color(0xFFE6EDF3),
    val textMuted: Color = Color(0xFF8B96A8),
    val accent: Color = Color(0xFF4C9AFF),
    val accentDim: Color = Color(0xFF2C5DA8),
    val ok: Color = Color(0xFF3FB950),
    val warn: Color = Color(0xFFD29922),
    val bad: Color = Color(0xFFF85149),
    val idle: Color = Color(0xFF6E7681),
)

val LocalArgusColors = staticCompositionLocalOf { ArgusColors() }

/** --tap-target: 44px in the stylesheet. Anything touchable is at least this tall. */
val TapTarget = 44.dp

private val argusColors = ArgusColors()

private val colorScheme = darkColorScheme(
    primary = argusColors.accent,
    onPrimary = argusColors.bg,
    primaryContainer = argusColors.accentDim,
    onPrimaryContainer = argusColors.text,
    secondary = argusColors.accentDim,
    background = argusColors.bg,
    onBackground = argusColors.text,
    surface = argusColors.surface,
    onSurface = argusColors.text,
    surfaceVariant = argusColors.surfaceRaised,
    onSurfaceVariant = argusColors.textMuted,
    outline = argusColors.borderStrong,
    outlineVariant = argusColors.border,
    error = argusColors.bad,
    onError = argusColors.bg,
)

/** --radius: 10px, --radius-sm: 6px. */
private val shapes = Shapes(
    extraSmall = RoundedCornerShape(6.dp),
    small = RoundedCornerShape(6.dp),
    medium = RoundedCornerShape(10.dp),
    large = RoundedCornerShape(10.dp),
)

/** The web sets a 15px base; Material's 14sp body would read a shade smaller than the browser does. */
private val typography = Typography().let { base ->
    base.copy(
        bodyLarge = base.bodyLarge.copy(fontSize = 15.sp),
        bodyMedium = base.bodyMedium.copy(fontSize = 14.sp),
        labelLarge = base.labelLarge.copy(fontSize = 14.sp),
    )
}

/** Secondary text - the .muted class. */
@Composable
fun mutedStyle(base: TextStyle = MaterialTheme.typography.bodyMedium): TextStyle =
    base.copy(color = LocalArgusColors.current.textMuted)

@Composable
fun ArgusTheme(content: @Composable () -> Unit) {
    CompositionLocalProvider(LocalArgusColors provides argusColors) {
        MaterialTheme(
            colorScheme = colorScheme,
            shapes = shapes,
            typography = typography,
            content = content,
        )
    }
}
