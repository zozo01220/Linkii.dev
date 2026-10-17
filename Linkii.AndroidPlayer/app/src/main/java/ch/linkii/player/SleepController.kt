package ch.linkii.player

import android.app.AlarmManager
import android.app.PendingIntent
import android.app.admin.DeviceAdminReceiver
import android.app.admin.DevicePolicyManager
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.PowerManager
import org.json.JSONObject
import java.util.Calendar
import java.util.TimeZone

/**
 * Mise en veille de l'appareil selon les plages horaires du back-office.
 *
 * Le player confie ses plages à l'app (LinkiiNative.sleepSchedule) ; l'appareil les garde et les applique seul :
 * - début d'une plage : écran verrouillé (DevicePolicyManager.lockNow) si l'app est administrateur de l'appareil,
 *   sinon écran noir et luminosité minimale (repli, voir [listener]) ;
 * - fin d'une plage : écran rallumé et player ramené au premier plan.
 * Un réveil (AlarmManager.setAlarmClock, exact et actif même en économie d'énergie) est programmé à chaque changement d'état,
 * donc la veille fonctionne même si le player (WebView) est suspendu ou le réseau coupé.
 */
class SleepController(private val ctx: Context, private val prefs: Prefs) {
    private val week = 7 * 24 * 60

    /** Repli sans droit d'administrateur : l'activité passe à l'écran noir (true) ou le quitte (false). */
    companion object {
        @Volatile var listener: ((Boolean) -> Unit)? = null
        private const val POLL_WAKE_MS = 60_000L   // pendant la veille : une demande au serveur par minute

        fun admin(ctx: Context) = ComponentName(ctx, SleepAdmin::class.java)
        fun isAdmin(ctx: Context) = (ctx.getSystemService(Context.DEVICE_POLICY_SERVICE) as DevicePolicyManager).isAdminActive(admin(ctx))
    }

    /** Nouvelles plages du serveur (chaîne vide : plus de veille). */
    fun setSchedule(json: String) {
        if (json == prefs.sleepJson) return
        prefs.sleepJson = json
        prefs.sleepApplied = false
        apply()
    }

    /** wake : fin d'un réveil manuel demandé depuis le back-office (ms, 0 : aucun) ; l'appareil reste éveillé jusque-là malgré les plages. */
    private class Plan(val tz: TimeZone, val ranges: List<IntArray>, val wake: Long)

    private fun plan(): Plan? {
        val raw = prefs.sleepJson
        if (raw.isEmpty()) return null
        return try {
            val o = JSONObject(raw)
            val a = o.getJSONArray("ranges")
            Plan(TimeZone.getTimeZone(o.getString("tz")), (0 until a.length()).map { i -> a.getJSONArray(i).let { intArrayOf(it.getInt(0), it.getInt(1)) } }, maxOf(o.optLong("wakeUntil", 0), prefs.wakeUntilMs))
        } catch (_: Exception) { null }
    }

    /**
     * Réveil manuel : pendant la veille matérielle, l'appareil ne dialogue plus avec le serveur ; il demande donc lui-même, à chaque réveil
     * programmé (chaque minute tant qu'il dort), si le back-office a demandé de l'éveiller. À appeler hors du fil principal.
     */
    fun pollWake() {
        val token = prefs.mediaToken
        if (token.isEmpty() || !asleep()) return
        try {
            val u = java.net.URL(prefs.serverUrl.substringBefore("/player").trimEnd('/') + "/api/player/version")
            (u.openConnection() as java.net.HttpURLConnection).run {
                connectTimeout = 8000; readTimeout = 8000; setRequestProperty("X-Token", token)
                try {
                    if (responseCode == 200) {
                        val until = JSONObject(inputStream.bufferedReader().readText()).optLong("wakeUntil", 0)
                        if (until > System.currentTimeMillis() && until != prefs.wakeUntilMs) prefs.wakeUntilMs = until
                    }
                } finally { disconnect() }
            }
        } catch (_: Exception) {}
    }

    /** Minutes écoulées depuis le lundi 00 h, à l'heure du fuseau des plages. */
    private fun weekMinute(tz: TimeZone, nowMs: Long): Int {
        val c = Calendar.getInstance(tz).apply { timeInMillis = nowMs }
        val day = (c.get(Calendar.DAY_OF_WEEK) + 5) % 7   // lundi = 0
        return day * 1440 + c.get(Calendar.HOUR_OF_DAY) * 60 + c.get(Calendar.MINUTE)
    }

    private fun asleepAt(p: Plan, w: Int) = p.ranges.any { (w >= it[0] && w < it[1]) || (w + week >= it[0] && w + week < it[1]) }

    /** Dans une plage de veille, et pas réveillé à la main. */
    private fun sleepingAt(p: Plan, now: Long) = now >= p.wake && asleepAt(p, weekMinute(p.tz, now))

    /** Dans combien de minutes l'état change-t-il (début ou fin d'une plage) ? */
    private fun minutesToNextChange(p: Plan, w: Int): Int =
        p.ranges.flatMap { listOf(it[0] % week, it[1] % week) }.map { ((it - w) % week + week) % week }.filter { it > 0 }.minOrNull() ?: week

