# Argus for Android

The phone client. Same job as `Argus.Web`, same palette, with one thing the browser build never
needed: it has to be told which agent to talk to, and remember it.

Kotlin and Jetpack Compose, built and installed entirely from a terminal. Android Studio is only an
IDE around Gradle and the SDK, and neither of those needs it.

---

## What you need

| Piece | Version used here | Where it comes from |
|-------|-------------------|---------------------|
| JDK | **21** | Any build; the daemon is pinned to 21 (see below) |
| Android SDK | platform `android-37`, platform-tools | `%LOCALAPPDATA%\Android\Sdk` |
| Gradle | 9.5 | The wrapper downloads it - do not install Gradle |
| Node, .NET | not needed | those are for the agent, not the app |

The SDK platform is the only piece that has to exist before the first build, and even that is
usually not true: AGP downloads a missing platform itself as long as the SDK licences have been
accepted once (`%LOCALAPPDATA%\Android\Sdk\licenses` is not empty). `android-37` arrived that way.

### The JDK pin

`gradle/gradle-daemon-jvm.properties` holds one line:

```properties
toolchainVersion=21
```

Gradle finds an installed JDK 21 by itself, so nothing needs to be on `PATH` and `JAVA_HOME` can
point anywhere. This is not decoration - AGP does not support JDK 25, and Gradle refuses to start
on it at all, so a machine whose `java` is 25 fails before it compiles a line.

### local.properties

Machine-local, git-ignored, one line:

```properties
sdk.dir=C:/Users/you/AppData/Local/Android/Sdk
```

**Forward slashes.** A Java properties file reads `\U` as an escape, so `C:\Users\...` silently
becomes `C:Users...` and the build dies with `The filename, directory name, or volume label syntax
is incorrect`, which names neither the file nor the setting.

---

## Build a debug APK

```powershell
cd src\Argus.Android.App
.\gradlew assembleDebug
```

Output:

```
app\build\outputs\apk\debug\app-debug.apk     the build's own output, always this name
dist\argus-0.2.4-debug.apk                      a keeping copy, named for its version
```

About 33 MB. It is signed with the debug keystore Android generates for you
(`~/.android/debug.keystore`), which is enough to install on any phone or emulator - it is not
enough for Play, and it marks the app debuggable.

First run downloads AGP, Kotlin and the AndroidX libraries and takes a few minutes. After that an
incremental build is a couple of seconds.

### Versions

Every `assembleDebug` or `assembleRelease` produces a new version. The build says what it made:

```
Built 0.2.4; next build is 0.2.5
```

`version.properties` at the project root is the whole mechanism:

```properties
major=0
minor=2
build=4
```

`versionName` is `major.minor.build`, `versionCode` is `build`. The `bumpVersion` task increments
`build` **after** a build finishes, so the APK just produced keeps the number that was on disk when
the build started and the next one gets the next number. Major and minor are yours to edit by hand;
nothing touches them.

Two things follow:

- `dist/` accumulates every APK built, each under its own name, so the one on a phone can be
  matched back to the code it came from. It is git-ignored.
- The version is on the **Settings** screen under About, read from `BuildConfig`, so you can check
  what a phone is running without a cable.

Commit `version.properties` after a build; that is what keeps the numbering monotonic across
machines. Skip it and two machines hand out the same build number.


Other useful targets:

```powershell
.\gradlew assembleRelease     # unsigned unless a keystore is configured - see below
.\gradlew installDebug        # build and push to the one connected device
.\gradlew clean               # when a build starts lying to you
.\gradlew --stop              # kill the daemon (after changing the JDK pin, say)
```

---

## Put it on something

### Emulator

```powershell
emulator -list-avds
emulator -avd Pixel_9a
adb install -r app\build\outputs\apk\debug\app-debug.apk
adb shell am start -n dev.mynrd.argus/.MainActivity
```

An emulator on the same machine as the agent reaches it at **`10.0.2.2:5227`** - that address is
the emulator's route to the host's loopback, which is one of the two things Argus binds. A tailnet
address works too, because the emulator's traffic leaves through this machine's network stack.

If the emulator feels slow, raise `hw.ramSize` in `~/.android/avd/<name>.avd/config.ini` - the
default 2048 is tight for a Compose app decoding JPEGs.

### A real phone, over USB

Enable **Developer options** (tap Build number seven times), then **USB debugging**. Plug it in,
accept the prompt on the phone, then:

```powershell
adb devices                                       # confirm it is listed as "device", not "unauthorized"
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

### A real phone, over wifi

Developer options → **Wireless debugging** → *Pair device with pairing code*:

```powershell
adb pair 192.168.1.50:37113        # the phone shows the host:port and a code
adb connect 192.168.1.50:41234     # the other port on the Wireless debugging screen
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

