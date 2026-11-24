// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

using System.Text.Json.Serialization;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Controllers.News;

[ApiController]
public class NewsController(ILogger<NewsController> logger, IAccountRepository accountRepository) : ControllerBase
{
    [HttpGet("api/motd")]
    [HttpGet("events/common/client/motd/feed.json")]
    [Produces("application/json")]
    public async Task<IActionResult> GetMotdFeed([FromQuery] string? lang, CancellationToken cancellationToken = default)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("[NewsController] Serving dynamic JSON MOTD feed for language: {Lang}", lang ?? "en");
        }

        List<string> streamerNames = [];
        try
        {
            IReadOnlyList<string> usernames = await accountRepository.GetApprovedStreamerTwitchUsernamesAsync(4, cancellationToken);
            streamerNames = usernames.ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client aborted/canceled the MOTD request during screen transition; cleanly ignore
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Failed to fetch active streamers for MOTD feed");
            }
        }

        MotdFeedResponseDto feed = new(
            Version: NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion,
            Spotlight: new MotdSpotlightDto(
                HeroId: "Hero_Gwynplaine",
                Name: "Gwynplaine",
                Title: "The Man Who Laughs",
                Icon: "/heroes/gwynplaine/icon.tga",
                Model: "/heroes/gwynplaine/preview.mdf",
                Anim: "idle",
                Tags: "Agility / Magic Assassin",
                Lore: "Gwynplaine flings ancient runic chain scythes across the battlefield, reeling in victims with Runic Tether and carving them down with psychotic glee.",
                Abilities:
                [
                    new("Runic Tether", "Flings ancient chain scythes that latch onto enemy flesh and pull target inward.", "/heroes/gwynplaine/ability_01/icon.tga"),
                    new("Rictus Glide", "Dashes through dark ether, phasing through creeps and dealing burst magic damage.", "/heroes/gwynplaine/ability_02/icon.tga"),
                    new("Sadistic Grin", "Passive aura of madness that siphons enemy magic resistance upon critical hits.", "/heroes/gwynplaine/ability_03/icon.tga"),
                    new("The Final Punchline", "Unleashes theatrical mayhem, spinning blades in an uncontrollable blood frenzy.", "/heroes/gwynplaine/ability_04/icon.tga")
                ],
                Effect: "/heroes/gwynplaine/effects/aura.effect"
            ),
            Slides:
            [
                new MotdSlideDto(
                    Id: "bubbles",
                    Badge: "LEGENDARY AVATAR",
                    Title: "Abyssal Bubbles",
                    Subtitle: "Deep-Sea Trench Leviathan * Bioluminescent Chitin",
                    Description: "Awoken from the oceanic abyss with pulsing bio-lures and aquatic resonance.",
                    Banner: "/heroes/bubbles/alt16/icon.tga",
                    Model: "/heroes/bubbles/alt16/preview.mdf",
                    Anim: "idle",
                    Action: "store:Hero_Bubbles.Alt16",
                    Bullets:
                    [
                        "Bioluminescent coral glow on Shell Surf and Kelp Field.",
                        "Eerie deep-sea aquatic voiceover set and soundscape."
                    ],
                    Effect: "/heroes/bubbles/alt16/effects/self.effect"
                ),
                new MotdSlideDto(
                    Id: "fps",
                    Badge: "UPDATE 5.0.2",
                    Title: "Matchmaking, Queue Arcade & Custom Stats",
                    Subtitle: "Queue Fix * Seamless Bot Swaps * Custom Scoreboards",
                    Description: "Reliable matchmaking queues, seamless Queue Arcade bot/player swaps, custom map match history & scoreboards, and mode polish.",
                    Banner: null,
                    Model: null,
                    Anim: null,
                    Action: "settings:video",
                    Bullets:
                    [
                        "Valkey matchmaking fix + seamless Queue Arcade bot/player swaps.",
                        "Custom map stat retrieval, match history & tailored scoreboards.",
                        "Fixed custom mode items/rules, Strikers 3-2-1 & avatar visuals."
                    ]
                ),
                new MotdSlideDto(
                    Id: "audio",
                    Badge: "AUDIO STUDIO",
                    Title: "3D Spatial Audio & Surround",
                    Subtitle: "Binaural HRTF Positioning",
                    Description: "Directional 3D sound routing, soft peak limiting, and clearer positional spell cues.",
                    Banner: null,
                    Model: null,
                    Anim: null,
                    Action: "settings:sound",
                    Bullets:
                    [
                        "OpenAL / WASAPI spatial routing with 5.1 and 7.1 surround mixdown.",
                        "Directional HRTF audio cues for nearby spell casts and footsteps.",
                        "Soft acoustic limiter helps reduce volume spikes in busy teamfights."
                    ]
                ),
                new MotdSlideDto(
                    Id: "twitch",
                    Badge: "COMMUNITY",
                    Title: "Twitch Live Stream Studio",
                    Subtitle: "Watch, Stream, and Compete",
                    Description: "Link your Twitch account to highlight community streams and join custom viewer lobbies.",
                    Banner: null,
                    Model: null,
                    Anim: null,
                    Action: "social:streamers",
                    Bullets:
                    [
                        "Live community creators featured in-game.",
                        "Spectate & Mentor friends live from your friendslist.",
                        "Join #STREAMERS channel in-game for community scrims."
                    ],
                    Streamers: streamerNames
                )
            ],
            Vault:
            [
                new MotdVaultDealDto("Join the Discord", "discord.gg/projectkongor", "DISCORD", "discord_link", "Connect with players, report bugs, share balance feedback, and chat directly with developers."),
                new MotdVaultDealDto("Looking for Volunteers", "C++ Engine * C# / Aspire * 3D Art", "CONTRIBUTE", "volunteer_link", "Help build the future of Newerth. Programmers, 3D artists, animators, and testers are always welcome!")
            ]
        );

        return Ok(feed);
    }

    [HttpGet("events/common/client/motd/index.php")]
    [HttpGet("events/common/client/motd")]
    [HttpGet("motd/index.php")]
    [HttpGet("motd")]
    [Produces("text/html")]
    public async Task<ContentResult> GetMotdNews([FromQuery] string? lang,
        CancellationToken cancellationToken = default)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("[NewsController] Serving MOTD News Feed for language: {Lang}", lang ?? "en");
        }

        List<string> streamerNames = [];
        try
        {
            IReadOnlyList<string> usernames = await accountRepository.GetApprovedStreamerTwitchUsernamesAsync(4, cancellationToken);
            streamerNames = usernames.ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client aborted/canceled the MOTD request during screen transition; cleanly ignore
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Failed to fetch active streamers for MOTD");
            }
        }

        string htmlContent = GenerateNewsHtml(streamerNames);
        return new ContentResult { Content = htmlContent, ContentType = "text/html; charset=utf-8", StatusCode = 200 };
    }


    public static string GenerateNewsHtml(List<string>? streamers = null)
    {
        StringBuilder sb = new();
        sb.Append("""
                  <!DOCTYPE html>
                  <html lang="en">
                  <head>
                      <meta charset="UTF-8">
                      <meta name="viewport" content="width=device-width, initial-scale=1.0">
                      <title>Project Kongor V - News &amp; Update Notes (v5.0.2)</title>
                      <style>
                          * {
                              box-sizing: border-box;
                              margin: 0;
                              padding: 0;
                              user-select: none;
                          }
                          body {
                              background: linear-gradient(135deg, #0b1118 0%, #121c27 50%, #0d1520 100%);
                              color: #d7e2ed;
                              font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
                              font-size: 13px;
                              line-height: 1.5;
                              padding: 16px;
                              overflow-x: hidden;
                          }
                          .container {
                              max-width: 820px;
                              margin: 0 auto;
                          }
                          .header-banner {
                              background: linear-gradient(90deg, #1b2838 0%, #2a3d54 50%, #1b2838 100%);
                              border: 1px solid #c89b3c;
                              border-radius: 6px;
                              padding: 16px 20px;
                              margin-bottom: 16px;
                              display: flex;
                              align-items: center;
                              justify-content: space-between;
                              box-shadow: 0 4px 15px rgba(0, 0, 0, 0.5), inset 0 0 10px rgba(200, 155, 60, 0.2);
                          }
                          .header-title h1 {
                              color: #f0c365;
                              font-size: 20px;
                              text-transform: uppercase;
                              letter-spacing: 1.5px;
                              text-shadow: 0 2px 4px rgba(0, 0, 0, 0.8);
                          }
                          .header-title p {
                              color: #8da4be;
                              font-size: 12px;
                              margin-top: 2px;
                          }
                          .version-badge {
                              background: #c89b3c;
                              color: #0b1118;
                              font-weight: bold;
                              font-size: 11px;
                              padding: 4px 10px;
                              border-radius: 3px;
                              letter-spacing: 1px;
                          }
                          .grid {
                              display: grid;
                              grid-template-columns: 1fr 1fr;
                              gap: 12px;
                              margin-bottom: 16px;
                          }
                          .card {
                              background: rgba(27, 40, 56, 0.7);
                              border: 1px solid #2a3d54;
                              border-radius: 6px;
                              padding: 14px;
                              box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
                              transition: transform 0.2s ease, border-color 0.2s ease;
                          }
                          .card:hover {
                              transform: translateY(-2px);
                              border-color: #4a6b94;
                          }
                          .card-header {
                              display: flex;
                              align-items: center;
                              margin-bottom: 8px;
                              gap: 8px;
                          }
                          .card-tag {
                              font-size: 10px;
                              font-weight: bold;
                              text-transform: uppercase;
                              padding: 2px 6px;
                              border-radius: 3px;
                              letter-spacing: 0.5px;
                          }
                          .card-tag.patch { background: #2a6f97; color: #fff; }
                          .card-tag.event { background: #d97706; color: #fff; }
                          .card-tag.streamer { background: #9146ff; color: #fff; }
                          .card-tag.mastery { background: #059669; color: #fff; }
                          .card-title {
                              color: #fff;
                              font-weight: 600;
                              font-size: 13px;
                          }
                          .card-body p {
                              color: #b0c4de;
                              font-size: 12px;
                              margin-bottom: 6px;
                          }
                          .card-body ul {
                              list-style: none;
                              padding-left: 2px;
                          }
                          .card-body li {
                              color: #9cb3c9;
                              font-size: 11.5px;
                              margin-bottom: 3px;
                              position: relative;
                              padding-left: 12px;
                          }
                          .card-body li::before {
                              content: "•";
                              color: #c89b3c;
                              position: absolute;
                              left: 0;
                              font-weight: bold;
                          }
                          .highlight {
                              color: #f0c365;
                              font-weight: 600;
                          }
                          .streamer-highlight {
                              color: #bf94ff;
                              font-weight: 600;
                          }
                          .footer {
                              text-align: center;
                              font-size: 11px;
                              color: #5c728a;
                              border-top: 1px solid #1f2f42;
                              padding-top: 10px;
                          }
                      </style>
                  </head>
                  <body>
                      <div class="container">
                          <div class="header-banner">
                              <div class="header-title">
                                  <h1>Project Kongor V</h1>
                                  <p>Queue Arcade, Friendslist Broadcasting, Custom Maps &amp; Community Preservation</p>
                              </div>
                              <div class="version-badge">v5.0.2 ACTIVE</div>
                          </div>

                          <div class="grid">
                              <div class="card">
                                  <div class="card-header">
                                      <span class="card-tag streamer">SOCIAL</span>
                                      <span class="card-title">Spectating, Mentoring &amp; Streamers</span>
                                  </div>
                                  <div class="card-body">
                                      <p>Two-way Spectate &amp; Mentor requests, Monitor Game, and real-time friendslist broadcasting are live.</p>
                                      <ul>
                  """);

        if (streamers is { Count: > 0 })
        {
            foreach (string streamer in streamers)
            {
                sb.Append($"""
                                           <li><span class="streamer-highlight">twitch.tv/{streamer}</span> - Verified Streamer</li>
                           """);
            }
        }
        else
        {
            sb.Append("""
                                      <li><span class="highlight">Spectate &amp; Mentor</span> friends directly from your friendslist.</li>
                                      <li>Live <span class="streamer-highlight">Online / AFK / Lobby / In-Match</span> broadcasting &amp; Monitor Game.</li>
                                      <li>Join <span class="highlight">#STREAMERS</span> in-game channel for community scrims.</li>
                      """);
        }

        sb.Append("""
                                      </ul>
                                  </div>
                              </div>

                              <div class="card">
                                  <div class="card-header">
                                      <span class="card-tag patch">PATCH 5.0.2</span>
                                      <span class="card-title">War Effort, Matchmaking &amp; Custom Stats</span>
                                  </div>
                                  <div class="card-body">
                                      <p>War Effort 14-bag quest cycles, Valkey matchmaking fix, seamless Queue Arcade bot/player swaps, and custom map stats &amp; scoreboards.</p>
                                      <ul>
                                          <li><span class="highlight">War Effort Quests:</span> 2 daily quests, 14 Merrick reward bags, exclusives &amp; live leaderboard.</li>
                                          <li><span class="highlight">Matchmaking &amp; Arcade:</span> Valkey queue fix + seamless bot/player swaps (<span class="highlight">/arcade</span>).</li>
                                          <li><span class="highlight">Custom Stats &amp; Polish:</span> Custom map match history, scoreboards, PKV Guard Safe Mode, RNNoise voice &amp; P2P relays.</li>
                                      </ul>
                                  </div>
                              </div>

                              <div class="card">
                                  <div class="card-header">
                                      <span class="card-tag event">MAPS &amp; BOTS</span>
                                      <span class="card-title">10v10 Chaos, Mode Rules &amp; 20-Map Bots</span>
                                  </div>
                                  <div class="card-body">
                                      <p>Fixed items, shops &amp; custom map rules across modes, plus 20-player kill feed credit and smoother 20-map bot AI.</p>
                                      <ul>
                                          <li><span class="highlight">10v10 Chaos:</span> Expanded 20-player multi-assist kill feed &amp; custom scoreboards.</li>
                                          <li><span class="highlight">Mode Rules &amp; Items:</span> Strikers (Soccer) 3-2-1 kickoff fix, True Devo shop, Enfo's &amp; Nightmarish Co-Op.</li>
                                          <li><span class="highlight">Bots (WIP):</span> Smoother shopping &amp; combo casting across Strikers, Mid Wars &amp; 18 more maps.</li>
                                      </ul>
                                  </div>
                              </div>

                              <div class="card">
                                  <div class="card-header">
                                      <span class="card-tag mastery">HEROES</span>
                                      <span class="card-title">Mastery &amp; 100+ Free Avatars</span>
                                  </div>
                                  <div class="card-body">
                                      <p>100% free forever: classic community avatars and hero mastery progression included with zero paywalls.</p>
                                      <ul>
                                          <li>Over 100 community avatars and skins unlocked out of the box.</li>
                                          <li>Seasonal Halloween maps, optional map themes &amp; soundtrack selector.</li>
                                          <li>Hero mastery progression &amp; lifetime stats in the Web Portal.</li>
                                      </ul>
                                  </div>
                              </div>
                          </div>

                          <div class="footer">
                              Project Kongor V (v5.0.2) &bull; Non-Commercial Preservation &bull; Heroes of Newerth &copy; Garena, Frostburn Studios &amp; S2 Games
                          </div>
                      </div>
                  </body>
                  </html>
                  """);
        return sb.ToString();
    }
}

