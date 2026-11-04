plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "org.amare.browser"
    compileSdk = 36

    defaultConfig {
        applicationId = "org.amare.browser"
        minSdk = 29
        targetSdk = 36
        versionCode = 5
        versionName = "0.5.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildFeatures {
        compose = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }

    val releaseStorePath = System.getenv("AMARE_ANDROID_KEYSTORE")
    val releaseStorePassword = System.getenv("AMARE_ANDROID_KEYSTORE_PASSWORD")
    val releaseKeyAlias = System.getenv("AMARE_ANDROID_KEY_ALIAS")
    val releaseKeyPassword = System.getenv("AMARE_ANDROID_KEY_PASSWORD")
    val releaseSigning = if (
        !releaseStorePath.isNullOrBlank() &&
        !releaseStorePassword.isNullOrBlank() &&
        !releaseKeyAlias.isNullOrBlank() &&
        !releaseKeyPassword.isNullOrBlank()
    ) {
        signingConfigs.create("release") {
            storeFile = file(releaseStorePath)
            storePassword = releaseStorePassword
            keyAlias = releaseKeyAlias
            keyPassword = releaseKeyPassword
        }
    } else {
        null
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            isDebuggable = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
            if (releaseSigning != null) {
                signingConfig = releaseSigning
            }
        }
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.12.01")
    implementation(composeBom)
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material:material-icons-core")
    implementation("androidx.core:core-ktx:1.18.0")
    debugImplementation("androidx.compose.ui:ui-tooling")
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test:runner:1.6.2")

    // Phase 5 engine candidate. Pinned to the latest available GeckoView 152
    // artifact compatible with compileSdk 36; verify the release channel before shipping.
    implementation("org.mozilla.geckoview:geckoview:152.0.20260713164047")
}
