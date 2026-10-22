package ch.linkii.player

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/**
 * Relance le player au démarrage de l'appareil et après une mise à jour de l'app.
 * Sur Android 10+, le lancement n'est autorisé que si l'app a l'autorisation « Affichage par-dessus les autres
 * applications » (proposée dans le menu technicien), ou si elle est définie comme écran d'accueil de l'appareil.
 * Démarrage automatique coupé dans les paramètres de l'écran (back-office) : rien au démarrage de l'appareil (une mise à jour relance quand même l'app ouverte).
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == Intent.ACTION_BOOT_COMPLETED && !Prefs(context).autoStart) return
        val launch = Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        try { context.startActivity(launch) } catch (_: Exception) { /* autorisation absente : sans effet */ }
    }
}
