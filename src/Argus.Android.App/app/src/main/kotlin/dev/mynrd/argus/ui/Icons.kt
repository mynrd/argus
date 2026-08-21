package dev.mynrd.argus.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import dev.mynrd.argus.ui.theme.LocalArgusColors

/**
 * The viewer's toolbar icons, drawn rather than imported.
 *
 * They are the same 24-unit stroked shapes the web build inlines as SVG, so the two toolbars read
 * as the same tool. Drawing them keeps the material-icons-extended artifact - several thousand
 * vectors for the nine used here - out of the APK.
 */
typealias IconPainter = DrawScope.(Color) -> Unit

/** Icon buttons are 36dp with an 18dp glyph, as in the stylesheet. */
private val ICON_BOX = 36.dp
private val ICON_GLYPH = 18.dp

/**
 * A toolbar button. `active` fills it, which is how a toggle shows its state without a label - the
 * same rule as `.icon.on` in the stylesheet.
 */
@Composable
fun ArgusIconButton(
    icon: IconPainter,
    contentDescription: String,
    modifier: Modifier = Modifier,
    active: Boolean = false,
    box: Dp = ICON_BOX,
    glyph: Dp = ICON_GLYPH,
    tint: Color? = null,
    onClick: () -> Unit,
) {
    val colors = LocalArgusColors.current

    Box(
        modifier
            .size(box)
            .clip(RoundedCornerShape(6.dp))
            .background(if (active) colors.accentDim else Color.Transparent)
            .border(
                width = 1.dp,
                color = if (active) colors.accent else Color.Transparent,
                shape = RoundedCornerShape(6.dp),
            )
            .clickable(onClick = onClick)
            .semantics { this.contentDescription = contentDescription },
        contentAlignment = Alignment.Center,
    ) {
        val colour = tint ?: if (active) colors.text else colors.textMuted
        Canvas(Modifier.size(glyph)) { icon(colour) }
    }
}

// ---------------------------------------------------------------- drawing helpers

/** Maps a coordinate in the 24-unit box the shapes are written in onto the canvas. */
private fun DrawScope.u(value: Float): Float = value / 24f * size.minDimension

private fun DrawScope.stroke(colour: Color, path: Path) {
    drawPath(
        path = path,
        color = colour,
        style = Stroke(width = u(2f), cap = StrokeCap.Round, join = StrokeJoin.Round),
    )
}

private fun DrawScope.line(colour: Color, x1: Float, y1: Float, x2: Float, y2: Float) {
    drawLine(
        color = colour,
        start = Offset(u(x1), u(y1)),
        end = Offset(u(x2), u(y2)),
        strokeWidth = u(2f),
        cap = StrokeCap.Round,
    )
}

private fun DrawScope.polyline(colour: Color, vararg points: Float) {
    val path = Path()
    for (i in points.indices step 2) {
        if (i == 0) path.moveTo(u(points[0]), u(points[1])) else path.lineTo(u(points[i]), u(points[i + 1]))
    }
    stroke(colour, path)
}

private fun DrawScope.roundedRect(colour: Color, x: Float, y: Float, w: Float, h: Float, r: Float) {
    val path = Path().apply {
        addRoundRect(
            androidx.compose.ui.geometry.RoundRect(
                left = u(x),
                top = u(y),
                right = u(x + w),
                bottom = u(y + h),
                radiusX = u(r),
                radiusY = u(r),
            ),
        )
    }
    stroke(colour, path)
}

// ---------------------------------------------------------------------- icons

/** Back to the dashboard. */
val IconBack: IconPainter = { c -> polyline(c, 15f, 4f, 7f, 12f, 15f, 20f) }

/** Quality - three bars of different heights. */
val IconQuality: IconPainter = { c ->
    line(c, 6f, 20f, 6f, 14f)
    line(c, 12f, 20f, 12f, 4f)
    line(c, 18f, 20f, 18f, 10f)
}

