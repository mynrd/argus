package dev.mynrd.argus

import android.app.Application
import android.content.Context
import dev.mynrd.argus.data.SettingsStore
import dev.mynrd.argus.net.ArgusApi
import dev.mynrd.argus.net.ArgusClient
import dev.mynrd.argus.session.SessionRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch

/**
 * Everything that outlives a screen, wired by hand.
 *
 * There are four objects here and one of each. A dependency-injection framework would add a build
 * step and an annotation processor to solve a problem this app does not have.
 */
class AppContainer(context: Context) {

    /** Outlives the activity on purpose: the idle clock has to keep running through a rotation. */
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    val settings = SettingsStore(context.applicationContext)
    val api = ArgusApi()
    val session = SessionRepository(api, settings, scope)
    val argus = ArgusClient(api.client, scope)

    init {
        // The connections follow the lock rather than the launch. Connecting while locked would be
        // pointless anyway - the server refuses the hub and the frame socket without a session.
        scope.launch {
            combine(session.unlocked, session.serverUrl) { unlocked, url -> unlocked to url }
                .collect { (unlocked, url) ->
                    if (unlocked && url.isNotEmpty()) argus.start(url) else argus.stop()
                }
        }
    }
}

class ArgusApplication : Application() {

    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        container = AppContainer(this)
    }
}
