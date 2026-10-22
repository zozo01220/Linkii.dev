package ch.linkii.player

import android.content.Context

/** Réglages locaux de l'appareil (adresse du serveur, code PIN du menu technicien). */
class Prefs(context: Context) {
    private val sp = context.getSharedPreferences("linkii", Context.MODE_PRIVATE)

    var serverUrl: String
        get() = sp.getString("server", null) ?: BuildConfig.DEFAULT_SERVER_URL
        set(v) = sp.edit().putString("server", v.trim().trimEnd('/')).apply()

    /** Code propre à l'appareil : 9999 tant qu'aucun autre n'est défini ; vide = menu sans code. */
    var devicePin: String
        get() = sp.getString("pin", DEFAULT_PIN) ?: DEFAULT_PIN
        set(v) = sp.edit().putString("pin", v.trim()).apply()

    /** Code imposé par l'organisation (reçu du serveur, gardé pour fonctionner hors ligne) ; null : rien d'imposé. Vide : menu sans code. */
    var orgPin: String?
        get() = if (sp.contains("org_pin")) sp.getString("org_pin", "") else null
        set(v) = sp.edit().apply { if (v == null) remove("org_pin") else putString("org_pin", v) }.apply()

    /** L'organisation laisse l'appareil définir son propre code (qui remplace alors celui de l'organisation). */
    var orgPinLocal: Boolean
        get() = sp.getBoolean("org_pin_local", false)
        set(v) = sp.edit().putBoolean("org_pin_local", v).apply()

    /** Un code propre à l'appareil a été défini depuis le menu (et non le 9999 par défaut). */
    val hasDevicePin: Boolean get() = sp.contains("pin")

    /** Le code propre à l'appareil prime si l'organisation l'autorise et qu'il en a un ; sinon celui de l'organisation ; sinon celui de l'appareil. */
    var pin: String
        get() = orgPin.let { org -> if (org == null || (orgPinLocal && hasDevicePin)) devicePin else org }
        set(v) { devicePin = v }

    /** L'organisation impose son code et interdit un code propre : « Définir le code PIN » disparaît du menu. */
    val pinManaged: Boolean get() = orgPin != null && !orgPinLocal

    /** Empreinte du code de l'organisation, renvoyée au serveur pour confirmer la réception (voir TechPin.Sig côté serveur). */
    fun orgPinSig(): String? = orgPin?.let { sha256Hex("linkii-pin|$it").take(8) }

    /** Dernière liste de médias à garder (une adresse par ligne) et jeton de l'écran : le téléchargement reprend dès le démarrage. */
    var mediaList: String
        get() = sp.getString("media_list", "") ?: ""
        set(v) = sp.edit().putString("media_list", v).apply()

    var mediaToken: String
        get() = sp.getString("media_token", "") ?: ""
        set(v) = sp.edit().putString("media_token", v).apply()

    /** Plages de mise en veille reçues du serveur ({"tz":…,"ranges":[[début,fin],…]}, vide : aucune) : gardées ici, l'appareil les applique seul, même hors ligne. */
    var sleepJson: String
        get() = sp.getString("sleep_json", "") ?: ""
        set(v) = sp.edit().putString("sleep_json", v).apply()

    /** Fin du dernier réveil manuel demandé depuis le back-office (ms, 0 : aucun) ; relevée par l'appareil pendant la veille, où le player ne dialogue plus. */
    var wakeUntilMs: Long
        get() = sp.getLong("wake_until", 0L)
        set(v) = sp.edit().putLong("wake_until", v).apply()

    /** L'appareil a déjà été mis en veille (écran verrouillé) pour la plage en cours : évite de le refaire à chaque contrôle. */
    var sleepApplied: Boolean
        get() = sp.getBoolean("sleep_applied", false)
        set(v) = sp.edit().putBoolean("sleep_applied", v).apply()

    /** Démarrage automatique avec l'appareil (paramètres de l'écran, back-office) : oui par défaut. */
    var autoStart: Boolean
        get() = sp.getBoolean("auto_start", true)
        set(v) = sp.edit().putBoolean("auto_start", v).apply()

    /** Adresse complète du player : l'adresse du serveur, suivie de /player/ sauf si elle le contient déjà. */
    fun playerUrl(): String {
        val base = serverUrl
        return if (base.contains("/player")) base else "$base/player/"
    }
}

private const val DEFAULT_PIN = "9999"

private fun sha256Hex(s: String): String =
    java.security.MessageDigest.getInstance("SHA-256").digest(s.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