public sealed record MotdAbilityDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("desc")] string Desc,
    [property: JsonPropertyName("icon")] string Icon
);

public sealed record MotdSpotlightDto(
    [property: JsonPropertyName("hero_id")] string HeroId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("icon")] string Icon,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("anim")] string? Anim = null,
    [property: JsonPropertyName("tags")] string Tags = "",
    [property: JsonPropertyName("lore")] string Lore = "",
    [property: JsonPropertyName("abilities")] IReadOnlyList<MotdAbilityDto>? Abilities = null,
    [property: JsonPropertyName("effect")] string? Effect = null
);

public sealed record MotdSlideDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("badge")] string Badge,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string Subtitle,
    [property: JsonPropertyName("desc")] string Description,
    [property: JsonPropertyName("banner")] string? Banner,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("anim")] string? Anim,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("bullets")] IReadOnlyList<string>? Bullets,
    [property: JsonPropertyName("streamers")] IReadOnlyList<string>? Streamers = null,
    [property: JsonPropertyName("effect")] string? Effect = null
);

public sealed record MotdVaultDealDto(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("price")] string Price,
    [property: JsonPropertyName("discount")] string Discount,
    [property: JsonPropertyName("product_id")] string ProductId,
    [property: JsonPropertyName("desc")] string? Desc = null
);

public sealed record MotdFeedResponseDto(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("spotlight")] MotdSpotlightDto Spotlight,
    [property: JsonPropertyName("slides")] IReadOnlyList<MotdSlideDto> Slides,
    [property: JsonPropertyName("vault")] IReadOnlyList<MotdVaultDealDto> Vault
);
