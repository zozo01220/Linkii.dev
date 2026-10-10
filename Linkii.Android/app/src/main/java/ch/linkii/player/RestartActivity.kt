package ch.linkii.player

import android.app.Activity
import android.content.Intent
import android.os.Bundle

/**
 * Relance l'application depuis un autre processus (:restart) : MainActivity tue son processus juste après l'avoir lancée,
 * puis cette activité démarre une nouvelle instance de l'application. Sans ce relais, la relance reprendrait le processus
 * encore vivant et serait tuée avec lui.
 */
class RestartActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        @Suppress("DEPRECATION")
        val next = intent.getParcelableExtra<Intent>("next")
        if (next != null) startActivity(next)
        finish()
        Runtime.getRuntime().exit(0)
    }
}