### Or just copy the file

The APK is a normal file. Send it to the phone however you like and tap it; Android asks for
permission to install from that app the first time.

A phone reaches the agent over **Tailscale** and nothing else - Argus binds loopback plus its
tailnet address only. Install Tailscale on the phone and log in to the same tailnet, or the app
will sit on "Cannot reach the Argus agent".

---

## Watch it run

```powershell
adb logcat -s AndroidRuntime:E              # crashes only
adb logcat --pid=$(adb shell pidof dev.mynrd.argus)
adb shell am force-stop dev.mynrd.argus     # restart it clean
adb shell pm clear dev.mynrd.argus          # forget the saved address and settings
adb exec-out screencap -p > shot.png        # screenshot
adb uninstall dev.mynrd.argus
```

---

## A signed release APK

Only worth doing if you want a smaller, non-debuggable build to keep. The debug APK is the same
app.

**1. Make a keystore** (once - back it up; losing it means a new app identity):

```powershell
keytool -genkeypair -v -keystore argus-release.jks -keyalg RSA -keysize 2048 `
        -validity 10000 -alias argus
```

`keytool` ships with the JDK: `C:\Program Files\Eclipse Adoptium\jdk-21.0.7.6-hotspot\bin\keytool.exe`.

**2. Point the build at it** without putting the passwords in git. Create
`keystore.properties` next to `local.properties` (both are git-ignored):

```properties
storeFile=D:/keys/argus-release.jks
storePassword=...
keyAlias=argus
keyPassword=...
```

Then add to `app/build.gradle.kts`, inside `android { }`:

```kotlin
val keystoreProperties = java.util.Properties().apply {
    val file = rootProject.file("keystore.properties")
    if (file.exists()) file.inputStream().use { load(it) }
}

signingConfigs {
    if (keystoreProperties.isNotEmpty()) {
        create("release") {
            storeFile = file(keystoreProperties.getProperty("storeFile"))
            storePassword = keystoreProperties.getProperty("storePassword")
            keyAlias = keystoreProperties.getProperty("keyAlias")
            keyPassword = keystoreProperties.getProperty("keyPassword")
        }
    }
}

buildTypes {
    release {
        if (keystoreProperties.isNotEmpty()) signingConfig = signingConfigs.getByName("release")
        isMinifyEnabled = false
        proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
    }
}
```

**3. Build and check it:**

```powershell
.\gradlew assembleRelease
# app\build\outputs\apk\release\app-release.apk
& "$env:LOCALAPPDATA\Android\Sdk\build-tools\36.0.0\apksigner.bat" verify --print-certs `
    app\build\outputs\apk\release\app-release.apk
```

Without the keystore the release task still runs and produces `app-release-unsigned.apk`, which
Android will refuse to install. That is the usual reason "the release build doesn't work".

Turning on `isMinifyEnabled = true` needs keep rules for kotlinx.serialization; leave it off unless
the size actually bothers you.

---

## Versions, and why they are floors

| Setting | Value | Why not lower |
|---------|-------|---------------|
| AGP | 9.3.1 | `compose-ui` 1.12 and `core-ktx` 1.19 refuse to build under AGP 8 |
| Gradle | 9.5 | AGP 9.3 rejects anything older |
| Kotlin | 2.3.21 | Compose compiler plugin follows the Kotlin version |
| `compileSdk` / `targetSdk` | 37 | The current AndroidX libraries require compiling against 37 |
| `minSdk` | 28 | Android 9. Nothing here needs to run older |

AGP 9 compiles Kotlin itself, so **there is no `org.jetbrains.kotlin.android` plugin** in the build
files - applying it alongside AGP 9 is an error, not a redundancy. Only the Compose and
serialization compiler plugins are applied.

Versions live in `gradle/libs.versions.toml`; nothing pins a version anywhere else.

---

## When the build fails

| Symptom | Cause |
|---------|-------|
| `Unsupported Java version` / daemon will not start | Gradle picked a JDK newer than 21. Check `gradle/gradle-daemon-jvm.properties`, then `.\gradlew --stop` |
| `The filename, directory name, or volume label syntax is incorrect` | Backslashes in `local.properties`. Use `/` |
| `SDK location not found` | No `local.properties` and no `ANDROID_HOME` |
| `requires Android Gradle plugin 9.1.0 or higher` | A library was bumped past what the pinned AGP supports |
| `plugin is no longer required for Kotlin support since AGP 9.0` | Someone re-added `org.jetbrains.kotlin.android` |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | An APK from a different keystore is installed. `adb uninstall dev.mynrd.argus` first |
| `avdmanager` throws `NoClassDefFoundError: javax/xml/bind/...` | The legacy tool needs JAXB, gone since Java 11. Use `emulator -list-avds`, or Android Studio, to manage AVDs |

