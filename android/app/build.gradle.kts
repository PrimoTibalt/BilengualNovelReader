import java.util.Properties

plugins {
    id("com.android.application")
}

/*
 * The Android app is a Trusted Web Activity (D51): Chrome showing the site full screen, with no
 * address bar, as an app with its own launcher icon. Everything the reader sees is the site —
 * offline reading included, through its service worker — so a web deploy updates the app and
 * this project changes only when the address, the icon or the signing key does.
 *
 * Where it points: change these three together with `asset_statements` in res/values/strings.xml
 * and `TrustedWebActivity:PackageName` in the server's appsettings.json.
 */
val host = "primotibalt.top"
val launchUrl = "https://$host/reader/ReadingPage"
val appId = "top.primotibalt.reader"

// The release key lives outside the repository: keystore.properties (gitignored) names it.
val keystoreProperties = Properties().apply {
    val file = rootProject.file("keystore.properties")
    if (file.exists()) file.inputStream().use { load(it) }
}

android {
    namespace = appId
    compileSdk = 35

    defaultConfig {
        applicationId = appId
        minSdk = 23
        targetSdk = 35
        versionCode = 1
        versionName = "1.0"

        manifestPlaceholders["hostName"] = host
        manifestPlaceholders["launchUrl"] = launchUrl
    }

    signingConfigs {
        if (keystoreProperties.isNotEmpty()) {
            create("release") {
                storeFile = rootProject.file(keystoreProperties.getProperty("storeFile"))
                storePassword = keystoreProperties.getProperty("storePassword")
                keyAlias = keystoreProperties.getProperty("keyAlias")
                keyPassword = keystoreProperties.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    // Google's Trusted Web Activity launcher: the whole of the app's native side.
    implementation("com.google.androidbrowserhelper:androidbrowserhelper:2.5.0")
}
