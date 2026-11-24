// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Registration;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class UiControlTests
{
    #region UiControlService Unit Tests

    [Theory]
    [InlineData("Enter", 0x0D)]
    [InlineData("Return", 0x0D)]
    [InlineData("Tab", 0x09)]
    [InlineData("Escape", 0x1B)]
    [InlineData("Esc", 0x1B)]
    [InlineData("Space", 0x20)]
    [InlineData("Spacebar", 0x20)]
    [InlineData("Backspace", 0x08)]
    [InlineData("Back", 0x08)]
    [InlineData("Delete", 0x2E)]
    [InlineData("Del", 0x2E)]
    [InlineData("Insert", 0x2D)]
    [InlineData("Home", 0x24)]
    [InlineData("End", 0x23)]
    [InlineData("PageUp", 0x21)]
    [InlineData("PageDown", 0x22)]
    [InlineData("Up", 0x26)]
    [InlineData("Down", 0x28)]
    [InlineData("Left", 0x25)]
    [InlineData("Right", 0x27)]
    [InlineData("F1", 0x70)]
    [InlineData("F8", 0x77)]
    [InlineData("F12", 0x7B)]
    [InlineData("Shift", 0x10)]
    [InlineData("Ctrl", 0x11)]
    [InlineData("Control", 0x11)]
    [InlineData("Alt", 0x12)]
    [InlineData("A", (byte)'A')]
    [InlineData("1", (byte)'1')]
    public void ResolveVirtualKeyCode_WhenInvoked_MapsSupportedKeysCorrectly(string keyName, byte expectedVk)
    {
        byte actual = UiControlService.ResolveVirtualKeyCode(keyName);
        actual.Should().Be(expectedVk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UnknownKeyXYZ")]
    [InlineData("NotAKey12345")]
    public void ResolveVirtualKeyCode_InvalidOrEmptyKeys_ReturnsZero(string invalidKey)
    {
        byte actual = UiControlService.ResolveVirtualKeyCode(invalidKey);
        actual.Should().Be(0);
    }

    [Theory]
    [InlineData(50000, "Button")]
    [InlineData(50004, "TextBox")]
    [InlineData(50003, "ComboBox")]
    [InlineData(50002, "CheckBox")]
    [InlineData(50013, "RadioButton")]
    [InlineData(50005, "Hyperlink")]
    [InlineData(50011, "MenuItem")]
    [InlineData(50019, "TabItem")]
    [InlineData(50007, "ListItem")]
    [InlineData(50015, "Slider")]
    [InlineData(50016, "Spinner")]
    [InlineData(50012, "ProgressBar")]
    [InlineData(50020, "Text")]
    [InlineData(50032, "Window")]
    [InlineData(50033, "Pane")]
    public void MapControlTypeId_WhenInvoked_MapsStandardUiaTypes(int typeId, string expectedName)
    {
        string actual = UiControlService.MapControlTypeId(typeId);
        actual.Should().Be(expectedName);
    }

    [Theory]
    [InlineData(50000, true)]  // Button
    [InlineData(50004, true)]  // TextBox
    [InlineData(50003, true)]  // ComboBox
    [InlineData(50002, true)]  // CheckBox
    [InlineData(50013, true)]  // RadioButton
    [InlineData(50005, true)]  // Hyperlink
    [InlineData(50011, true)]  // MenuItem
    [InlineData(50019, true)]  // TabItem
    [InlineData(50032, false)] // Window
    [InlineData(50033, false)] // Pane
    [InlineData(50020, false)] // Text
    public void IsInteractiveControlType_GivenControlTypeId_DifferentiatesInteractiveControls(int typeId, bool expectedInteractive)
    {
        bool actual = UiControlService.IsInteractiveControlType(typeId);
        actual.Should().Be(expectedInteractive);
    }

    [Theory]
    [InlineData("BUTTON", "Button")]
    [InlineData("Edit", "TextBox")]
    [InlineData("ComboBox", "ComboBox")]
    [InlineData("ListBox", "ListBox")]
    [InlineData("Static", "Label")]
    [InlineData("ScrollBar", "ScrollBar")]
    public void MapWin32ClassNameToControlType_WhenInvoked_MapsExpectedClasses(string className, string expectedType)
    {
        string actual = UiControlService.MapWin32ClassNameToControlType(className);
        actual.Should().Be(expectedType);
    }

    [Fact]
    public async Task UiControlService_InspectUiAsync_NonExistentTarget_ReturnsFailure()
    {
        UiControlService service = new();
        UiInspectResult result = await service.InspectUiAsync("non_existent_target_app_window_99999");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("Target window not found");
        result.Controls.Should().BeEmpty();
    }

    [Fact]
    public async Task UiControlService_ClickAsync_NonExistentTarget_ReturnsFailure()
    {
        UiControlService service = new();
        UiClickResult result = await service.ClickAsync("non_existent_target_app_window_99999", elementName: "SomeButton");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("Target window");
    }

    [Fact]
    public async Task UiControlService_TypeTextAsync_NonExistentTarget_ReturnsFailure()
    {
        UiControlService service = new();
        UiTypeResult result = await service.TypeTextAsync("non_existent_target_app_window_99999", "hello world");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("Target window");
    }

    [Fact]
    public async Task UiControlService_TypeTextAsync_EmptyText_ReturnsSuccessWithoutError()
    {
        UiControlService service = new();
        UiTypeResult result = await service.TypeTextAsync("any_target", string.Empty);

        result.Success.Should().BeTrue();
        result.CharactersTyped.Should().Be(0);
        result.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task UiControlService_SendKeyAsync_NonExistentTarget_ReturnsFailure()
    {
        UiControlService service = new();
        UiKeyResult result = await service.SendKeyAsync("non_existent_target_app_window_99999", "Enter");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("Target window");
    }

    [Fact]
    public async Task UiControlService_SendKeyAsync_EmptyKey_ReturnsFailure()
    {
        UiControlService service = new();
        UiKeyResult result = await service.SendKeyAsync("any_target", "");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Key name cannot be null or empty");
    }

    [Fact]
    public async Task UiControlService_SendKeyAsync_UnknownKey_ReturnsFailure()
    {
        UiControlService service = new();
        UiKeyResult result = await service.SendKeyAsync("any_target", "NonExistentKey123");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unrecognized key name");
    }

    #endregion

    #region UiControlHandler Tool Dispatch Tests

    [Fact]
    public async Task UiControlHandler_Inspect_DispatchesToService_AndReturnsJson()
    {
        Mock<IUiControlService> mockService = new();
        mockService.Setup(s => s.InspectUiAsync("hon_x64", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiInspectResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x001B0E24",
                Pid = 9820,
                ControlCount = 2,
                Controls =
                [
                    new UiControlElement("PlayButton", "btnPlay", "Button", new UiBoundingRectangle(100, 100, 80, 30), true),
                    new UiControlElement("OptionsButton", "btnOptions", "Button", new UiBoundingRectangle(100, 150, 80, 30), true)
                ]
            });

        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""inspect"", ""target"": ""hon_x64"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeEmpty();

        string jsonText = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("inspect");
        root.GetProperty("controlCount").GetInt32().Should().Be(2);
        root.GetProperty("controls").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task UiControlHandler_Click_WithElementName_DispatchesToService()
    {
        Mock<IUiControlService> mockService = new();
        mockService.Setup(s => s.ClickAsync("willowmaker", "LaunchGame", null, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiClickResult
            {
                Success = true,
                Target = "willowmaker",
                WindowTitle = "WILLOWMAKER",
                Hwnd = "0x002A1133",
                ElementClicked = "LaunchGame",
                ActionTaken = "mouse_click",
                ScreenX = 640,
                ScreenY = 480,
                WindowRelativeX = 200,
                WindowRelativeY = 150
            });

        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""click"", ""target"": ""willowmaker"", ""element_name"": ""LaunchGame"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("elementClicked").GetString().Should().Be("LaunchGame");
        root.GetProperty("screenX").GetInt32().Should().Be(640);
        root.GetProperty("screenY").GetInt32().Should().Be(480);
    }

    [Fact]
    public async Task UiControlHandler_Click_WithCoordinates_DispatchesToService()
    {
        Mock<IUiControlService> mockService = new();
        mockService.Setup(s => s.ClickAsync("hon_x64", null, 300, 200, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiClickResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x001B0E24",
                ElementClicked = null,
                ActionTaken = "mouse_click",
                ScreenX = 350,
                ScreenY = 250,
                WindowRelativeX = 300,
                WindowRelativeY = 200
            });

        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""click"", ""target"": ""hon_x64"", ""x"": 300, ""y"": 200 }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("windowRelativeX").GetInt32().Should().Be(300);
        root.GetProperty("windowRelativeY").GetInt32().Should().Be(200);
    }

    [Fact]
    public async Task UiControlHandler_Invoke_SetsUseInvokePatternTrue()
    {
        Mock<IUiControlService> mockService = new();
        mockService.Setup(s => s.ClickAsync("hon_x64", "SubmitButton", null, null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiClickResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x001B0E24",
                ElementClicked = "SubmitButton",
                ActionTaken = "invoke_pattern",
                ScreenX = 500,
                ScreenY = 400
            });

        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""invoke"", ""target"": ""hon_x64"", ""element_name"": ""SubmitButton"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("actionTaken").GetString().Should().Be("invoke_pattern");
    }

    [Fact]
    public async Task UiControlHandler_Type_DispatchesText()
    {
        Mock<IUiControlService> mockService = new();
        mockService.Setup(s => s.TypeTextAsync("hon_x64", "StartGame practice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiTypeResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x001B0E24",
                CharactersTyped = 18,
                Text = "StartGame practice"
            });

        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""type"", ""target"": ""hon_x64"", ""text"": ""StartGame practice"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("charactersTyped").GetInt32().Should().Be(18);
        root.GetProperty("text").GetString().Should().Be("StartGame practice");
    }

    [Fact]
    public async Task UiControlHandler_Type_MissingText_ReturnsError()
    {
        Mock<IUiControlService> mockService = new();
        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""type"", ""target"": ""hon_x64"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        (result.Content[0] as TextContentItem)!.Text.Should().Contain("Missing required parameter 'text'");
    }

    [Fact]
    public async Task UiControlHandler_Key_DispatchesKeyName()
    {
        Mock<IUiControlService> mockService = new();
        mockService.Setup(s => s.SendKeyAsync("hon_x64", "F8", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiKeyResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x001B0E24",
                KeyName = "F8",
                VirtualKeyCode = 0x77
            });

        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""key"", ""target"": ""hon_x64"", ""key"": ""F8"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("key").GetString().Should().Be("F8");
        root.GetProperty("virtualKeyCode").GetInt32().Should().Be(0x77);
    }

    [Fact]
    public async Task UiControlHandler_Key_MissingKey_ReturnsError()
    {
        Mock<IUiControlService> mockService = new();
        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""key"", ""target"": ""hon_x64"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        (result.Content[0] as TextContentItem)!.Text.Should().Contain("Missing required parameter 'key'");
    }

    [Fact]
    public async Task UiControlHandler_UnknownAction_ReturnsError()
    {
        Mock<IUiControlService> mockService = new();
        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""invalid_action_xyz"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        (result.Content[0] as TextContentItem)!.Text.Should().Contain("Unknown action");
    }

    [Fact]
    public async Task UiControlHandler_StatusAction_AliasesToInspect()
    {
        Mock<IUiControlService> mockService = new();
        mockService.Setup(s => s.InspectUiAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiInspectResult
            {
                Success = true,
                Target = "foreground",
                ControlCount = 1,
                Controls = []
            });

        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""status"", ""target"": ""foreground"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        mockService.Verify(s => s.InspectUiAsync("foreground", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UiControlHandler_KeyTapAction_AliasesToKey()
    {
        Mock<IUiControlService> mockService = new();
        mockService.Setup(s => s.SendKeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiKeyResult { Success = true, Target = "hon_x64", KeyName = "Enter" });

        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""key_tap"", ""target"": ""hon_x64"", ""key"": ""Enter"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        mockService.Verify(s => s.SendKeyAsync("hon_x64", "Enter", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UiControlHandler_MouseClickAction_AliasesToClick()
    {
        Mock<IUiControlService> mockService = new();
        mockService.Setup(s => s.ClickAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiClickResult { Success = true, Target = "hon_x64" });

        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""mouse_click"", ""target"": ""hon_x64"", ""x"": 100, ""y"": 200 }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        mockService.Verify(s => s.ClickAsync("hon_x64", null, 100, 200, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UiControlHandler_WriteAction_AliasesToType()
    {
        Mock<IUiControlService> mockService = new();
        mockService.Setup(s => s.TypeTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiTypeResult { Success = true, Target = "hon_x64", Text = "hello" });

        UiControlHandler handler = new(mockService.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""write"", ""target"": ""hon_x64"", ""text"": ""hello"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        mockService.Verify(s => s.TypeTextAsync("hon_x64", "hello", It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region McpToolRegistry Integration Tests

    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersUiControlAndAlias()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool("kongor_ui_control").Should().BeTrue();
        registry.HasTool("kongor_ui_controller").Should().BeTrue();

        ToolDefinition? def = registry.GetTool("kongor_ui_control");
        def.Should().NotBeNull();
        def!.InputSchema.Properties.Should().ContainKey("action");
        def.InputSchema.Properties.Should().ContainKey("target");
        def.InputSchema.Properties.Should().ContainKey("element_name");
        def.InputSchema.Properties.Should().ContainKey("x");
        def.InputSchema.Properties.Should().ContainKey("y");
        def.InputSchema.Properties.Should().ContainKey("use_invoke_pattern");
        def.InputSchema.Properties.Should().ContainKey("text");
        def.InputSchema.Properties.Should().ContainKey("key");
        def.InputSchema.Required.Should().Contain("action");
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_UiControl_UnknownAction_ReturnsError()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""bad_action"" }");

        ToolCallResult result = await registry.ExecuteToolAsync("kongor_ui_control", args);

        result.IsError.Should().BeTrue();
        (result.Content[0] as TextContentItem)!.Text.Should().Contain("Unknown action");
    }

    [Fact]
    public void AntigravityRegistrar_WithValidArguments_GeneratesValidUiControlSchemaJson()
    {
        ToolDefinition toolDef = McpToolRegistry.CreateUiControlDefinition("kongor_ui_control");
        string json = AntigravityRegistrar.GenerateToolSchemaJson(toolDef);

        json.Should().NotBeNullOrWhiteSpace();
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("name").GetString().Should().Be("kongor_ui_control");
        doc.RootElement.GetProperty("parameters").GetProperty("properties").Should().NotBeNull();
    }

    [Fact]
    public async Task UiControlService_ClickAsync_WithNonExistentPid_ReturnsStructuredDiagnosticError()
    {
        UiControlService service = new();
        UiClickResult result = await service.ClickAsync("pid:999999", null, 10, 10, false);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("Target window 'pid:999999' not found");
    }

    #endregion
}
