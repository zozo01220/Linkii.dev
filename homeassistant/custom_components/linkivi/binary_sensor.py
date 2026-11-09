"""L'écran répond-il à Linkivi ?"""

from __future__ import annotations

from typing import Any

from homeassistant.components.binary_sensor import BinarySensorDeviceClass, BinarySensorEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .entity import LinkivEntity, add_new_screens


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    coordinator = entry.runtime_data
    entry.async_on_unload(
        add_new_screens(coordinator, async_add_entities, lambda sid, _s: [LinkivOnlineSensor(coordinator, sid)])
    )


class LinkivOnlineSensor(LinkivEntity, BinarySensorEntity):
    """En ligne : l'écran a donné de ses nouvelles ces 90 dernières secondes."""

    _attr_device_class = BinarySensorDeviceClass.CONNECTIVITY
    _attr_translation_key = "online"

    def __init__(self, coordinator, screen_id: str) -> None:
        super().__init__(coordinator, screen_id, "online")

    @property
    def is_on(self) -> bool:
        return bool(self.screen.get("online"))

    @property
    def extra_state_attributes(self) -> dict[str, Any]:
        s = self.screen
        return {
            "status": s.get("status"),
            "last_seen": s.get("lastSeenAt"),
            "paused": s.get("paused"),
            "area": (s.get("area") or {}).get("name"),
            "zone": (s.get("zone") or {}).get("name"),
            "playlist": (s.get("playlist") or {}).get("name"),
            "resolution": s.get("resolution"),
        }
