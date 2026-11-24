// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.ServerRequester;

public partial class ConnectClientResponse
{
    [PHPProperty("cookie")] public required string Cookie { get; set; }

    [PHPProperty("account_id")] public required int AccountID { get; set; }

    [PHPProperty("nickname")] public required string Nickname { get; set; }

    [PHPProperty("super_id")] public required int SuperID { get; set; }

    [PHPProperty("account_type")] public required int AccountType { get; set; }

    [PHPProperty("level")] public required int Level { get; set; }

    [PHPProperty("clan_id")] public int? ClanID { get; set; }

    [PHPProperty("tag")] public string? ClanTag { get; set; }

    [PHPProperty("infos")] public string Infos { get; set; } = "";

    [PHPProperty("game_cookie")] public string GameCookie { get; set; } = "";
 
    [PHPProperty("standing")] public string Standing { get; set; } = "3";

    [PHPProperty("is_legacy")] public string IsLegacy { get; set; } = "1";

    [PHPProperty("game_tokens")] public int GameTokens { get; set; } = 999999;

    [PHPProperty("dice_tokens")] public int DiceTokens { get; set; } = 999999;

    [PHPProperty("my_upgrades")] public required List<string> MyUpgrades { get; set; }

    [PHPProperty("selected_upgrades")] public required List<string> SelectedUpgrades { get; set; }
}
