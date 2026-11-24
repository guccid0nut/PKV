// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;

namespace ASPIRE.Tests.Lua;

public class LuaIntegrationTests : LuaTestBase
{
    public LuaIntegrationTests()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Load all scripts to verify they load without crashing
        LoadAllScripts();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void LuaIntegration_LoadAllScripts_ShouldLoadWithoutCriticalErrors()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        if (!Directory.Exists(LuaScriptsRoot)) return;

        object? mainTable = LuaState["Main"] ?? LuaState["main"];
        Assert.NotNull(mainTable);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void LuaIntegration_SubmitForm_ShouldSendRequestToServer()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        LuaState.DoString("SubmitForm('MyTestForm', 'f', 'test_submit', 'test_key', 'test_value')");

        Assert.NotNull(LastResponse);
        Assert.NotEqual(HttpStatusCode.NotFound, LastResponse.StatusCode);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void LuaIntegration_ClickButton_ShouldExecuteBoundCallback()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Register a widget with a callback
        LuaState.DoString(@"
            local w = GetWidget('TestButton')
            w:SetCallback('onclick', function()
                SubmitForm('ClickedForm', 'f', 'clicked_f')
            end)
        ");

        // Verify that the callback is registered in C#
        Assert.True(WidgetCallbacks.ContainsKey("TestButton"));

        // Act: Click the button
        ClickButton("TestButton");

        // Assert: SubmitForm should have been called
        Assert.NotNull(LastResponse);
        // The last request should be for 'ClickedForm' logic.
        // We can inspect LastResponseContent if needed, or just that it fired.

        // Since we didn't mock the response for 'clicked_f', it might be anything.
        // But verifying LastResponse is not null after ClickButton implies the Lua callback ran.
        // To be safer, we can check if the response status is not 404 (client_requester exists).
        Assert.NotEqual(HttpStatusCode.NotFound, LastResponse.StatusCode);
    }
}
