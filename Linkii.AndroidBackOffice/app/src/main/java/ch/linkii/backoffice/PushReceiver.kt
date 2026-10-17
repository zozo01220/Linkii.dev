package ch.linkii.backoffice

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage

/**
 * Reçoit les messages du serveur (données seulement : title, body, category, path) et affiche la notification.
 * Un toucher ouvre le back-office sur la page `path`.
 */
class PushReceiver : FirebaseMessagingService() {
    override fun onNewToken(token: String) { Prefs.setPushToken(this, token) }

    override fun onMessageReceived(msg: RemoteMessage) {
        val d = msg.data
        val title = d["title"] ?: return
        val open = Intent(this, MainActivity::class.java)
            .putExtra(MainActivity.EXTRA_PATH, d["path"] ?: "/")
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        val pending = PendingIntent.getActivity(this, (System.nanoTime() and 0xFFFFFF).toInt(), open, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val b = if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, App.CHANNEL) else @Suppress("DEPRECATION") Notification.Builder(this)
        b.setSmallIcon(R.drawable.ic_notification).setContentTitle(title).setContentText(d["body"] ?: "")
            .setStyle(Notification.BigTextStyle().bigText(d["body"] ?: ""))
            .setColor(0xFF0B1F3A.toInt()).setAutoCancel(true).setContentIntent(pending)
        // une notification par catégorie : une nouvelle remplace la précédente de la même catégorie au lieu de s'empiler
        getSystemService(NotificationManager::class.java).notify(d["category"] ?: "alerts", 1, b.build())
    }
}
