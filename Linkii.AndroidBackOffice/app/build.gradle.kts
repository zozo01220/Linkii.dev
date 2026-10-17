plugins {
    id("com.android.application")
}

// Fichiers de sortie : linkii-backoffice-debug.apk, linkii-backoffice-release.aab
base { archivesName.set("linkii-backoffice") }

android {
    namespace = "ch.linkii.backoffice"
    compileSdk = 35

    defaultConfig {
        applicationId = "ch.linkii.backoffice"
        minSdk = 24
        targetSdk = 35
        versionCode = 1
        versionName = "1.0.0"

        // Adresse du back-office (modifiable sur l'appareil : écran « serveur injoignable »).
        // Surcharge possible : gradlew bundleRelease -PserverUrl=https://app.exemple.ch
        val serverUrl = (project.findProperty("serverUrl") as String?) ?: "https://linkii.duckdns.org"
        buildConfigField("String", "DEFAULT_SERVER_URL", "\"$serverUrl\"")

        // Notifications (Firebase Cloud Messaging) : valeurs du projet Firebase, à passer en -P ou dans ~/.gradle/gradle.properties.
        // Sans elles l'app se compile et fonctionne, sans notifications.
        fun fcm(name: String) = (project.findProperty(name) as String?) ?: ""
        buildConfigField("String", "FCM_PROJECT_ID", "\"${fcm("fcmProjectId")}\"")
        buildConfigField("String", "FCM_APP_ID", "\"${fcm("fcmAppId")}\"")
        buildConfigField("String", "FCM_API_KEY", "\"${fcm("fcmApiKey")}\"")
        buildConfigField("String", "FCM_SENDER_ID", "\"${fcm("fcmSenderId")}\"")
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

dependencies {
    // Seule dépendance : réception des notifications push (FCM).
    implementation("com.google.firebase:firebase-messaging:24.1.0")
}
