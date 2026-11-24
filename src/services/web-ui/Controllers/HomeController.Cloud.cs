// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Models;
using Microsoft.AspNetCore.Mvc;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public partial class HomeController
{
    [HttpGet("cloud")]
    [HttpGet("Home/Cloud")]
    [HttpGet("cloud-sync")]
    public async Task<IActionResult> Cloud(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("Login", "Account");
        }

        string ign = User.Identity.Name!;
        AccountDto? account = await _playerStatsClient.GetAccountByIgnAsync(ign, cancellationToken);

        if (account == null)
        {
            return RedirectToAction("Index");
        }

        ViewData["Account"] = account;
        return View();
    }

    [HttpPost("cloud/toggle")]
    public IActionResult ToggleCloud([FromForm] bool useCloud = false, [FromForm] bool autoUpload = false)
    {
        _ = useCloud;
        _ = autoUpload;
        return RedirectToAction("Cloud");
    }

    [HttpGet("cloud/download")]
    public async Task<IActionResult> DownloadCloudPackage(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("Login", "Account");
        }

        string ign = User.Identity.Name!;
        AccountDto? account = await _playerStatsClient.GetAccountByIgnAsync(ign, cancellationToken);
        if (account == null) return RedirectToAction("Cloud");

        return File([], "application/zip", $"{account.Name}_kongor_config.zip");
    }
}
