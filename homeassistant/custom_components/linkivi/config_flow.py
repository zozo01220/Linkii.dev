"""Ajout de Linkivi : l'adresse du serveur et une clé d'API (modèle « Home Assistant »)."""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

import voluptuous as vol

from homeassistant.config_entries import ConfigFlow, ConfigFlowResult
from homeassistant.const import CONF_API_KEY, CONF_URL
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import LinkivApi, LinkivAuthError, LinkivError
from .const import DOMAIN


class LinkivConfigFlow(ConfigFlow, domain=DOMAIN):
    VERSION = 1

    async def _check(self, url: str, key: str) -> tuple[dict[str, Any] | None, str | None]:
        """Essaie la clé : l'organisation si elle marche, sinon le code d'erreur à afficher."""
        api = LinkivApi(async_get_clientsession(self.hass), url, key)
        try:
            me = await api.me()
            await api.screens()  # vérifie aussi l'accès « Écrans »
        except LinkivAuthError:
            return None, "invalid_auth"
        except LinkivError:
            return None, "cannot_connect"
        return me, None

    async def async_step_user(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        if user_input is not None:
            url = user_input[CONF_URL].strip().rstrip("/")
            key = user_input[CONF_API_KEY].strip()
            me, error = await self._check(url, key)
            if me is not None:
                org = me.get("organisation", {})
                await self.async_set_unique_id(str(org.get("id", url)))
                self._abort_if_unique_id_configured()
                return self.async_create_entry(
                    title=org.get("name") or "Linkivi", data={CONF_URL: url, CONF_API_KEY: key}
                )
            errors["base"] = error or "unknown"
        schema = vol.Schema(
            {
                vol.Required(CONF_URL, default=(user_input or {}).get(CONF_URL, "https://")): str,
                vol.Required(CONF_API_KEY): str,
            }
        )
        return self.async_show_form(step_id="user", data_schema=schema, errors=errors)

    async def async_step_reauth(self, entry_data: Mapping[str, Any]) -> ConfigFlowResult:
        return await self.async_step_reauth_confirm()

    async def async_step_reauth_confirm(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        """La clé a été révoquée ou a expiré : en saisir une nouvelle."""
        errors: dict[str, str] = {}
        entry = self._get_reauth_entry()
        if user_input is not None:
            key = user_input[CONF_API_KEY].strip()
            me, error = await self._check(entry.data[CONF_URL], key)
            if me is not None:
                return self.async_update_reload_and_abort(entry, data_updates={CONF_API_KEY: key})
            errors["base"] = error or "unknown"
        return self.async_show_form(
            step_id="reauth_confirm", data_schema=vol.Schema({vol.Required(CONF_API_KEY): str}), errors=errors
        )
