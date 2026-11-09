"""Entité de base : un appareil Home Assistant par écran Linkivi."""

from __future__ import annotations

from collections.abc import Callable
from typing import Any

from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from . import LinkivCoordinator
from .const import DEVICE_LABELS, DOMAIN


class LinkivEntity(CoordinatorEntity[LinkivCoordinator]):
    """Un écran : son nom, son matériel et son état viennent de GET /screens."""

    _attr_has_entity_name = True

    def __init__(self, coordinator: LinkivCoordinator, screen_id: str, key: str) -> None:
        super().__init__(coordinator)
        self._screen_id = screen_id
        self._attr_unique_id = f"{screen_id}_{key}"
        screen = self.screen
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, screen_id)},
            name=screen.get("name", "Écran"),
            manufacturer="Linkivi",
            model=DEVICE_LABELS.get(screen.get("device"), "Écran"),
            configuration_url=coordinator.api.base_url,
        )

    @property
    def screen(self) -> dict[str, Any]:
        return self.coordinator.data.get(self._screen_id, {})

    @property
    def available(self) -> bool:
        # un écran supprimé dans Linkivi reste déclaré, mais indisponible
        return super().available and self._screen_id in self.coordinator.data


@callback
def add_new_screens(coordinator: LinkivCoordinator, async_add_entities: AddEntitiesCallback, build: Callable[[str, dict[str, Any]], list]) -> Callable[[], None]:
    """Crée les entités des écrans connus maintenant, puis de ceux qui apparaîtront plus tard.

    ``build(screen_id, screen)`` rend la liste des entités d'un écran ; l'abonnement rendu est à passer à async_on_unload.
    """
    known: set[str] = set()

    @callback
    def _add() -> None:
        entities = []
        for screen_id, screen in coordinator.data.items():
            if screen_id in known:
                continue
            known.add(screen_id)
            entities.extend(build(screen_id, screen))
        if entities:
            async_add_entities(entities)

    _add()
    return coordinator.async_add_listener(_add)
