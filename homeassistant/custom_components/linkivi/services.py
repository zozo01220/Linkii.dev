"""Actions Linkivi : afficher un message sur des écrans, et le retirer."""

from __future__ import annotations

import logging

import voluptuous as vol

from homeassistant.core import HomeAssistant, ServiceCall
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import area_registry as ar
from homeassistant.helpers import config_validation as cv
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers import entity_registry as er

from .api import LinkivError
from .const import (
    DOMAIN,
    LOOKS,
    SERVICE_CLEAR_DISPLAY,
    SERVICE_SHOW_MESSAGE,
)

_LOGGER = logging.getLogger(__name__)

# Les clés de cible (entity_id, device_id, area_id, floor_id, label_id) passent telles quelles (extra=ALLOW_EXTRA) : on les lit nous-mêmes,
# sans dépendre d'une fonction interne de Home Assistant qui change d'une version à l'autre.
SHOW_MESSAGE_SCHEMA = vol.All(
    vol.Schema(
        {
            vol.Optional("title"): vol.All(cv.string, vol.Length(max=60)),
            vol.Optional("message"): vol.All(cv.string, vol.Length(max=300)),
            vol.Optional("look", default=LOOKS[0]): vol.In(LOOKS),
            vol.Optional("duration", default=30): vol.All(vol.Coerce(int), vol.Range(min=5, max=3600)),
            vol.Optional("wake", default=False): cv.boolean,
            vol.Optional("place", default="full"): vol.In(("full", "banner")),
        },
        extra=vol.ALLOW_EXTRA,
    ),
    cv.has_at_least_one_key("title", "message"),
)

CLEAR_DISPLAY_SCHEMA = vol.Schema({}, extra=vol.ALLOW_EXTRA)


def _ids(call: ServiceCall, key: str) -> set[str]:
    value = call.data.get(key)
    if value is None:
        return set()
    return {v for v in ([value] if isinstance(value, str) else list(value)) if isinstance(v, str) and v not in ("none", "all")}


async def _screens_of(hass: HomeAssistant, call: ServiceCall) -> list[tuple[object, str]]:
    """Les écrans Linkivi visés par l'action (écrans, appareils, pièces, étages, libellés), avec le coordinateur qui les connaît."""
    entity_reg = er.async_get(hass)
    device_reg = dr.async_get(hass)
    area_reg = ar.async_get(hass)

    entity_ids = _ids(call, "entity_id")
    device_ids = _ids(call, "device_id")
    area_ids = _ids(call, "area_id")
    labels = _ids(call, "label_id")
    floors = _ids(call, "floor_id")
    if floors:
        area_ids |= {a.id for a in area_reg.async_list_areas() if a.floor_id in floors}
    if labels:
        entity_ids |= {e.entity_id for e in entity_reg.entities.values() if labels & set(e.labels)}
        device_ids |= {d.id for d in device_reg.devices.values() if labels & set(d.labels)}
    for area_id in area_ids:   # une pièce : ses appareils, et les entités rangées directement dedans
        device_ids |= {d.id for d in dr.async_entries_for_area(device_reg, area_id)}
        entity_ids |= {e.entity_id for e in er.async_entries_for_area(entity_reg, area_id)}

    screen_ids: set[str] = set()
    for entity_id in entity_ids:
        entry = entity_reg.async_get(entity_id)
        if entry and entry.platform == DOMAIN and entry.unique_id:
            screen_ids.add(entry.unique_id.split("_", 1)[0])   # « {écran}_{entité} »
    for device_id in device_ids:
        device = device_reg.async_get(device_id)
        if device:
            screen_ids.update(ident for domain, ident in device.identifiers if domain == DOMAIN)

    found: list[tuple[object, str]] = []
    for entry in hass.config_entries.async_entries(DOMAIN):
        coordinator = getattr(entry, "runtime_data", None)
        if coordinator is None:
            continue
        found.extend((coordinator, sid) for sid in sorted(screen_ids) if sid in coordinator.data)
    if not found:
        raise ServiceValidationError("Choisissez au moins un écran Linkivi (un écran, un appareil ou une pièce).")
    return found


async def _run(call: ServiceCall, send) -> None:
    """Envoie à chaque écran ; une erreur d'un écran n'empêche pas les autres, et toutes sont rapportées ensemble."""
    failures: list[str] = []
    for coordinator, screen_id in await _screens_of(call.hass, call):
        name = coordinator.data.get(screen_id, {}).get("name", screen_id)
        try:
            await send(coordinator.api, screen_id)
        except LinkivError as err:
            failures.append(f"{name} : {err}")
    if failures:
        raise HomeAssistantError("\n".join(failures))


async def async_register_services(hass: HomeAssistant) -> None:
    """Déclare les actions ; appelé une fois, au chargement de l'intégration."""

    async def show_message(call: ServiceCall) -> None:
        data = call.data

        async def send(api, screen_id: str) -> None:
            await api.display(
                screen_id,
                title=data.get("title"),
                message=data.get("message"),
                look=data["look"],
                duration=data["duration"],
                wake=data["wake"],
                place=data["place"],
            )

        await _run(call, send)

    async def clear_display(call: ServiceCall) -> None:
        async def send(api, screen_id: str) -> None:
            await api.clear_display(screen_id)

        await _run(call, send)

    hass.services.async_register(DOMAIN, SERVICE_SHOW_MESSAGE, show_message, schema=SHOW_MESSAGE_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_CLEAR_DISPLAY, clear_display, schema=CLEAR_DISPLAY_SCHEMA)
