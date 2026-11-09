"""Redémarrer le player, redémarrer l'appareil, éteindre l'appareil."""

from __future__ import annotations

from homeassistant.components.button import ButtonDeviceClass, ButtonEntity, ButtonEntityDescription
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .api import LinkivError
from .const import CMD_POWER_OFF, CMD_REBOOT, CMD_RESTART_PLAYER
from .entity import LinkivEntity, add_new_screens

# La clé de chaque bouton est la commande de l'API ; le bouton n'existe que si le matériel de l'écran la sait faire
# (Android : redémarrer l'appareil seulement s'il en est propriétaire, jamais l'éteindre ; navigateur : redémarrer le player seulement).
BUTTONS: tuple[ButtonEntityDescription, ...] = (
    ButtonEntityDescription(key=CMD_RESTART_PLAYER, translation_key="restart_player", device_class=ButtonDeviceClass.RESTART),
    ButtonEntityDescription(key=CMD_REBOOT, translation_key="reboot", device_class=ButtonDeviceClass.RESTART),
    ButtonEntityDescription(key=CMD_POWER_OFF, translation_key="power_off"),
)


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    coordinator = entry.runtime_data

    def build(screen_id: str, screen: dict) -> list["LinkivButton"]:
        supported = screen.get("commands") or []
        return [LinkivButton(coordinator, screen_id, d) for d in BUTTONS if d.key in supported]

    entry.async_on_unload(add_new_screens(coordinator, async_add_entities, build))


class LinkivButton(LinkivEntity, ButtonEntity):
    def __init__(self, coordinator, screen_id: str, description: ButtonEntityDescription) -> None:
        super().__init__(coordinator, screen_id, description.key)
        self.entity_description = description

    async def async_press(self) -> None:
        try:
            await self.coordinator.api.command(self._screen_id, self.entity_description.key)
        except LinkivError as err:
            # Linkivi dit pourquoi : écran hors ligne, commande déjà en cours, appareil non compatible…
            raise HomeAssistantError(str(err)) from err
        await self.coordinator.async_request_refresh()
