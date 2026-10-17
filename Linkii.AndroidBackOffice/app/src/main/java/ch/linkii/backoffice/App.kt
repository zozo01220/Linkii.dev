package ch.linkii.backoffice

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import com.google.firebase.FirebaseApp
import com.google.firebase.FirebaseOptions
import com.google.firebase.messaging.FirebaseMessaging

/**
 * Démarre Firebase à partir des paramètres de compilation (`-PfcmProjectId`, `-PfcmAppId`, `-PfcmApiKey`, `-PfcmSenderId`) :
 * pas de fichier google-services.json. Sans ces paramètres, l'app fonctionne sans notifications.
 */
class App : Application() {
    override fun onCreate() {
        super.onCreate()
        createChannel()
        if (!pushConfigured) return
        if (FirebaseApp.getApps(this).isEmpty()) {
            FirebaseApp.initializeApp(this, FirebaseOptions.Builder()
                .setProjectId(BuildConfig.FCM_PROJECT_ID)
                .setApplicationId(BuildConfig.FCM_APP_ID)
                .setApiKey(BuildConfig.FCM_API_KEY)
                .setGcmSenderId(BuildConfig.FCM_SENDER_ID)
                .build())
        }
        FirebaseMessaging.getInstance().token.addOnSuccessListener { Prefs.setPushToken(this, it) }
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT < 26) return
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.channel_alerts), NotificationManager.IMPORTANCE_HIGH))
    }

    companion object {
        const val CHANNEL = "alerts"
        val pushConfigured: Boolean
            get() = BuildConfig.FCM_PROJECT_ID.isNotEmpty() && BuildConfig.FCM_APP_ID.isNotEmpty() && BuildConfig.FCM_API_KEY.isNotEmpty()
    }
}
