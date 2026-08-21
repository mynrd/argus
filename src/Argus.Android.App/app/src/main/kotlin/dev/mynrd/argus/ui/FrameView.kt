package dev.mynrd.argus.ui

import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import dev.mynrd.argus.net.ArgusClient
import dev.mynrd.argus.ui.theme.LocalArgusColors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlin.math.min

/**
 * What the viewer needs to turn a finger position into a point on the host's window.
 *
 * `content` is the size the frame is drawn at with no zoom applied - the letterboxed fit inside the
 * stage. Everything else (clamping the pan, zooming about a point, deciding whether a tap landed on
 * the picture or on the black bars beside it) is derived from these four numbers.
 */
data class FrameMetrics(
    val containerW: Float = 0f,
    val containerH: Float = 0f,
    val contentW: Float = 0f,
    val contentH: Float = 0f,
) {
    val ready: Boolean get() = containerW > 0 && containerH > 0 && contentW > 0 && contentH > 0
}

/**
 * One window's live JPEG stream.
 *
 * Decoding happens off the main thread and the frame flow drops what it cannot keep up with, so a
 * tile that decodes slower than the agent sends shows fewer frames rather than falling further
 * behind on a queue that never drains.
 */
@Composable
fun FrameView(
    argus: ArgusClient,
    handle: Long,
    placeholder: String,
    modifier: Modifier = Modifier,
    contentScale: ContentScale = ContentScale.Fit,
    zoom: Float = 1f,
    panX: Float = 0f,
    panY: Float = 0f,
    onMetrics: (FrameMetrics) -> Unit = {},
) {
    val colors = LocalArgusColors.current
    var image by remember(handle) { mutableStateOf<ImageBitmap?>(null) }
    var containerW by remember { mutableStateOf(0f) }
    var containerH by remember { mutableStateOf(0f) }

    LaunchedEffect(handle) {
        argus.framesFor(handle).collect { frame ->
            val decoded = withContext(Dispatchers.Default) {
                runCatching { BitmapFactory.decodeByteArray(frame.jpeg, 0, frame.jpeg.size) }.getOrNull()
            }
            if (decoded != null) image = decoded.asImageBitmap()
        }
    }

    // A stale frame of a window that has gone is worse than an honest placeholder.
    DisposableEffect(handle) {
        onDispose { image = null }
    }

    val current = image

    // Contain, not cover: a 16:9 window on a phone screen gets bars, never a squashed picture.
    LaunchedEffect(current, containerW, containerH) {
        val bitmap = current
        if (bitmap == null || containerW <= 0f || containerH <= 0f) {
            onMetrics(FrameMetrics(containerW, containerH, 0f, 0f))
            return@LaunchedEffect
        }

        // Must match what is actually drawn, or every click lands in the wrong place and Fit does
        // nothing: ContentScale.Inside never enlarges a frame smaller than the stage, so neither
        // does this. Zooming past that is the viewer's job, not the layout's.
        val fit = min(containerW / bitmap.width, containerH / bitmap.height)
        val scale = if (contentScale == ContentScale.Inside) min(1f, fit) else fit
        onMetrics(
            FrameMetrics(
                containerW = containerW,
                containerH = containerH,
                contentW = bitmap.width * scale,
                contentH = bitmap.height * scale,
            ),
        )
    }

    Box(
        modifier
            .background(colors.surfaceSunken)
            .onSizeChanged {
                containerW = it.width.toFloat()
                containerH = it.height.toFloat()
            },
        contentAlignment = Alignment.Center,
    ) {
        if (current != null) {
            Image(
                bitmap = current,
                contentDescription = null,
                contentScale = contentScale,
                modifier = Modifier
                    .fillMaxSize()
                    // Translate then scale about the centre, matching the browser's transform, so
                    // the arithmetic the viewer does to place a click is the same on both.
                    .graphicsLayer(
                        scaleX = zoom,
                        scaleY = zoom,
                        translationX = panX,
                        translationY = panY,
                    ),
            )
        } else {
            Text(
                text = placeholder,
                style = MaterialTheme.typography.bodySmall,
                color = colors.textMuted,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(12.dp),
            )
        }
    }
}
