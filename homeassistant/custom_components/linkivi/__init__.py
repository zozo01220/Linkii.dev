"""Linkivi : voir et commander ses écrans depuis Home Assistant."""

from __future__ import annotations

import logging
from typing import Any

from pathlib import Path

from homeassistant.components.frontend import add_extra_js_url
from homeassistant.components.http import StaticPathConfig
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_API_KEY, CONF_URL, Platform
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryAuthFailed
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.typing import ConfigType
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from .api import LinkivApi, LinkivAuthError, LinkivError
from .const import CARD_FILE, CARD_URL, CARD_VERSION, DOMAIN, SCAN_INTERVAL

_LOGGER = logging.getLogger(__name__)

PLATFORMS: list[Platform] = [Platform.BINARY_SENSOR, Platform.BUTTON, Platform.SWITCH]


class LinkivCoordinator(DataUpdateCoordinator[dict[str, dict[str, Any]]]):
    """Relit la liste des écrans de Linkivi ; les données sont indexées par identifiant d'écran."""

    def __init__(self, hass: HomeAssistant, entry: ConfigEntry, api: LinkivApi) -> None:
        super().__init__(hass, _LOGGER, name=DOMAIN, config_entry=entry, update_interval=SCAN_INTERVAL)
        self.api = api

    async def _async_update_data(self) -> dict[str, dict[str, Any]]:
        try:
            screens = await self.api.screens()
        except LinkivAuthError as err:
            raise ConfigEntryAuthFailed(str(err)) from err
        except LinkivError as err:
            raise UpdateFailed(str(err)) from err
        return {s["id"]: s for s in screens}


async def async_setup(hass: HomeAssistant, config: ConfigType) -> bool:
    """Publie la carte « Écrans Linkivi » et la fait charger par l'interface : aucune ressource à déclarer à la main."""
    path = Path(__file__).parent / "www" / CARD_FILE
    await hass.http.async_register_static_paths([StaticPathConfig(CARD_URL, str(path), False)])
    add_extra_js_url(hass, f"{CARD_URL}?v={CARD_VERSION}")
    return True


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Crée le client et le coordinateur, puis les entités."""
    api = LinkivApi(async_get_clientsession(hass), entry.data[CONF_URL], entry.data[CONF_API_KEY])
    coordinator = LinkivCoordinator(hass, entry, api)
    await coordinator.async_config_entry_first_refresh()
    entry.runtime_data = coordinator
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    return True


async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
