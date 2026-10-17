package ch.linkii.backoffice

import android.app.Activity
import android.content.Context
import android.hardware.biometrics.BiometricManager
import android.hardware.biometrics.BiometricPrompt
import android.os.Build
import android.os.CancellationSignal

/**
 * Verrouillage par empreinte ou visage (ou code de l'appareil), prévu pour plus tard : désactivé par défaut,
 * aucun écran du back-office ne le propose encore. Pour l'activer : `LinkiiAdmin.setBiometric(true)` depuis la page.
 * L'app se reverrouille après [GRACE_MS] passées en arrière-plan.
 */
object BiometricGate {
    private const val GRACE_MS = 30_000L
    private var leftAt = 0L
    private var unlocked = false
    private var asking = false

    fun available(c: Context): Boolean {
        if (Build.VERSION.SDK_INT < 30) return false
        val authenticators = BiometricManager.Authenticators.BIOMETRIC_WEAK or BiometricManager.Authenticators.DEVICE_CREDENTIAL
        return c.getSystemService(BiometricManager::class.java).canAuthenticate(authenticators) == BiometricManager.BIOMETRIC_SUCCESS
    }

    fun onLeave() { leftAt = System.currentTimeMillis() }

    /** Verrouille si l'option est active et que l'absence a duré ; [onResult] reçoit true quand l'app peut s'afficher. */
    fun check(a: Activity, onResult: (Boolean) -> Unit) {
        if (!Prefs.biometric(a) || !available(a)) { onResult(true); return }
        if (unlocked && System.currentTimeMillis() - leftAt < GRACE_MS) { onResult(true); return }
        if (asking) return
        asking = true; unlocked = false
        val authenticators = BiometricManager.Authenticators.BIOMETRIC_WEAK or BiometricManager.Authenticators.DEVICE_CREDENTIAL
        BiometricPrompt.Builder(a).setTitle(a.getString(R.string.unlock_title)).setAllowedAuthenticators(authenticators).build()
            .authenticate(CancellationSignal(), a.mainExecutor, object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(r: BiometricPrompt.AuthenticationResult) { asking = false; unlocked = true; leftAt = System.currentTimeMillis(); onResult(true) }
                override fun onAuthenticationError(code: Int, msg: CharSequence?) { asking = false; onResult(false) }
            })
    }
}
