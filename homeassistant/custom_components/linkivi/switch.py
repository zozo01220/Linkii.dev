"""Allumer et éteindre l'écran (la veille de Linkivi : l'affichage est vraiment coupé)."""

from __future__ import annotations

from typing import Any

from homeassistant.components.switch import SwitchEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .api import LinkivError
from .const import CMD_SCREEN_OFF, CMD_WAKE
from .entity import LinkivEntity, add_new_screens


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    coordinator = entry.runtime_data
    entry.async_on_unload(
        add_new_screens(coordinator, async_add_entities, lambda sid, _s: [LinkivScreenSwitch(coordinator, sid)])
    )


class LinkivScreenSwitch(LinkivEntity, SwitchEntity):
    """Allumé : l'écran est éveillé. Éteindre le met en veille jusqu'au réveil ; allumer le réveille une heure,
    puis ses plages de veille reprennent la main (comme le bouton « Réveiller » de Linkivi)."""

    _attr_translation_key = "screen"

    def __init__(self, coordinator, screen_id: str) -> None:
        super().__init__(coordinator, screen_id, "screen")

    @property
    def is_on(self) -> bool:
        return not self.screen.get("asleep", False)

    async def _send(self, command: str) -> None:
        try:
            await self.coordinator.api.command(self._screen_id, command)
        except LinkivError as err:
            raise HomeAssistantError(str(err)) from err
        await self.coordinator.async_request_refresh()

    async def async_turn_on(self, **kwargs: Any) -> None:
        await self._send(CMD_WAKE)

    async def async_turn_off(self, **kwargs: Any) -> None:
        await self._send(CMD_SCREEN_OFF)