/** Window size - a monitor on a stand. */
val IconSize: IconPainter = { c ->
    roundedRect(c, 2f, 3f, 20f, 14f, 2f)
    line(c, 8f, 21f, 16f, 21f)
    line(c, 12f, 17f, 12f, 21f)
}

/** Reset zoom - an arrow curling back on itself. */
val IconReset: IconPainter = { c ->
    polyline(c, 1f, 4f, 1f, 10f, 7f, 10f)
    val path = Path().apply {
        arcTo(
            rect = Rect(u(3f), u(3f), u(21f), u(21f)),
            startAngleDegrees = 200f,
            sweepAngleDegrees = -290f,
            forceMoveTo = true,
        )
    }
    stroke(c, path)
}

/** Fit to screen - two arrows pulling to opposite corners. */
val IconFit: IconPainter = { c ->
    polyline(c, 4f, 14f, 10f, 14f, 10f, 20f)
    polyline(c, 20f, 10f, 14f, 10f, 14f, 4f)
    line(c, 14f, 10f, 21f, 3f)
    line(c, 3f, 21f, 10f, 14f)
}

/** Scroll buttons toggle - a chevron each way. */
val IconScroll: IconPainter = { c ->
    polyline(c, 8f, 9f, 12f, 5f, 16f, 9f)
    polyline(c, 8f, 15f, 12f, 19f, 16f, 15f)
}

/** Key pad toggle. */
val IconKeyPad: IconPainter = { c ->
    roundedRect(c, 2f, 6f, 20f, 12f, 2f)
    for (x in listOf(6f, 10f, 14f, 18f)) line(c, x, 10f, x + 0.01f, 10f)
    line(c, 8f, 14f, 16f, 14f)
}

/** What a one-finger drag means - the four-way move arrows, as in the web build. */
val IconDrag: IconPainter = { c ->
    polyline(c, 5f, 9f, 2f, 12f, 5f, 15f)
    polyline(c, 9f, 5f, 12f, 2f, 15f, 5f)
    polyline(c, 15f, 19f, 12f, 22f, 9f, 19f)
    polyline(c, 19f, 9f, 22f, 12f, 19f, 15f)
    line(c, 2f, 12f, 22f, 12f)
    line(c, 12f, 2f, 12f, 22f)
}

val IconFullscreenEnter: IconPainter = { c ->
    polyline(c, 8f, 3f, 5f, 3f, 3f, 5f, 3f, 8f)
    polyline(c, 21f, 8f, 21f, 5f, 19f, 3f, 16f, 3f)
    polyline(c, 3f, 16f, 3f, 19f, 5f, 21f, 8f, 21f)
    polyline(c, 16f, 21f, 19f, 21f, 21f, 19f, 21f, 16f)
}

val IconFullscreenExit: IconPainter = { c ->
    polyline(c, 3f, 8f, 6f, 8f, 8f, 6f, 8f, 3f)
    polyline(c, 16f, 3f, 16f, 6f, 18f, 8f, 21f, 8f)
    polyline(c, 8f, 21f, 8f, 18f, 6f, 16f, 3f, 16f)
    polyline(c, 21f, 16f, 18f, 16f, 16f, 18f, 16f, 21f)
}

val IconChevronUp: IconPainter = { c -> polyline(c, 6f, 15f, 12f, 9f, 18f, 15f) }

val IconChevronDown: IconPainter = { c -> polyline(c, 6f, 9f, 12f, 15f, 18f, 9f) }

/** The key pad's drag handle - six dots, the ⠿ the web build uses as text. */
val IconGrip: IconPainter = { c ->
    for (x in listOf(9f, 15f)) {
        for (y in listOf(6f, 12f, 18f)) {
            drawCircle(color = c, radius = u(1.4f), center = Offset(u(x), u(y)))
        }
    }
}

val IconMinimize: IconPainter = { c -> line(c, 5f, 12f, 19f, 12f) }

val IconClose: IconPainter = { c ->
    line(c, 6f, 6f, 18f, 18f)
    line(c, 18f, 6f, 6f, 18f)
}