    fun asleep(): Boolean = plan()?.let { sleepingAt(it, System.currentTimeMillis()) } ?: false

    /** Aligne l'appareil sur les plages : à appeler au démarrage, à chaque réception de plages, à chaque réveil programmé et régulièrement. */
    fun apply() {
        try { applyNow() } catch (e: Exception) { android.util.Log.w("Linkii", "Mise en veille : $e") }   // la veille ne doit jamais faire tomber le player
    }

    private fun applyNow() {
        val p = plan()
        val now = System.currentTimeMillis()
        val sleeping = p != null && sleepingAt(p, now)
        val pm = ctx.getSystemService(Context.POWER_SERVICE) as PowerManager
        if (sleeping) {
            // verrouille à l'entrée dans la plage, et si quelqu'un a rallumé l'écran pendant la plage
            val hardware = isAdmin(ctx)
            if (hardware) { if (!prefs.sleepApplied || pm.isInteractive) lock() }
            else listener?.invoke(true)
            prefs.sleepApplied = true
        } else {
            listener?.invoke(false)
            if (prefs.sleepApplied) wake(pm)
            prefs.sleepApplied = false
        }
        schedule(p, now)
    }

    private fun lock() {
        try { (ctx.getSystemService(Context.DEVICE_POLICY_SERVICE) as DevicePolicyManager).lockNow() } catch (_: Exception) { listener?.invoke(true) }
    }

    @Suppress("DEPRECATION")
    private fun wake(pm: PowerManager) {
        try {
            pm.newWakeLock(PowerManager.SCREEN_BRIGHT_WAKE_LOCK or PowerManager.ACQUIRE_CAUSES_WAKEUP or PowerManager.ON_AFTER_RELEASE, "linkii:wake").acquire(10_000)
        } catch (_: Exception) {}
        // premier plan : autorisé par « Affichage par-dessus les autres applications » (menu technicien) ou si l'app est l'écran d'accueil
        try { ctx.startActivity(Intent(ctx, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT)) } catch (_: Exception) {}
    }

    private fun schedule(p: Plan?, now: Long) {
        val am = ctx.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        val flags = PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0)
        val op = PendingIntent.getBroadcast(ctx, 41, Intent(ctx, SleepReceiver::class.java), flags)
        if (p == null || p.ranges.isEmpty()) { am.cancel(op); return }
        val w = weekMinute(p.tz, now)
        // à la minute pile : on retire les secondes déjà écoulées dans la minute en cours
        val c = Calendar.getInstance(p.tz).apply { timeInMillis = now }
        val intoMinute = c.get(Calendar.SECOND) * 1000L + c.get(Calendar.MILLISECOND)
        var at = now - intoMinute + minutesToNextChange(p, w) * 60_000L
        if (p.wake > now) at = minOf(at, p.wake)                                   // fin d'un réveil manuel : l'appareil se rendort
        if (sleepingAt(p, now)) at = minOf(at, now + POLL_WAKE_MS)                 // pendant la veille : on demande chaque minute si un réveil est demandé
        val show = PendingIntent.getActivity(ctx, 42, Intent(ctx, MainActivity::class.java), flags)
        // Exact et actif en économie d'énergie si l'appareil accorde les « alarmes exactes » (Android 12+ : à autoriser, voir le menu technicien) ;
        // sinon alarme approximative : le contrôle toutes les 15 s de l'activité prend le relais tant que le player tourne.
        // Jamais d'exception ici : un refus ne doit pas faire planter le player.
        try {
            if (Build.VERSION.SDK_INT < 31 || am.canScheduleExactAlarms()) am.setAlarmClock(AlarmManager.AlarmClockInfo(at, show), op)
            else am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, op)
        } catch (_: Exception) {
            try { am.set(AlarmManager.RTC_WAKEUP, at, op) } catch (_: Exception) {}
        }
    }

    /** Les alarmes sont-elles exactes sur cet appareil ? (information du menu technicien) */
    fun exactAlarms(): Boolean = Build.VERSION.SDK_INT < 31 || (ctx.getSystemService(Context.ALARM_SERVICE) as AlarmManager).canScheduleExactAlarms()
}

/** Réveil programmé : début ou fin d'une plage de veille, fin d'un réveil manuel, ou (pendant la veille) contrôle d'une demande de réveil. */
class SleepReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val app = context.applicationContext
        val pending = goAsync()   // la demande au serveur se fait hors du fil principal
        Thread {
            try {
                val controller = SleepController(app, Prefs(app))
                controller.pollWake()
                controller.apply()
            } catch (_: Exception) {} finally { pending.finish() }
        }.start()
    }
}

/**
 * Administrateur de l'appareil, uniquement pour pouvoir verrouiller l'écran (« force-lock »).
 * Activation : menu technicien › « Activer la mise en veille de l'écran », ou, si l'appareil n'affiche pas cet écran de confirmation :
 * adb shell dpm set-active-admin ch.linkii.player/.SleepAdmin
 */
class SleepAdmin : DeviceAdminReceiver()
