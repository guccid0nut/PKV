// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

[Authorize]
[Route("ar-mini-games")]
public class ArMiniGamesController(ILogger<ArMiniGamesController> logger) : Controller
{
    private readonly ILogger<ArMiniGamesController> _logger = logger;

    [HttpGet("")]
    [HttpGet("hub")]
    [HttpGet("arcade")]
    public IActionResult Index()
    {
        if (User?.Identity?.IsAuthenticated == true && !IsDiscordAuthenticated())
        {
            return RedirectToAction("LoginDiscord", "Account", new { returnUrl = "/ar-mini-games" });
        }

        ViewData["Title"] = "AR Mini Games - Donut Mixed Reality Arcade";
        return View();
    }

    [HttpGet("monkey-checkers")]
    [HttpGet("monkey-checkers-ar")]
    [HttpGet("checkers")]
    [HttpGet("checkers-ar")]
    public IActionResult MonkeyCheckers([FromQuery] string? room = null)
    {
        if (User?.Identity?.IsAuthenticated == true && !IsDiscordAuthenticated())
        {
            string returnUrl = Request?.Path.Value ?? "/ar-mini-games/monkey-checkers";
            if (!string.IsNullOrEmpty(Request?.QueryString.Value)) returnUrl += Request.QueryString.Value;
            return RedirectToAction("LoginDiscord", "Account", new { returnUrl });
        }

        ViewData["Title"] = "Monkey King AR Checkers - Legion vs Hellbourne";
        ViewData["InitialRoom"] = string.IsNullOrWhiteSpace(room) ? "MK42" : MonkeyCheckersRoomManager.NormalizeCode(room);
        return View();
    }

    [HttpGet("chess")]
    [HttpGet("chess-ar")]
    [HttpGet("kongor-chess")]
    [HttpGet("kongor-chess-ar")]
    public IActionResult KongorChess([FromQuery] string? room = null)
    {
        if (User?.Identity?.IsAuthenticated == true && !IsDiscordAuthenticated())
        {
            string returnUrl = Request?.Path.Value ?? "/ar-mini-games/chess";
            if (!string.IsNullOrEmpty(Request?.QueryString.Value)) returnUrl += Request.QueryString.Value;
            return RedirectToAction("LoginDiscord", "Account", new { returnUrl });
        }

        ViewData["Title"] = "KONGOR AR Chess - Legion vs Hellbourne";
        ViewData["InitialRoom"] = string.IsNullOrWhiteSpace(room) ? "KNG1" : ChessRoomManager.NormalizeCode(room);
        return View("KongorChess");
    }

    [HttpGet("devo-wars")]
    [HttpGet("devowars")]
    [HttpGet("devowars-ar")]
    [HttpGet("devo-wars-ar")]
    [HttpGet("hook-wars")]
    public IActionResult DevoWars([FromQuery] string? room = null)
    {
        if (User?.Identity?.IsAuthenticated == true && !IsDiscordAuthenticated())
        {
            string returnUrl = Request?.Path.Value ?? "/ar-mini-games/devo-wars";
            if (!string.IsNullOrEmpty(Request?.QueryString.Value)) returnUrl += Request.QueryString.Value;
            return RedirectToAction("LoginDiscord", "Account", new { returnUrl });
        }

        ViewData["Title"] = "DevoWarsAR - 1v1 Tabletop AR Hook Duel";
        ViewData["InitialRoom"] = string.IsNullOrWhiteSpace(room) ? "HOOK" : DevoWarsRoomManager.NormalizeCode(room);
        return View();
    }

    [HttpGet("empath")]
    [HttpGet("empath-ar")]
    public IActionResult Empath()
    {
        if (User?.Identity?.IsAuthenticated == true && !IsDiscordAuthenticated())
        {
            return RedirectToAction("LoginDiscord", "Account", new { returnUrl = "/ar-mini-games/empath" });
        }

        ViewData["Title"] = "Empath Tabletop AR Companion";
        return View();
    }

    [HttpGet("riftbreaker")]
    [HttpGet("chrono-pong")]
    public IActionResult Riftbreaker([FromQuery] string? room = null)
    {
        if (User?.Identity?.IsAuthenticated == true && !IsDiscordAuthenticated())
        {
            string returnUrl = Request?.Path.Value ?? "/ar-mini-games/riftbreaker";
            if (!string.IsNullOrEmpty(Request?.QueryString.Value)) returnUrl += Request.QueryString.Value;
            return RedirectToAction("LoginDiscord", "Account", new { returnUrl });
        }

        bool discordAuth = IsDiscordAuthenticated();
        string normalizedRoom = RiftbreakerRoomManager.NormalizeCode(room);
        string? rawHandle = discordAuth
            ? (User?.FindFirst("discord_username")?.Value ?? User?.Identity?.Name)
            : null;
        string displayName = string.IsNullOrWhiteSpace(rawHandle) ? "Guest" : rawHandle.Trim();
        if (displayName.Length > 24) displayName = displayName[..24];

        _logger.LogInformation(
            "[Riftbreaker] Serving Chrono-Pong: Riftbreaker page (Room={Room}, DiscordAuth={DiscordAuth})",
            normalizedRoom,
            discordAuth);

        ViewData["Title"] = "Chrono-Pong: Riftbreaker - 3D Escalating Hero Pong";
        ViewData["InitialRoom"] = normalizedRoom;
        ViewData["PlayerDisplayName"] = displayName;
        ViewData["IsDiscordAuthenticated"] = discordAuth;
        return View();
    }

    private bool IsDiscordAuthenticated()
    {
        if (User?.Identity?.IsAuthenticated != true) return false;
        return User.HasClaim(c => c.Type is "discord_id" or "urn:discord:id" or "urn:discord:avatar") ||
               !string.IsNullOrEmpty(User.FindFirst("discord_id")?.Value) ||
               !string.IsNullOrEmpty(User.FindFirst("urn:discord:id")?.Value);
    }
}
