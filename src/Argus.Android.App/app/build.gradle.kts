import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

/**
 * The version lives in version.properties so that both this build and a human can read it, and so
 * that bumping it is a one-line diff rather than an edit buried in the Gradle file.
 */
val versionFile = rootProject.file("version.properties")

val versionProps = Properties().apply {
    if (versionFile.exists()) versionFile.inputStream().use { load(it) }
}

val versionMajor = versionProps.getProperty("major", "0").trim().toInt()
val versionMinor = versionProps.getProperty("minor", "1").trim().toInt()
val versionBuild = versionProps.getProperty("build", "1").trim().toInt()

/** major.minor.build - the build number is what makes every APK distinguishable. */
val appVersionName = "$versionMajor.$versionMinor.$versionBuild"

android {
    namespace = "dev.mynrd.argus"
    compileSdk = 37

    defaultConfig {
        applicationId = "dev.mynrd.argus"
        minSdk = 28
        targetSdk = 37
        // The build number alone: versionCode has to increase, and nothing else here has to be
        // recoverable from it.
        versionCode = versionBuild
        versionName = appVersionName
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        // For BuildConfig.VERSION_NAME, which the Settings screen shows. Reading it back off the
        // PackageManager would work too, on a deprecated API, for the same string.
        buildConfig = true
    }

    packaging {
        resources.excludes += setOf("/META-INF/{AL2.0,LGPL2.1}")
    }
}

/**
 * Keeps a copy of every APK built, named for its version, in dist/.
 *
 * The build's own output is always app-debug.apk, so without this an APK on a phone cannot be
 * matched back to anything - and the previous one is gone the moment the next build runs.
 */
val archiveApk = tasks.register<Copy>("archiveApk") {
    description = "Copies the built APKs into dist/ under their version number."
    from(layout.buildDirectory.file("outputs/apk/debug/app-debug.apk")) {
        rename { "argus-$appVersionName-debug.apk" }
    }
    from(layout.buildDirectory.file("outputs/apk/release/app-release.apk")) {
        rename { "argus-$appVersionName-release.apk" }
    }
    into(rootProject.layout.projectDirectory.dir("dist"))
}

/**
 * Bumps the build number after a build, not before it: the APK that was just produced carries the
 * number that was on disk when the build was configured, and the next one gets the next number.
 * That is what makes "every build is a new version" true without renumbering the thing in hand.
 */
val bumpVersion = tasks.register("bumpVersion") {
    description = "Increments the build number in version.properties."
    doLast {
        val next = versionBuild + 1
        versionFile.writeText(
            """
            # The app version. `build` is bumped by every assemble - see bumpVersion in
            # app/build.gradle.kts - so no two APKs ever carry the same version. Edit major and
            # minor by hand.
            major=$versionMajor
            minor=$versionMinor
            build=$next
            """.trimIndent() + "\n",
        )
        logger.lifecycle("Built $appVersionName; next build is $versionMajor.$versionMinor.$next")
    }
}

tasks.matching { it.name == "assembleDebug" || it.name == "assembleRelease" }.configureEach {
    finalizedBy(archiveApk, bumpVersion)
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.datastore.preferences)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.material3)

    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)
}
