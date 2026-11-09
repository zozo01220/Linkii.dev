"""Client de l'API publique de Linkivi (accès par clé, https://…/docs/api)."""

from __future__ import annotations

from typing import Any

import aiohttp

from .const import API_PATH


class LinkivError(Exception):
    """Linkivi n'a pas pu répondre, ou a refusé la demande (le texte dit pourquoi)."""


class LinkivAuthError(LinkivError):
    """Clé inconnue, révoquée, expirée, sans l'accès nécessaire ou hors du plan Pro."""


class LinkivApi:
    """Les appels dont l'intégration a besoin : /me, /screens, /screens/{id}/commands et /screens/{id}/display."""

    def __init__(self, session: aiohttp.ClientSession, base_url: str, api_key: str) -> None:
        self._session = session
        self.base_url = base_url.rstrip("/")
        self._headers = {"Authorization": f"Bearer {api_key}", "Accept": "application/json"}

    async def _request(self, method: str, path: str, **kwargs: Any) -> dict[str, Any]:
        url = f"{self.base_url}{API_PATH}{path}"
        try:
            async with self._session.request(
                method, url, headers=self._headers, timeout=aiohttp.ClientTimeout(total=15), **kwargs
            ) as res:
                try:
                    body = await res.json(content_type=None)
                except (aiohttp.ContentTypeError, ValueError):
                    body = {}
                if res.status < 300:
                    return body if isinstance(body, dict) else {}
                # application/problem+json : « code » stable, « detail » lisible
                detail = ""
                if isinstance(body, dict):
                    detail = body.get("detail") or body.get("title") or ""
                if res.status in (401, 403):
                    raise LinkivAuthError(detail or f"Accès refusé ({res.status}).")
                raise LinkivError(detail or f"Erreur {res.status}.")
        except aiohttp.ClientError as err:
            raise LinkivError(f"Linkivi est injoignable : {err}") from err
        except TimeoutError as err:
            raise LinkivError("Linkivi ne répond pas.") from err

    async def me(self) -> dict[str, Any]:
        """Organisation et clé (accès account:read)."""
        return await self._request("GET", "/me")

    async def screens(self) -> list[dict[str, Any]]:
        """Tous les écrans appairés, en suivant le curseur de pagination."""
        result: list[dict[str, Any]] = []
        cursor: str | None = None
        while True:
            params: dict[str, Any] = {"limit": 200}
            if cursor:
                params["cursor"] = cursor
            page = await self._request("GET", "/screens", params=params)
            result.extend(page.get("data", []))
            cursor = page.get("next")
            if not cursor:
                return result

    async def command(self, screen_id: str, command: str) -> dict[str, Any]:
        """Envoie une commande à un écran ; lève LinkivError avec la raison si Linkivi la refuse."""
        return await self._request("POST", f"/screens/{screen_id}/commands", json={"command": command})

    async def display(
        self,
        screen_id: str,
        *,
        title: str | None,
        message: str | None,
        look: str,
        duration: int,
        wake: bool,
    ) -> dict[str, Any]:
        """Affiche un message un moment par-dessus la diffusion (accès screens:control) ; Linkivi dit pourquoi il refuse (écran hors ligne, en veille…)."""
        body: dict[str, Any] = {"look": look, "durationSeconds": duration, "wake": wake}
        if title:
            body["title"] = title
        if message:
            body["message"] = message
        return await self._request("POST", f"/screens/{screen_id}/display", json=body)

    async def clear_display(self, screen_id: str) -> dict[str, Any]:
        """Retire le message affiché : la diffusion reprend."""
        return await self._request("DELETE", f"/screens/{screen_id}/display")