---

## Layout

```
src/Argus.Android.App/
  gradle/libs.versions.toml            every version
  gradle/gradle-daemon-jvm.properties  the JDK pin
  app/src/main/AndroidManifest.xml
  app/src/main/res/xml/network_security_config.xml
  app/src/main/kotlin/dev/mynrd/argus/
    ArgusApplication.kt      the container: settings, api, session, client
    MainActivity.kt
    data/SettingsStore.kt    DataStore: address, auto lock, idle minutes, overlay toggles
    net/ServerUrl.kt         what the user typed -> one canonical base
    net/ArgusApi.kt          /api/session, /api/unlock, /api/lock, and the cookie jar
    net/HubConnection.kt     the SignalR JSON protocol, by hand
    net/FrameSocket.kt       /ws/frames, 16-byte header then JPEG
    net/ArgusClient.kt       both connections and everything they say
    net/Models.kt            the server's DTOs
    session/SessionRepository.kt  locked or not, and the idle clock
    ui/                      theme, shell, lock, dashboard, viewer, explorer, settings
```

---

## What is here so far

| Screen | State |
|--------|-------|
| Lock | Address + password, unlocks against `/api/unlock` |
| Dashboard | Live tiles, Run, Browse, Watch, Close, Kill, Unwatch |
| Explorer | Host filesystem, Open with |
| Viewer | Icon toolbar, zoom slider, pinch zoom, fit, full screen, quality, window size, drag toggle, scroll buttons, draggable key pad |
| Settings | Address, auto lock, idle timeout, viewer overlay toggles, version |

The viewer is a control-for-control port of `viewer.component.ts`: same 25-400% zoom range, same
pinch-about-the-midpoint arithmetic, same long-press-is-a-right-click and 10px drag threshold, same
three-state modifiers (off, latched for the next key, locked until tapped off), same Fn row, same
200ms scroll repeat. The icons are the web build's SVG paths, redrawn in Compose.

Two deliberate differences, both because a phone is not a browser window:

- The first frame **fits itself to the screen**. The browser build opens at 100%, which on a
  monitor is about right and on a phone in portrait is a postage stamp.
- Full screen hides the Android system bars rather than calling the Fullscreen API, and the app's
  own tab bar is hidden on the viewer route whether or not full screen is on.

Not yet ported: the tile grid that goes multi-column on a tablet, and wheel-to-zoom (no wheel).

---

## How it talks to the agent

### SignalR without the SignalR client

`HubConnection.kt` writes the JSON hub protocol by hand over an OkHttp WebSocket: a negotiate POST,
a one-line handshake, and four message types. The official Java client would pull in RxJava and
Gson to do the same, and would need the session cookie threaded through its own header API instead
of riding the shared cookie jar.

Note the two ids the negotiate returns. `connectionToken` identifies the socket to the server;
`connectionId` is what the frame socket and the capture subscriptions are keyed by. Using the wrong
one gets you a socket that connects and never receives a frame.

Frames stay on their own socket, for the reason the server splits them: a burst of JPEGs must not
delay a keystroke, and SignalR's JSON protocol would base64 every frame at a third again the bytes.

### The agent address

Stored in DataStore under `server_url`, in the canonical form `scheme://host[:port]` with no
trailing slash. Whatever is typed goes through `ServerUrl.normalise`, so `100.84.12.3:5227` and
`http://100.84.12.3:5227/` land on the same string - the one everything else appends paths to.

It is editable in two places, both writing the same key: the lock screen, so a first run has
somewhere to put it, and Settings, for changing agents later. Changing it drops the session with
the old agent, because the cookie belongs to the host that issued it.

### Sessions

`/api/unlock` answers with an `argus.session` cookie, and OkHttp keeps it **in memory only**.

That mirrors the browser, which gets a session cookie with no expiry and so forgets it when the tab
closes. Writing it to disk would quietly turn the lock into "unlocked forever on a phone anyone can
pick up". The cost is that killing the app means typing the password again; the address survives,
the session does not.

The agent marks that cookie `HttpOnly` so nothing running in the page can read it. An installed app
reads it out of a normal response header, which is what lets the hub and the frame socket be
authenticated at all.

### Cleartext HTTP

`res/xml/network_security_config.xml` permits cleartext everywhere, because the host is typed in by
the user and there is no domain to scope it to. The password and the desktop stream therefore cross
the network unencrypted - acceptable on a tailnet, which is already encrypted, and not acceptable
on plain wifi. `tailscale serve` in front of the agent gets HTTPS, and `https://` addresses work
here without a change.
