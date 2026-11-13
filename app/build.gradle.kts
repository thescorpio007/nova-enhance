import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Build number comes from GitHub (run number); shown as "1.0 · build 01".
val buildNo: Int = (System.getenv("NOVA_BUILD") ?: "1").toInt()
// 2.0 counts its builds from 01 (internal build 30 = 2.0 · build 01); the internal number keeps rising so updates keep working.
// Shared with Windows from internal 101 (= 2.0 · build 06): shown = internal − 95.
val buildLabel: String = String.format("%02d", if (buildNo > 100) buildNo - 95 else if (buildNo > 29) buildNo - 29 else buildNo)
val appVersion = if (buildNo > 29) "2.0" else "1.0"

// Signing key: decrypted by the GitHub workflow from keystore/nova-enhance.jks.enc using the
// NOVA_ENHANCE_KEY secret. The key and its password are never stored in the public code.
val signProps = Properties().apply {
    val f = rootProject.file("keystore/signing.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}

android {
    namespace = "com.nova.enhance"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.nova.enhance"
        minSdk = 24
        targetSdk = 35
        versionCode = buildNo
        versionName = "$appVersion · build $buildLabel"
    }

    signingConfigs {
        create("release") {
            val ks = rootProject.file("keystore/nova-enhance.jks")
            if (ks.exists()) {
                storeFile = ks
                storePassword = signProps.getProperty("password")
                keyAlias = "novaenhance"
                keyPassword = signProps.getProperty("password")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            val ks = rootProject.file("keystore/nova-enhance.jks")
            signingConfig = if (ks.exists()) signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }

    lint {
        abortOnError = false
        checkReleaseBuilds = false
    }

    applicationVariants.all {
        outputs.all {
            (this as com.android.build.gradle.internal.api.BaseVariantOutputImpl).outputFileName =
                "NovaEnhance-$appVersion-build-$buildLabel.apk"
        }
    }
}

dependencies {
    implementation("org.jetbrains.kotlin:kotlin-stdlib:2.0.21")
    implementation("com.google.zxing:core:3.5.3")   // 📷 reading the pairing QR code (Apache 2.0)
}
