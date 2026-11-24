// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Mvc;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

[ApiController]
[Route("api/v1/assets")]
public class Assets3DController(
    IGameArchiveVfsService vfsService,
    ILogger<Assets3DController> logger,
    IConfiguration? configuration = null) : ControllerBase
{
    private static readonly Dictionary<string, string> AnnouncerFolderMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["flamboyant"] = "flamboyant",
        ["hardcore"] = "ballsofsteel",
        ["badass"] = "ballsofsteel",
        ["samuel l jackson"] = "bamf",
        ["samuel_l_jackson"] = "bamf",
        ["samuel"] = "bamf",
        ["bamf"] = "bamf",
        ["censored samuel l jackson"] = "bamf_censored",
        ["breakycpk"] = "breakycpk",
        ["breaky"] = "breakycpk",
        ["pimp"] = "pimp",
        ["british"] = "english",
        ["english"] = "english",
        ["female"] = "female",
        ["pudding"] = "female",
        ["mspudding"] = "mspudding",
        ["8-bit"] = "8-bit",
        ["8bit"] = "8-bit",
        ["8-bit announcer pack"] = "8-bit",
        ["ursa"] = "ursa",
        ["ursa corps announcer pack"] = "ursa",
        ["merrick"] = "merrick",
        ["merrick announcer pack"] = "merrick",
        ["ninja"] = "ninja",
        ["ninja announcer pack"] = "ninja",
        ["pirate"] = "pirate",
        ["pirate announcer"] = "pirate",
        ["dark_master"] = "dark_master",
        ["dark master announcer pack"] = "dark_master",
        ["paragon"] = "paragon",
        ["paragon announcer pack"] = "paragon",
        ["siam"] = "siam",
        ["siam warrior announcer pack"] = "siam",
        ["ascension"] = "ascension",
        ["ascension announcer pack"] = "ascension",
        ["soccer"] = "soccer",
        ["soccerannouncerpack"] = "soccer",
        ["haunted_house"] = "haunted_house",
        ["haunted house announcer"] = "haunted_house",
        ["miku"] = "miku",
        ["miku announcer"] = "miku",
        ["punk"] = "graffiti",
        ["punk announcer"] = "graffiti",
        ["esan"] = "esan",
        ["esan announcer pack"] = "esan",
        ["2018worldcup"] = "2018worldcup",
        ["2018 world cup announcer"] = "2018worldcup",
        ["na_khom"] = "na_khom",
        ["na khom thai announcer"] = "na_khom",
        ["surfer"] = "surfer",
        ["surfer announcer pack"] = "surfer",
        ["sea"] = "sea",
        ["south east asia pack"] = "sea",
        ["thai"] = "thai",
        ["thai_english"] = "thai_english",
        ["thai english"] = "thai_english",
        ["seductive"] = "seductive"
    };

    private static readonly Dictionary<string, string> TauntFolderMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["baby_taunt"] = "cryingbaby",
        ["cryingbaby"] = "cryingbaby",
        ["baby"] = "cryingbaby",
        ["hellbourne_taunt"] = "hellbourne",
        ["hellbourne"] = "hellbourne",
        ["dumpster_taunt"] = "dumpster",
        ["dumpster"] = "dumpster",
        ["droppinggs_taunt"] = "gs",
        ["gs"] = "gs",
        ["acme_taunt"] = "acme",
        ["acme"] = "acme",
        ["fatlady_taunt"] = "fatlady",
        ["fatlady"] = "fatlady",
        ["fist_taunt"] = "fist",
        ["fist"] = "fist",
        ["honeybadger_taunt"] = "honeybadger",
        ["honeybadger"] = "honeybadger",
        ["kongor_taunt"] = "kongor",
        ["kongor"] = "kongor",
        ["rip_taunt"] = "rip",
        ["tombstone"] = "rip",
        ["rainbow_taunt"] = "unicorn",
        ["unicorn"] = "unicorn",
        ["ursa_taunt"] = "ursa",
        ["ursa"] = "ursa",
        ["rekt_taunt"] = "rekt",
        ["rekt"] = "rekt",
        ["slap_the_troll_taunt"] = "slap_the_troll",
        ["slap_the_troll"] = "slap_the_troll",
        ["rest_in_p_taunt"] = "rest_in_p/pepperoni_pizza",
        ["gamer_rage_taunt"] = "gamer_rage",
        ["gamer_rage"] = "gamer_rage",
        ["5's_taunt"] = "555",
        ["5s"] = "555",
        ["wolf_time_taunt"] = "wolf_time",
        ["wolf_time"] = "wolf_time",
        ["blacksmith_taunt"] = "blacksmith_taunt",
        ["celebration_taunt"] = "celebration",
        ["fortunecookie_taunt"] = "fortunecookie",
        ["bad_present_taunt"] = "bad_present",
        ["songkran_taunt"] = "songkran_bucket",
        ["treasurechest_taunt"] = "treasure_chest",
        ["killer_ball_taunt"] = "killerball",
        ["miku_taunt"] = "miku",
        ["poker_taunt"] = "poker",
        ["paragon_taunt"] = "paragon",
        ["siam_taunt"] = "siam",
        ["ascension_boogie_taunt"] = "ascension01",
        ["beam_me_up_taunt"] = "ascension02",
        ["smackdown"] = "smackdown",
        ["standard"] = "standard"
    };

    private bool IsCleanroomWithoutGameArchives()
    {
        if (CleanroomPlaceholderAssets.IsForceCleanroomPlaceholders(configuration))
            return true;

        if (vfsService is Kongor.Core.Services.Vfs.GameArchiveVfsService concreteVfs)
            return string.IsNullOrEmpty(concreteVfs.GamePath) || !Directory.Exists(concreteVfs.GamePath);

        return !vfsService.FileExists("heroes/empath/hero.entity");
    }

    [HttpGet("3d/hero/{hero}/{alt?}")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public IActionResult GetHeroModel(string hero, string? alt = null, [FromQuery] string? resource = null)
    {
        try
        {
            if (CleanroomPlaceholderAssets.IsForceCleanroomPlaceholders(configuration))
            {
                byte[] cleanroomGlb = CleanroomPlaceholderAssets.BuildCrystalGlb($"{hero}:{alt ?? "default"}");
                return File(cleanroomGlb, "model/gltf-binary", $"{hero}_{alt ?? "default"}_cleanroom.glb");
            }

            byte[]? glb = vfsService.GetHeroModelGlb(hero, alt, resource);
            if (glb == null || glb.Length == 0)
            {
                if (hero.Equals("creep", StringComparison.OrdinalIgnoreCase) ||
                    hero.Equals("creep_melee", StringComparison.OrdinalIgnoreCase) ||
                    hero.Equals("good_melee", StringComparison.OrdinalIgnoreCase) ||
                    hero.Equals("creep_legion", StringComparison.OrdinalIgnoreCase))
                {
                    glb = vfsService.GetModelGlb("npcs/good_melee/model.mdf");
                }
                else if (hero.Equals("bad_melee", StringComparison.OrdinalIgnoreCase) ||
                         hero.Equals("creep_hellbourne", StringComparison.OrdinalIgnoreCase))
                {
                    glb = vfsService.GetModelGlb("npcs/bad_melee/model.mdf");
                }
            }

            if (glb == null || glb.Length == 0)
            {
                if (IsCleanroomWithoutGameArchives())
                {
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("[Assets3D] Cleanroom mode (no GamePath): serving procedural crystal glTF for hero={Hero}, alt={Alt}.",
                            hero, alt ?? "default");

                    byte[] fallbackGlb = CleanroomPlaceholderAssets.BuildCrystalGlb($"{hero}:{alt ?? "default"}");
                    return File(fallbackGlb, "model/gltf-binary", $"{hero}_{alt ?? "default"}_cleanroom.glb");
                }

                if (logger.IsEnabled(LogLevel.Warning))
                    logger.LogWarning("[Assets3D] Model not found for hero: {Hero}, alt: {Alt}, resource: {Resource}",
                        hero, alt ?? "default", resource ?? "none");

                return NotFound(new
                {
                    error = $"3D Model for '{hero}' (alt: '{alt ?? "default"}') was not found in game archives."
                });
            }

            return File(glb, "model/gltf-binary", $"{hero}_{alt ?? "default"}.glb");
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex, "[Assets3D] Error generating 3D model for hero: {Hero}, alt: {Alt}", hero,
                    alt ?? "default");

            return StatusCode(500, new { error = "Failed to process 3D model: " + ex.Message });
        }
    }

    [HttpGet("3d/model")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public IActionResult GetModelByPath([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { error = "Path query parameter is required." });

        try
        {
            if (CleanroomPlaceholderAssets.IsForceCleanroomPlaceholders(configuration))
            {
                string cleanFilename = Path.GetFileNameWithoutExtension(path) + "_cleanroom.glb";
                return File(CleanroomPlaceholderAssets.BuildCrystalGlb(path), "model/gltf-binary", cleanFilename);
            }

            byte[]? glb = vfsService.GetModelGlb(path);
            if (glb == null || glb.Length == 0)
            {
                if (IsCleanroomWithoutGameArchives())
                {
                    string fallbackFilename = Path.GetFileNameWithoutExtension(path) + "_cleanroom.glb";
                    return File(CleanroomPlaceholderAssets.BuildCrystalGlb(path), "model/gltf-binary", fallbackFilename);
                }

                return NotFound(new { error = $"Model file at '{path}' was not found." });
            }

            string filename = Path.GetFileNameWithoutExtension(path) + ".glb";
            return File(glb, "model/gltf-binary", filename);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex, "[Assets3D] Error generating 3D model for path: {Path}", path);

            return StatusCode(500, new { error = "Failed to process 3D model: " + ex.Message });
        }
    }

    [HttpGet("texture")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public IActionResult GetTexture([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { error = "Path query parameter is required." });

        if (CleanroomPlaceholderAssets.IsForceCleanroomPlaceholders(configuration))
        {
            byte[] svgBytes = Encoding.UTF8.GetBytes(CleanroomPlaceholderAssets.BuildSvgBadge("store", path));
            return File(svgBytes, "image/svg+xml");
        }

        byte[]? textureBytes = vfsService.GetTextureBytes(path);
        if (textureBytes == null || textureBytes.Length == 0)
        {
            if (IsCleanroomWithoutGameArchives())
            {
                byte[] fallbackSvg = Encoding.UTF8.GetBytes(CleanroomPlaceholderAssets.BuildSvgBadge("store", path));
                return File(fallbackSvg, "image/svg+xml");
            }

            return NotFound(new { error = $"Texture at '{path}' was not found." });
        }

        string contentType = path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
            ? "image/x-dds"
            : path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                ? "image/x-tga"
                : "application/octet-stream";

        return File(textureBytes, contentType);
    }

    [HttpGet("icon")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public IActionResult GetIcon([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { error = "Path query parameter is required." });

        if (!CleanroomPlaceholderAssets.IsForceCleanroomPlaceholders(configuration))
        {
            byte[]? pngBytes = vfsService.GetTexturePng(path);
            if (pngBytes != null && pngBytes.Length > 0)
                return File(pngBytes, "image/png");
        }

        byte[] fallbackSvg = Encoding.UTF8.GetBytes(CleanroomPlaceholderAssets.BuildSvgBadge("store", path));
        return File(fallbackSvg, "image/svg+xml");
    }

    [HttpGet("sound")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public IActionResult GetSound(
        [FromQuery] string? hero = null,
        [FromQuery] string? alt = null,
        [FromQuery] string? action = null,
        [FromQuery] string? announcer = null,
        [FromQuery] string? taunt = null,
        [FromQuery] string? path = null)
    {
        if (CleanroomPlaceholderAssets.IsForceCleanroomPlaceholders(configuration))
            return NoContent();

        byte[]? soundBytes = null;

        // 1. Direct path lookup
        if (!string.IsNullOrWhiteSpace(path))
        {
            soundBytes = vfsService.ReadAllBytes(path);
            if (soundBytes == null && !string.IsNullOrWhiteSpace(hero))
                soundBytes = vfsService.GetHeroSound(hero, alt, path);
        }

        // 2. Announcer sound lookup
        if ((soundBytes == null || soundBytes.Length == 0) && !string.IsNullOrWhiteSpace(announcer))
        {
            string cleanAnnouncer = announcer.Trim().ToLowerInvariant();
            if (AnnouncerFolderMap.TryGetValue(cleanAnnouncer, out string? mappedFolder)) cleanAnnouncer = mappedFolder;

            string cleanAction = string.IsNullOrWhiteSpace(action) ? "first_blood" : action.Trim().ToLowerInvariant();

            List<string> announcerCandidates =
            [
                $"shared/sounds/announcer/{cleanAnnouncer}/{cleanAction}.ogg",
                $"shared/sounds/announcer/{cleanAnnouncer}/{cleanAction}.wav",
                $"shared/sounds/announcer/{cleanAnnouncer}/{cleanAction}_1.ogg",
                $"shared/sounds/announcer/{cleanAnnouncer}/{cleanAction}_1.wav",
                $"shared/sounds/announcer/{cleanAction}.ogg",
                $"shared/sounds/announcer/{cleanAction}.wav"
            ];

            foreach (string candidate in announcerCandidates)
            {
                soundBytes = vfsService.ReadAllBytes(candidate);
                if (soundBytes != null && soundBytes.Length > 0) break;
            }
        }

        // 3. Taunt sound lookup
        if ((soundBytes == null || soundBytes.Length == 0) && !string.IsNullOrWhiteSpace(taunt))
        {
            string cleanTaunt = taunt.Trim().ToLowerInvariant();
            if (TauntFolderMap.TryGetValue(cleanTaunt, out string? mappedTauntFolder)) cleanTaunt = mappedTauntFolder;

            string cleanAction = string.IsNullOrWhiteSpace(action) ? "cast" : action.Trim().ToLowerInvariant();

            List<string> tauntCandidates =
            [
                $"shared/effects/taunts/{cleanTaunt}/sounds/{cleanAction}.ogg",
                $"shared/effects/taunts/{cleanTaunt}/sounds/{cleanAction}.wav",
                $"shared/effects/taunts/{cleanTaunt}/sounds/cast.ogg",
                $"shared/effects/taunts/{cleanTaunt}/sounds/cast2.ogg",
                $"shared/effects/taunts/{cleanTaunt}/sounds/deathmetal.ogg",
                $"shared/effects/taunts/{cleanTaunt}/sounds/taunt.ogg",
                $"shared/effects/taunts/{cleanTaunt}/sounds/anvil.ogg",
                $"shared/effects/taunts/{cleanTaunt}/sounds/kongortaunt.ogg",
                $"shared/effects/taunts/{cleanTaunt}/sounds/sound.ogg",
                "shared/sounds/announcer/humiliation.ogg"
            ];

            foreach (string candidate in tauntCandidates)
            {
                soundBytes = vfsService.ReadAllBytes(candidate);
                if (soundBytes != null && soundBytes.Length > 0) break;
            }
        }

        // 4. Hero sound lookup
        if ((soundBytes == null || soundBytes.Length == 0) && !string.IsNullOrWhiteSpace(hero))
            soundBytes = vfsService.GetHeroSound(hero, alt, action);

        if (soundBytes == null || soundBytes.Length == 0) return NoContent();

        string contentType = soundBytes.Length >= 4 && soundBytes[0] == 0x4F && soundBytes[1] == 0x67 &&
                             soundBytes[2] == 0x67 && soundBytes[3] == 0x53
            ? "audio/ogg"
            : "audio/wav";

        return File(soundBytes, contentType);
    }

    [HttpGet("hero-info/{hero}")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public IActionResult GetHeroInfo(string hero)
    {
        HeroEntityInfoDto? info = vfsService.GetHeroEntityInfo(hero);
        if (info == null) return NotFound(new { error = $"Hero '{hero}' was not found." });

        return Ok(info);
    }
}
