plugins {
    id("com.android.application")
}

android {
    namespace = "ch.linkii.player"
    compileSdk = 35

    defaultConfig {
        applicationId = "ch.linkii.player"
        minSdk = 24
        targetSdk = 35
        versionCode = 1
        versionName = "1.0.0"

        // Adresse neutre du serveur Linkii (modifiable sur l'appareil dans le menu technicien).
        // Surcharge possible : gradlew bundleRelease -PserverUrl=https://app.exemple.ch
        val serverUrl = (project.findProperty("serverUrl") as String?) ?: "https://linkii.duckdns.org"
        buildConfigField("String", "DEFAULT_SERVER_URL", "\"$serverUrl\"")
    }

    buildFeatures { buildConfig = true }

    signingConfigs {
        // Clé d'envoi Google Play : renseignée dans ~/.gradle/gradle.properties (jamais dans le dépôt)
        create("release") {
            val store = project.findProperty("LINKII_KEYSTORE") as String?
            if (store != null) {
                storeFile = file(store)
                storePassword = project.findProperty("LINKII_KEYSTORE_PASSWORD") as String?
                keyAlias = project.findProperty("LINKII_KEY_ALIAS") as String?
                keyPassword = project.findProperty("LINKII_KEY_PASSWORD") as String?
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            if (project.findProperty("LINKII_KEYSTORE") != null) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
