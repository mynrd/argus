// Versions live in gradle/libs.versions.toml. Nothing is applied at the root - only :app builds.
// There is no kotlin-android plugin: AGP 9 compiles Kotlin itself and rejects it.
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.kotlin.serialization) apply false
}
