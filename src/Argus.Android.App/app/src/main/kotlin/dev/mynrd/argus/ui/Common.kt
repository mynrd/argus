package dev.mynrd.argus.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextFieldColors
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import dev.mynrd.argus.ui.theme.LocalArgusColors

/** The brand mark: an accent ring with a filled pupil, as the stylesheet draws it in CSS. */
@Composable
fun ArgusEye(size: Dp, modifier: Modifier = Modifier) {
    val accent = LocalArgusColors.current.accent

    Canvas(modifier.size(size)) {
        val stroke = this.size.minDimension / 7f
        val radius = (this.size.minDimension - stroke) / 2f

        drawCircle(color = accent, radius = radius, style = Stroke(width = stroke))
        drawCircle(color = accent, radius = this.size.minDimension / 5f)
    }
}

enum class BannerKind { Warn, Bad }

/** The .banner class: a tinted strip that states one problem. */
@Composable
fun Banner(text: String, kind: BannerKind, modifier: Modifier = Modifier) {
    val colors = LocalArgusColors.current
    val base = if (kind == BannerKind.Bad) colors.bad else colors.warn
    val foreground = if (kind == BannerKind.Bad) Color(0xFFFF9492) else Color(0xFFF0C674)

    Text(
        text = text,
        style = MaterialTheme.typography.bodySmall,
        color = foreground,
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(6.dp))
            .background(base.copy(alpha = 0.12f))
            .border(1.dp, base.copy(alpha = 0.4f), RoundedCornerShape(6.dp))
            .padding(horizontal = 12.dp, vertical = 10.dp),
    )
}

/** One dot of the status legend. Colour is the caller's call - it means different things per page. */
@Composable
fun StatusDot(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier.size(9.dp).clip(CircleShape)) { drawCircle(color) }
}

/** The .card block: a titled panel with an optional line of explanation under the heading. */
@Composable
fun SectionCard(
    title: String,
    subtitle: String? = null,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val colors = LocalArgusColors.current

    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(colors.surface)
            .border(1.dp, colors.border, RoundedCornerShape(10.dp))
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(title, style = MaterialTheme.typography.titleMedium, color = colors.text)
        if (subtitle != null) {
            Text(subtitle, style = MaterialTheme.typography.bodySmall, color = colors.textMuted)
        }
        content()
    }
}

/** Text fields sit on --surface-sunken, so they read as a hole rather than another raised panel. */
@Composable
fun argusFieldColors(): TextFieldColors {
    val colors = LocalArgusColors.current

    return OutlinedTextFieldDefaults.colors(
        focusedTextColor = colors.text,
        unfocusedTextColor = colors.text,
        focusedContainerColor = colors.surfaceSunken,
        unfocusedContainerColor = colors.surfaceSunken,
        disabledContainerColor = colors.surfaceSunken,
        cursorColor = colors.accent,
        focusedBorderColor = colors.accent,
        unfocusedBorderColor = colors.borderStrong,
        focusedLabelColor = colors.accent,
        unfocusedLabelColor = colors.textMuted,
        focusedPlaceholderColor = colors.textMuted,
        unfocusedPlaceholderColor = colors.textMuted,
    )
}
