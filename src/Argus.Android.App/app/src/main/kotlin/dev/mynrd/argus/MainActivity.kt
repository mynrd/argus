package dev.mynrd.argus

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.lifecycle.lifecycleScope
import dev.mynrd.argus.ui.ArgusRoot
import dev.mynrd.argus.ui.theme.ArgusTheme
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {

    private val container: AppContainer get() = (application as ArgusApplication).container

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Not optional from targetSdk 35 up - the system draws behind the bars whatever we ask for,
        // so the layout has to account for them rather than pretend they are not there.
        enableEdgeToEdge()

        setContent {
            ArgusTheme {
                ArgusRoot(container)
            }
        }
    }

    /**
     * The agent may have restarted, or the session may have aged out, while the app was in the
     * background. This is the visibilitychange handler the browser build has.
     */
    override fun onResume() {
        super.onResume()
        lifecycleScope.launch { container.session.refresh() }
    }
}
