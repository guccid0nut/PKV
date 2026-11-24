// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Interop.UIAutomationClient;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Bounding rectangle in screen coordinates.
/// </summary>
public sealed record UiBoundingRectangle(int Left, int Top, int Width, int Height);

/// <summary>
/// Discovered UI control element within a window tree.
/// </summary>
public sealed record UiControlElement(
    string Name,
    string AutomationId,
    string ControlType,
    UiBoundingRectangle Bounds,
    bool IsEnabled,
    string ClassName = "",
    string Hwnd = "0x0"
);

/// <summary>
/// Result of an InspectUi operation.
/// </summary>
public sealed record UiInspectResult
{
    public bool Success { get; init; }
    public string Target { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string Hwnd { get; init; } = "0x00000000";
    public int Pid { get; init; }
    public int ControlCount { get; init; }
    public IReadOnlyList<UiControlElement> Controls { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of a Click operation.
/// </summary>
public sealed record UiClickResult
{
    public bool Success { get; init; }
    public string Target { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string Hwnd { get; init; } = "0x00000000";
    public string? ElementClicked { get; init; }
    public string ActionTaken { get; init; } = "none";
    public int ScreenX { get; init; }
    public int ScreenY { get; init; }
    public int WindowRelativeX { get; init; }
    public int WindowRelativeY { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of a TypeText operation.
/// </summary>
public sealed record UiTypeResult
{
    public bool Success { get; init; }
    public string Target { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string Hwnd { get; init; } = "0x00000000";
    public int CharactersTyped { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of a SendKey operation.
/// </summary>
public sealed record UiKeyResult
{
    public bool Success { get; init; }
    public string Target { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string Hwnd { get; init; } = "0x00000000";
    public string KeyName { get; init; } = string.Empty;
    public int VirtualKeyCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of an in-game console command actuation operation.
/// </summary>
public sealed record UiConsoleCommandResult
{
    public bool Success { get; init; }
    public string Target { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string Hwnd { get; init; } = "0x00000000";
    public string Command { get; init; } = string.Empty;
    public double ExecutionDurationMs { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of a Drag operation.
/// </summary>
public sealed record UiDragResult
{
    public bool Success { get; init; }
    public string Target { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string Hwnd { get; init; } = "0x00000000";
    public int StartX { get; init; }
    public int StartY { get; init; }
    public int EndX { get; init; }
    public int EndY { get; init; }
    public double DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Service interface for universal UI control and automation.
/// </summary>
public interface IUiControlService
{
    Task<UiInspectResult> InspectUiAsync(string target, CancellationToken ct = default);

    Task<UiClickResult> ClickAsync(
        string target,
        string? elementName = null,
        int? x = null,
        int? y = null,
        bool useInvokePattern = false,
        CancellationToken ct = default);

    Task<UiDragResult> DragAsync(
        string target,
        int startX,
        int startY,
        int endX,
        int endY,
        int durationMs = 300,
        CancellationToken ct = default);

    Task<UiTypeResult> TypeTextAsync(string target, string text, CancellationToken ct = default);

    Task<UiKeyResult> SendKeyAsync(string target, string keyName, CancellationToken ct = default);

    Task<UiConsoleCommandResult> ExecuteInGameConsoleCommandAsync(string target, string command, CancellationToken ct = default);

    Task<UiConsoleCommandResult> DismissInGameConsoleAsync(string target, CancellationToken ct = default);
}

/// <summary>
/// Comprehensive UI control and automation engine using Windows UI Automation COM (Interop.UIAutomationClient)
/// and Win32 P/Invoke with PerMonitorV2 DPI awareness.
/// </summary>
public sealed class UiControlService : IUiControlService
{
    private readonly ILogger<UiControlService> _logger;
    private readonly TimeProvider _timeProvider;
    private static bool _dpiAwarenessSet;
    private static readonly Lock _dpiLock = new();

    public ScrcpyDroppableControlQueue<UiControlEvent> ControlQueue { get; } = new();
    public ScrcpyAckSyncBarrier AckSyncBarrier { get; } = new();

    public UiControlService(ILogger<UiControlService>? logger = null, TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<UiControlService>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        EnsureDpiAwareness();
    }

    public bool EnqueueControlEvent(UiControlEvent controlEvent)
    {
        bool isDroppable = controlEvent.Kind is UiControlEventKind.MouseMove or UiControlEventKind.MouseHover or UiControlEventKind.MouseScroll;
        return ControlQueue.Push(controlEvent, isDroppable);
    }

    private void EnsureDpiAwareness()
    {
        if (_dpiAwarenessSet) return;
        lock (_dpiLock)
        {
            if (_dpiAwarenessSet) return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    SetProcessDpiAwarenessContext(new IntPtr(-4)); // PerMonitorV2
                    LogDiag("[UiControlService] Initialized PerMonitorV2 DPI awareness context.");
                }
            }
            catch (Exception ex)
            {
                LogDiag($"[UiControlService] Warning: Failed to set DPI awareness: {ex.Message}");
            }
            finally
            {
                _dpiAwarenessSet = true;
            }
        }
    }

    private void LogDiag(string message)
    {
        _logger.LogInformation("{Message}", message);
        try
        {
            Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] {message}");
        }
        catch
        {
        }
    }

    private void LogWarn(string message)
    {
        _logger.LogWarning("{Message}", message);
        try
        {
            Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [WARN] {message}");
        }
        catch
        {
        }
    }

    private void LogError(Exception ex, string message)
    {
        _logger.LogError(ex, "{Message}", message);
        try
        {
            Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [ERROR] {message}: {ex.Message}");
        }
        catch
        {
        }
    }

    private void LogErr(string message, Exception? ex = null)
    {
        _logger.LogError(ex, "{Message}", message);
        try
        {
            Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [ERROR] {message} {ex?.Message}");
        }
        catch
        {
        }
    }

    #region 1. InspectUiAsync

    public Task<UiInspectResult> InspectUiAsync(string target, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new UiInspectResult { Success = false, Target = target, ErrorMessage = "UI automation is Windows-only" });
        }

        using IDisposable? desktopScope = EnsureDesktopAttachment();
        LogDiag($"[UiControlService] Starting UI inspection for target: '{target}'");

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            LogWarn($"[UiControlService] Window not found for target: '{target}'");
            return Task.FromResult(new UiInspectResult
            {
                Success = false,
                Target = target,
                ErrorMessage = $"Target window not found for query '{target}'. Ensure target process/window is active."
            });
        }

        string hwndHex = $"0x{hwnd.ToInt64():X8}";
        LogDiag($"[UiControlService] Inspecting window '{windowTitle}' (HWND: {hwndHex}, PID: {pid})");

        List<UiControlElement> controls = [];

        // 1. Traverse via UI Automation COM
        try
        {
            IUIAutomation? uia = CreateAutomation();
            if (uia != null)
            {
                IUIAutomationElement root = uia.ElementFromHandle(hwnd);
                if (root != null)
                {
                    IUIAutomationCondition trueCondition = uia.CreateTrueCondition();
                    IUIAutomationElementArray elementArray = root.FindAll(TreeScope.TreeScope_Subtree, trueCondition);
                    int count = elementArray?.Length ?? 0;
                    LogDiag($"[UiControlService] UI Automation discovered {count} elements in subtree.");

                    int maxElements = Math.Min(count, 500); // Guard against massive control trees
                    for (int i = 0; i < maxElements; i++)
                    {
                        if (ct.IsCancellationRequested) break;

                        try
                        {
                            IUIAutomationElement elem = elementArray!.GetElement(i);
                            if (elem == null) continue;

                            string name = elem.CurrentName ?? string.Empty;
                            string autoId = elem.CurrentAutomationId ?? string.Empty;
                            int controlTypeId = elem.CurrentControlType;
                            string controlType = MapControlTypeId(controlTypeId);
                            tagRECT r = elem.CurrentBoundingRectangle;
                            int w = r.right - r.left;
                            int h = r.bottom - r.top;
                            bool isEnabled = elem.CurrentIsEnabled != 0;
                            string className = elem.CurrentClassName ?? string.Empty;

                            // Filter to interactive controls or controls with descriptive identities
                            bool isInteractive = IsInteractiveControlType(controlTypeId);
                            bool hasIdentity = !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(autoId);

                            if ((isInteractive || hasIdentity) && (w > 0 && h > 0))
                            {
                                controls.Add(new UiControlElement(
                                    Name: name,
                                    AutomationId: autoId,
                                    ControlType: controlType,
                                    Bounds: new UiBoundingRectangle(r.left, r.top, w, h),
                                    IsEnabled: isEnabled,
                                    ClassName: className,
                                    Hwnd: hwndHex
                                ));
                            }
                        }
                        catch (Exception exElem)
                        {
                            LogDiag($"[UiControlService] Note: Failed to inspect subtree element #{i}: {exElem.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception exUia)
        {
            LogWarn($"[UiControlService] UIA COM inspection encountered error: {exUia.Message}. Falling back to Win32.");
        }

        // 2. Win32 P/Invoke fallback/augmentation if UIA produced no interactive elements
        if (controls.Count == 0)
        {
            LogDiag("[UiControlService] No controls found via UIA. Traversing Win32 child windows...");
            try
            {
                EnumChildWindows(hwnd, (childHwnd, _) =>
                {
                    if (ct.IsCancellationRequested) return false;

                    if (IsWindowVisible(childHwnd))
                    {
                        string childTitle = GetWindowTitle(childHwnd);
                        string childClass = GetWindowClassName(childHwnd);
                        int ctrlId = GetDlgCtrlID(childHwnd);
                        bool enabled = IsWindowEnabled(childHwnd);

                        if (GetWindowRect(childHwnd, out RECT childRect))
                        {
                            int cw = childRect.Right - childRect.Left;
                            int ch = childRect.Bottom - childRect.Top;
                            if (cw > 0 && ch > 0)
                            {
                                controls.Add(new UiControlElement(
                                    Name: childTitle,
                                    AutomationId: ctrlId != 0 ? ctrlId.ToString() : childClass,
                                    ControlType: MapWin32ClassNameToControlType(childClass),
                                    Bounds: new UiBoundingRectangle(childRect.Left, childRect.Top, cw, ch),
                                    IsEnabled: enabled,
                                    ClassName: childClass,
                                    Hwnd: $"0x{childHwnd.ToInt64():X8}"
                                ));
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception exWin32)
            {
                LogWarn($"[UiControlService] Win32 child window traversal exception: {exWin32.Message}");
            }
        }

        LogDiag($"[UiControlService] UI Inspection complete. Found {controls.Count} controls.");

        return Task.FromResult(new UiInspectResult
        {
            Success = true,
            Target = target,
            WindowTitle = windowTitle,
            Hwnd = hwndHex,
            Pid = pid,
            ControlCount = controls.Count,
            Controls = controls
        });
    }

    #endregion

    #region 2. ClickAsync

    private static bool IsOffscreenBounds(RECT bounds) =>
        bounds.Left <= -3000 || bounds.Top <= -3000 || bounds.Left >= 30000 || bounds.Top >= 30000;

    public async Task<UiClickResult> ClickAsync(
        string target,
        string? elementName = null,
        int? x = null,
        int? y = null,
        bool useInvokePattern = false,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UiClickResult { Success = false, Target = target, ErrorMessage = "UI automation is Windows-only" };
        }

        using IDisposable? desktopScope = EnsureDesktopAttachment();
        LogDiag($"[UiControlService] Starting ClickAsync for target '{target}'. Element: '{elementName}', RelCoords: ({x},{y}), InvokePattern: {useInvokePattern}");

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            LogWarn($"[UiControlService] Target window '{target}' not found for click.");
            return new UiClickResult
            {
                Success = false,
                Target = target,
                ErrorMessage = BuildWindowNotFoundErrorMessage(target, pid)
            };
        }

        string hwndHex = $"0x{hwnd.ToInt64():X8}";
        RECT windowBounds = GetAccurateWindowBounds(hwnd);
        int winWidth = windowBounds.Right - windowBounds.Left;
        int winHeight = windowBounds.Bottom - windowBounds.Top;

        if (IsOffscreenBounds(windowBounds))
        {
            LogWarn($"[UiControlService] Refusing OS-level mouse click on offscreen window '{windowTitle}' at ({windowBounds.Left}, {windowBounds.Top}) (MCP-289).");
            return new UiClickResult
            {
                Success = false,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                ElementClicked = elementName,
                ErrorMessage = $"Refusing to synthesize OS-level mouse click for offscreen window '{windowTitle}' at ({windowBounds.Left}, {windowBounds.Top}). Use in-engine widget dispatch ('click_widget', 'dispatch_event', 'invoke' with 'widget') or 'kongor_client_navigate'."
            };
        }

        // 1. If elementName is specified: locate element by name or AutomationId
        if (!string.IsNullOrWhiteSpace(elementName))
        {
            string cleanName = elementName.Trim();
            LogDiag($"[UiControlService] Searching for element '{cleanName}' in HWND {hwndHex}...");

            IUIAutomationElement? matchedElement = null;
            try
            {
                IUIAutomation? uia = CreateAutomation();
                if (uia != null)
                {
                    IUIAutomationElement root = uia.ElementFromHandle(hwnd);
                    if (root != null)
                    {
                        IUIAutomationElementArray all = root.FindAll(TreeScope.TreeScope_Subtree, uia.CreateTrueCondition());
                        int len = all?.Length ?? 0;

                        // Pass 1: Exact match on Name or AutomationId
                        for (int i = 0; i < len; i++)
                        {
                            IUIAutomationElement candidate = all!.GetElement(i);
                            if (candidate == null) continue;

                            string curName = candidate.CurrentName ?? string.Empty;
                            string curId = candidate.CurrentAutomationId ?? string.Empty;

                            if (curName.Equals(cleanName, StringComparison.OrdinalIgnoreCase) ||
                                curId.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedElement = candidate;
                                break;
                            }
                        }

                        // Pass 2: Substring match if exact match not found
                        if (matchedElement == null)
                        {
                            for (int i = 0; i < len; i++)
                            {
                                IUIAutomationElement candidate = all!.GetElement(i);
                                if (candidate == null) continue;

                                string curName = candidate.CurrentName ?? string.Empty;
                                string curId = candidate.CurrentAutomationId ?? string.Empty;

                                if (curName.Contains(cleanName, StringComparison.OrdinalIgnoreCase) ||
                                    curId.Contains(cleanName, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedElement = candidate;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception exSearch)
            {
                LogWarn($"[UiControlService] UIA element search exception: {exSearch.Message}");
            }

            if (matchedElement != null)
            {
                string foundName = matchedElement.CurrentName ?? matchedElement.CurrentAutomationId ?? cleanName;
                tagRECT elemRect = matchedElement.CurrentBoundingRectangle;
                int elemW = elemRect.right - elemRect.left;
                int elemH = elemRect.bottom - elemRect.top;

                int screenCenterX = elemRect.left + (elemW / 2);
                int screenCenterY = elemRect.top + (elemH / 2);
                int relX = screenCenterX - windowBounds.Left;
                int relY = screenCenterY - windowBounds.Top;

                // Option A: Direct InvokePattern invocation if requested
                if (useInvokePattern)
                {
                    try
                    {
                        object patternObj = matchedElement.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId);
                        if (patternObj is IUIAutomationInvokePattern invokePattern)
                        {
                            LogDiag($"[UiControlService] Invoking IUIAutomationInvokePattern on element '{foundName}'");
                            invokePattern.Invoke();

                            return new UiClickResult
                            {
                                Success = true,
                                Target = target,
                                WindowTitle = windowTitle,
                                Hwnd = hwndHex,
                                ElementClicked = foundName,
                                ActionTaken = "invoke_pattern",
                                ScreenX = screenCenterX,
                                ScreenY = screenCenterY,
                                WindowRelativeX = relX,
                                WindowRelativeY = relY
                            };
                        }
                        LogDiag($"[UiControlService] InvokePattern not available for '{foundName}'. Falling back to mouse click.");
                    }
                    catch (Exception exInvoke)
                    {
                        LogWarn($"[UiControlService] InvokePattern invocation failed ({exInvoke.Message}). Falling back to mouse click.");
                    }
                }

                // Option B: Mouse click on element center
                LogDiag($"[UiControlService] Dispatching mouse click at element center ({screenCenterX}, {screenCenterY})");
                BringWindowToForeground(hwnd);
                await Task.Delay(50, ct);

                await SendHardwareLeftClickAsync(screenCenterX, screenCenterY, ct);

                return new UiClickResult
                {
                    Success = true,
                    Target = target,
                    WindowTitle = windowTitle,
                    Hwnd = hwndHex,
                    ElementClicked = foundName,
                    ActionTaken = "mouse_click",
                    ScreenX = screenCenterX,
                    ScreenY = screenCenterY,
                    WindowRelativeX = relX,
                    WindowRelativeY = relY
                };
            }

            // Win32 child window search fallback for elementName
            IntPtr foundChild = FindChildWindowByTextOrId(hwnd, cleanName);
            if (foundChild != IntPtr.Zero && GetWindowRect(foundChild, out RECT childR))
            {
                int cW = childR.Right - childR.Left;
                int cH = childR.Bottom - childR.Top;
                int screenCenterX = childR.Left + (cW / 2);
                int screenCenterY = childR.Top + (cH / 2);
                int relX = screenCenterX - windowBounds.Left;
                int relY = screenCenterY - windowBounds.Top;

                LogDiag($"[UiControlService] Located Win32 child window 0x{foundChild.ToInt64():X8} for element '{cleanName}'. Dispatching click.");
                BringWindowToForeground(hwnd);
                await Task.Delay(50, ct);

                await SendHardwareLeftClickAsync(screenCenterX, screenCenterY, ct);

                return new UiClickResult
                {
                    Success = true,
                    Target = target,
                    WindowTitle = windowTitle,
                    Hwnd = hwndHex,
                    ElementClicked = cleanName,
                    ActionTaken = "mouse_click",
                    ScreenX = screenCenterX,
                    ScreenY = screenCenterY,
                    WindowRelativeX = relX,
                    WindowRelativeY = relY
                };
            }

            LogWarn($"[UiControlService] Element '{cleanName}' could not be found in window {hwndHex}");
            return new UiClickResult
            {
                Success = false,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                ElementClicked = cleanName,
                ErrorMessage = $"Element '{cleanName}' was not found in target window '{windowTitle}'."
            };
        }

        BringWindowToForeground(hwnd);
        await Task.Delay(50, ct).ConfigureAwait(false);

        // 2. If window-relative coordinates x and y are specified
        int clickRelX = x ?? (winWidth / 2);
        int clickRelY = y ?? (winHeight / 2);
        int clickScreenX;
        int clickScreenY;
        POINT pt = new POINT { X = clickRelX, Y = clickRelY };
        if (ClientToScreen(hwnd, ref pt))
        {
            clickScreenX = pt.X;
            clickScreenY = pt.Y;
        }
        else
        {
            clickScreenX = windowBounds.Left + clickRelX;
            clickScreenY = windowBounds.Top + clickRelY;
        }

        LogDiag($"[UiControlService] Clicking coordinates relative: ({clickRelX}, {clickRelY}) -> screen: ({clickScreenX}, {clickScreenY})");

        await SendHardwareLeftClickAsync(clickScreenX, clickScreenY, ct);

        return new UiClickResult
        {
            Success = true,
            Target = target,
            WindowTitle = windowTitle,
            Hwnd = hwndHex,
            ElementClicked = elementName,
            ActionTaken = "mouse_click",
            ScreenX = clickScreenX,
            ScreenY = clickScreenY,
            WindowRelativeX = clickRelX,
            WindowRelativeY = clickRelY
        };
    }

    #endregion

    #region 2b. DragAsync

    public async Task<UiDragResult> DragAsync(
        string target,
        int startX,
        int startY,
        int endX,
        int endY,
        int durationMs = 300,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UiDragResult { Success = false, Target = target, ErrorMessage = "UI automation is Windows-only" };
        }

        using IDisposable? desktopScope = EnsureDesktopAttachment();
        ct.ThrowIfCancellationRequested();

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return new UiDragResult
            {
                Success = false,
                Target = target,
                ErrorMessage = BuildWindowNotFoundErrorMessage(target, pid)
            };
        }

        string hwndHex = $"0x{hwnd.ToInt64():X8}";
        RECT windowBounds = GetAccurateWindowBounds(hwnd);
        if (IsOffscreenBounds(windowBounds))
        {
            LogWarn($"[UiControlService] Refusing OS-level mouse drag on offscreen window '{windowTitle}' at ({windowBounds.Left}, {windowBounds.Top}) (MCP-289).");
            return new UiDragResult
            {
                Success = false,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                ErrorMessage = $"Refusing to synthesize OS-level mouse drag for offscreen window '{windowTitle}' at ({windowBounds.Left}, {windowBounds.Top})."
            };
        }

        BringWindowToForeground(hwnd);
        await Task.Delay(50, ct).ConfigureAwait(false);
        int screenStartX;
        int screenStartY;
        int screenEndX;
        int screenEndY;

        POINT pStart = new POINT { X = startX, Y = startY };
        if (ClientToScreen(hwnd, ref pStart))
        {
            screenStartX = pStart.X;
            screenStartY = pStart.Y;
        }
        else
        {
            screenStartX = windowBounds.Left + startX;
            screenStartY = windowBounds.Top + startY;
        }

        POINT pEnd = new POINT { X = endX, Y = endY };
        if (ClientToScreen(hwnd, ref pEnd))
        {
            screenEndX = pEnd.X;
            screenEndY = pEnd.Y;
        }
        else
        {
            screenEndX = windowBounds.Left + endX;
            screenEndY = windowBounds.Top + endY;
        }

        LogDiag($"[UiControlService] Dragging relative ({startX},{startY}) -> ({endX},{endY}) [screen ({screenStartX},{screenStartY}) -> ({screenEndX},{screenEndY})] on '{windowTitle}' over {durationMs}ms");

        Stopwatch sw = Stopwatch.StartNew();
        await SendHardwareDragAsync(screenStartX, screenStartY, screenEndX, screenEndY, durationMs, ct);
        sw.Stop();

        return new UiDragResult
        {
            Success = true,
            Target = target,
            WindowTitle = windowTitle,
            Hwnd = hwndHex,
            StartX = startX,
            StartY = startY,
            EndX = endX,
            EndY = endY,
            DurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
        };
    }

    #endregion

    #region 3. TypeTextAsync

    public async Task<UiTypeResult> TypeTextAsync(string target, string text, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UiTypeResult { Success = false, Target = target, ErrorMessage = "UI automation is Windows-only" };
        }

        using IDisposable? desktopScope = EnsureDesktopAttachment();
        LogDiag($"[UiControlService] Starting TypeTextAsync for target '{target}' with text length {text?.Length ?? 0}");

        if (string.IsNullOrEmpty(text))
        {
            return new UiTypeResult
            {
                Success = true,
                Target = target,
                CharactersTyped = 0,
                Text = string.Empty
            };
        }

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            LogWarn($"[UiControlService] Target window '{target}' not found for typing.");
            return new UiTypeResult
            {
                Success = false,
                Target = target,
                ErrorMessage = BuildWindowNotFoundErrorMessage(target, pid)
            };
        }

        string hwndHex = $"0x{hwnd.ToInt64():X8}";
        RECT typeBounds = GetAccurateWindowBounds(hwnd);
        if (IsOffscreenBounds(typeBounds))
        {
            LogWarn($"[UiControlService] Refusing OS-level keyboard typing on offscreen window '{windowTitle}' at ({typeBounds.Left}, {typeBounds.Top}) (MCP-289).");
            return new UiTypeResult
            {
                Success = false,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                ErrorMessage = $"Cannot send OS-level keystrokes to offscreen window '{windowTitle}' at ({typeBounds.Left}, {typeBounds.Top})."
            };
        }

        BringWindowToForeground(hwnd);
        await Task.Delay(75, ct);

        LogDiag($"[UiControlService] Dispatching {text.Length} characters to window {hwndHex}...");

        int typedCount = 0;
        foreach (char c in text)
        {
            if (ct.IsCancellationRequested) break;

            // Send Unicode KeyDown & KeyUp via SendInput
            SendUnicodeChar(c);
            typedCount++;
            await Task.Delay(15, ct); // Natural actuation pacing
        }

        LogDiag($"[UiControlService] Successfully dispatched {typedCount} characters.");

        return new UiTypeResult
        {
            Success = true,
            Target = target,
            WindowTitle = windowTitle,
            Hwnd = hwndHex,
            CharactersTyped = typedCount,
            Text = text
        };
    }

    #endregion

    #region 4. SendKeyAsync

    public async Task<UiKeyResult> SendKeyAsync(string target, string keyName, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UiKeyResult { Success = false, Target = target, KeyName = keyName, ErrorMessage = "UI automation is Windows-only" };
        }

        using IDisposable? desktopScope = EnsureDesktopAttachment();
        LogDiag($"[UiControlService] Starting SendKeyAsync for target '{target}', Key: '{keyName}'");

        if (string.IsNullOrWhiteSpace(keyName))
        {
            return new UiKeyResult
            {
                Success = false,
                Target = target,
                ErrorMessage = "Key name cannot be null or empty."
            };
        }

        byte vk = ResolveVirtualKeyCode(keyName);
        if (vk == 0)
        {
            LogWarn($"[UiControlService] Unknown key name: '{keyName}'");
            return new UiKeyResult
            {
                Success = false,
                Target = target,
                KeyName = keyName,
                ErrorMessage = $"Unrecognized key name '{keyName}'. Supported: Enter, Tab, Escape, F8, Space, Backspace, Delete, Up, Down, Left, Right, etc."
            };
        }

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            LogWarn($"[UiControlService] Target window '{target}' not found for key dispatch.");
            return new UiKeyResult
            {
                Success = false,
                Target = target,
                KeyName = keyName,
                VirtualKeyCode = vk,
                ErrorMessage = BuildWindowNotFoundErrorMessage(target, pid)
            };
        }

        string hwndHex = $"0x{hwnd.ToInt64():X8}";
        RECT keyBounds = GetAccurateWindowBounds(hwnd);
        if (IsOffscreenBounds(keyBounds))
        {
            LogWarn($"[UiControlService] Refusing OS-level key '{keyName}' on offscreen window '{windowTitle}' at ({keyBounds.Left}, {keyBounds.Top}) (MCP-289).");
            return new UiKeyResult
            {
                Success = false,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                KeyName = keyName,
                VirtualKeyCode = vk,
                ErrorMessage = $"Cannot send keystrokes to offscreen window at ({keyBounds.Left}, {keyBounds.Top})"
            };
        }

        BringWindowToForeground(hwnd);
        await Task.Delay(75, ct);

        LogDiag($"[UiControlService] Dispatching key '{keyName}' (VK: 0x{vk:X2}) to window {hwndHex}...");

        // Key down
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        await Task.Delay(100, ct); // Hold duration (100ms)
        // Key up
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

        LogDiag($"[UiControlService] Key '{keyName}' dispatched successfully.");

        return new UiKeyResult
        {
            Success = true,
            Target = target,
            WindowTitle = windowTitle,
            Hwnd = hwndHex,
            KeyName = keyName,
            VirtualKeyCode = vk
        };
    }

    #endregion

    #region 5. ExecuteInGameConsoleCommandAsync

    public Task<UiConsoleCommandResult> ExecuteInGameConsoleCommandAsync(string target, string command, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new UiConsoleCommandResult { Success = false, Target = target, Command = command, ErrorMessage = "UI automation is Windows-only" });
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            TaskCompletionSource<UiConsoleCommandResult> tcs = new TaskCompletionSource<UiConsoleCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new Thread(() =>
            {
                IntPtr hDesk = IntPtr.Zero;
                try
                {
                    hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                    if (hDesk != IntPtr.Zero)
                    {
                        SetThreadDesktop(hDesk);
                    }
                    UiConsoleCommandResult result = ExecuteInGameConsoleCommandCoreSync(target, command, ct);
                    tcs.SetResult(result);
                }
                catch (OperationCanceledException)
                {
                    tcs.SetCanceled(ct);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                finally
                {
                    if (hDesk != IntPtr.Zero)
                    {
                        try { CloseDesktop(hDesk); } catch { }
                    }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        return ExecuteInGameConsoleCommandCoreAsync(target, command, ct);
    }

    private UiConsoleCommandResult ExecuteInGameConsoleCommandCoreSync(string target, string command, CancellationToken ct = default)
    {
        LogDiag($"[UiControlService] Executing in-game console command for target '{target}': '{command}'");

        if (string.IsNullOrWhiteSpace(command))
        {
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                Command = command,
                ErrorMessage = "Command cannot be null or empty."
            };
        }

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            LogWarn($"[UiControlService] Target window '{target}' not found for console command execution.");
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                Command = command,
                ErrorMessage = $"Target game window '{target}' not found. Ensure the game client is running."
            };
        }

        string hwndHex = $"0x{hwnd.ToInt64():X8}";
        Stopwatch sw = Stopwatch.StartNew();

        if (IsWindowOffscreen(hwnd))
        {
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                Command = command,
                ExecutionDurationMs = sw.Elapsed.TotalMilliseconds,
                ErrorMessage = "Window is offscreen (headless mode). Keystroke simulation is disabled to prevent desktop interference. Route commands via Named Pipe IPC (\\\\.\\pipe\\willowmaker-ipc) or switch window mode to foreground."
            };
        }

        try
        {
            LogDiag($"[UiControlService] Setting command onto clipboard ({command.Length} chars)...");
            if (!SetClipboardText(command, ct))
            {
                LogWarn("[UiControlService] Failed to set clipboard text. Proceeding with execution attempt.");
            }

            BringWindowToForeground(hwnd);
            WaitWithCancellation(100, ct);

            // Pre-flight: send Escape to ensure any open in-game chat box (preventing chat spill!) is cancelled
            LogDiag($"[UiControlService] Pre-flight: ensuring active chat prompt is dismissed via Escape on {hwndHex}...");
            EnsureDesktopAttachment();
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            try
            {
                WaitWithCancellation(40, ct);
            }
            finally
            {
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            WaitWithCancellation(100, ct);

            // 1. Send Ctrl+F8 with a 40ms hold time to open the in-game console
            LogDiag($"[UiControlService] Opening K2 in-game console via Ctrl+F8 on {hwndHex} (40ms hold)...");
            SendKeyComboWithHoldSync(0x11, 0x77, 40, ct); // VK_CONTROL + VK_F8

            // 2. Wait 200ms for console dropdown
            WaitWithCancellation(200, ct);

            // 3. Send Ctrl+V with a 40ms hold time to paste the command
            LogDiag($"[UiControlService] Pasting command via Ctrl+V on {hwndHex} (40ms hold)...");
            SendKeyComboWithHoldSync(0x11, 0x56, 40, ct); // VK_CONTROL + 'V'

            // 4. Send Enter with a 40ms hold time to execute the command
            LogDiag($"[UiControlService] Executing command via Enter on {hwndHex} (40ms hold)...");
            EnsureDesktopAttachment();
            keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
            try
            {
                WaitWithCancellation(40, ct);
            }
            finally
            {
                keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }

            // 5. Wait 100ms, then send Ctrl+F8 to dismiss the console
            WaitWithCancellation(100, ct);
            LogDiag($"[UiControlService] Dismissing K2 in-game console via Ctrl+F8 on {hwndHex} (40ms hold)...");
            SendKeyComboWithHoldSync(0x11, 0x77, 40, ct);
            WaitWithCancellation(100, ct);
            // 6. Post-execution: console is closed via Ctrl+F8; allow focus to settle without spurious Escape key
            WaitWithCancellation(50, ct);

            sw.Stop();
            LogDiag($"[UiControlService] In-game console command executed successfully in {sw.ElapsedMilliseconds} ms.");

            return new UiConsoleCommandResult
            {
                Success = true,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                Command = command,
                ExecutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogWarn($"[UiControlService] Exception executing in-game console command on '{target}': {ex.Message}");
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                Command = command,
                ErrorMessage = $"Failed to execute in-game console command: {ex.Message}",
                ExecutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            };
        }
    }

    private async Task<UiConsoleCommandResult> ExecuteInGameConsoleCommandCoreAsync(string target, string command, CancellationToken ct = default)
    {
        LogDiag($"[UiControlService] Executing in-game console command for target '{target}': '{command}'");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                Command = command,
                ErrorMessage = "In-game console actuation is only supported on Windows operating systems."
            };
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                Command = command,
                ErrorMessage = "Command cannot be null or empty."
            };
        }

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            LogWarn($"[UiControlService] Target window '{target}' not found for console command execution.");
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                Command = command,
                ErrorMessage = $"Target game window '{target}' not found. Ensure the game client is running."
            };
        }

        string hwndHex = $"0x{hwnd.ToInt64():X8}";
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            // Set the command text onto the Windows clipboard
            LogDiag($"[UiControlService] Setting command onto clipboard ({command.Length} chars)...");
            if (!await SetClipboardTextAsync(command, ct).ConfigureAwait(false))
            {
                LogWarn("[UiControlService] Failed to set clipboard text. Proceeding with execution attempt.");
            }

            // Bring hon_x64 window to foreground
            BringWindowToForeground(hwnd);
            await Task.Delay(100, ct).ConfigureAwait(false);

            // Pre-flight: send Escape to ensure any open in-game chat box (preventing chat spill!) is cancelled
            LogDiag($"[UiControlService] Pre-flight: ensuring active chat prompt is dismissed via Escape on {hwndHex}...");
            EnsureDesktopAttachment();
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            try
            {
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
            finally
            {
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            await Task.Delay(100, ct).ConfigureAwait(false);

            // 1. Send Ctrl+F8 with a 40ms hold time to open the in-game console
            LogDiag($"[UiControlService] Opening K2 in-game console via Ctrl+F8 on {hwndHex} (40ms hold)...");
            await SendKeyComboWithHoldAsync(0x11, 0x77, 40, ct).ConfigureAwait(false); // VK_CONTROL + VK_F8

            // 2. Wait 200ms for console dropdown
            await Task.Delay(200, ct).ConfigureAwait(false);

            // 3. Send Ctrl+V with a 40ms hold time to paste the command
            LogDiag($"[UiControlService] Pasting command via Ctrl+V on {hwndHex} (40ms hold)...");
            await SendKeyComboWithHoldAsync(0x11, 0x56, 40, ct).ConfigureAwait(false); // VK_CONTROL + 'V'

            // 4. Send Enter with a 40ms hold time to execute the command
            LogDiag($"[UiControlService] Executing command via Enter on {hwndHex} (40ms hold)...");
            EnsureDesktopAttachment();
            keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
            try
            {
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
            finally
            {
                keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }

            // 5. Wait 100ms, then send Ctrl+F8 to dismiss the console
            await Task.Delay(100, ct).ConfigureAwait(false);
            LogDiag($"[UiControlService] Dismissing K2 in-game console via Ctrl+F8 on {hwndHex} (40ms hold)...");
            await SendKeyComboWithHoldAsync(0x11, 0x77, 40, ct).ConfigureAwait(false);
            await Task.Delay(100, ct).ConfigureAwait(false);

            // 6. Post-execution: send Escape to guarantee chat prompt is closed and focus returns to game UI
            EnsureDesktopAttachment();
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            try
            {
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
            finally
            {
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            await Task.Delay(50, ct).ConfigureAwait(false);

            sw.Stop();
            LogDiag($"[UiControlService] In-game console command executed successfully in {sw.ElapsedMilliseconds} ms.");

            return new UiConsoleCommandResult
            {
                Success = true,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                Command = command,
                ExecutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogWarn($"[UiControlService] Exception executing in-game console command on '{target}': {ex.Message}");
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                Command = command,
                ErrorMessage = $"Failed to execute in-game console command: {ex.Message}",
                ExecutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            };
        }
    }

    public Task<UiConsoleCommandResult> DismissInGameConsoleAsync(string target, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new UiConsoleCommandResult { Success = false, Target = target, ErrorMessage = "UI automation is Windows-only" });
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            TaskCompletionSource<UiConsoleCommandResult> tcs = new TaskCompletionSource<UiConsoleCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new Thread(() =>
            {
                IntPtr hDesk = IntPtr.Zero;
                try
                {
                    hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                    if (hDesk != IntPtr.Zero)
                    {
                        SetThreadDesktop(hDesk);
                    }
                    UiConsoleCommandResult result = DismissInGameConsoleCoreSync(target, ct);
                    tcs.SetResult(result);
                }
                catch (OperationCanceledException)
                {
                    tcs.SetCanceled(ct);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                finally
                {
                    if (hDesk != IntPtr.Zero)
                    {
                        try { CloseDesktop(hDesk); } catch { }
                    }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        return DismissInGameConsoleCoreAsync(target, ct);
    }

    private UiConsoleCommandResult DismissInGameConsoleCoreSync(string target, CancellationToken ct = default)
    {
        LogDiag($"[UiControlService] Dismissing in-game console / clearing input focus for target '{target}'");

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            LogWarn($"[UiControlService] Target window '{target}' not found for console dismissal.");
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                Command = "dismiss_console",
                ErrorMessage = $"Target game window '{target}' not found. Ensure the game client is running."
            };
        }

        string hwndHex = $"0x{hwnd.ToInt64():X8}";
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            BringWindowToForeground(hwnd);
            WaitWithCancellation(100, ct);

            // 1. Send Escape with 50ms hold time to cancel any active chat edit box prompt
            LogDiag($"[UiControlService] Step 1: Sending Escape on {hwndHex} to close active chat prompt...");
            EnsureDesktopAttachment();
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            try
            {
                WaitWithCancellation(50, ct);
            }
            finally
            {
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            WaitWithCancellation(100, ct);

            // 2. Send Ctrl+F8 with 50ms hold time to toggle dropdown console if it was open
            LogDiag($"[UiControlService] Step 2: Sending Ctrl+F8 on {hwndHex} to toggle console closed if active...");
            SendKeyComboWithHoldSync(0x11, 0x77, 50, ct); // VK_CONTROL + VK_F8
            WaitWithCancellation(150, ct);

            // 3. Send Escape once more with 50ms hold to ensure focus is clean in the viewport
            LogDiag($"[UiControlService] Step 3: Sending final Escape on {hwndHex} to guarantee neutral UI focus...");
            EnsureDesktopAttachment();
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            try
            {
                WaitWithCancellation(50, ct);
            }
            finally
            {
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            WaitWithCancellation(50, ct);

            sw.Stop();
            LogDiag($"[UiControlService] In-game console dismissed and input focus cleared in {sw.ElapsedMilliseconds} ms.");

            return new UiConsoleCommandResult
            {
                Success = true,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                Command = "dismiss_console",
                ExecutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogWarn($"[UiControlService] Exception dismissing in-game console on '{target}': {ex.Message}");
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                Command = "dismiss_console",
                ErrorMessage = $"Failed to dismiss in-game console: {ex.Message}",
                ExecutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            };
        }
    }

    private async Task<UiConsoleCommandResult> DismissInGameConsoleCoreAsync(string target, CancellationToken ct = default)
    {
        LogDiag($"[UiControlService] Dismissing in-game console / clearing input focus for target '{target}'");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                Command = "dismiss_console",
                ErrorMessage = "In-game console dismissal is only supported on Windows operating systems."
            };
        }

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            LogWarn($"[UiControlService] Target window '{target}' not found for console dismissal.");
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                Command = "dismiss_console",
                ErrorMessage = $"Target game window '{target}' not found. Ensure the game client is running."
            };
        }

        string hwndHex = $"0x{hwnd.ToInt64():X8}";
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            BringWindowToForeground(hwnd);
            await Task.Delay(100, ct).ConfigureAwait(false);

            // 1. Send Escape with 50ms hold time to cancel any active chat edit box prompt
            LogDiag($"[UiControlService] Step 1: Sending Escape on {hwndHex} to close active chat prompt...");
            EnsureDesktopAttachment();
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            finally
            {
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            await Task.Delay(100, ct).ConfigureAwait(false);

            // 2. Send Ctrl+F8 with 50ms hold time to toggle dropdown console if it was open
            LogDiag($"[UiControlService] Step 2: Sending Ctrl+F8 on {hwndHex} to toggle console closed if active...");
            await SendKeyComboWithHoldAsync(0x11, 0x77, 50, ct).ConfigureAwait(false); // VK_CONTROL + VK_F8
            await Task.Delay(150, ct).ConfigureAwait(false);

            // 3. Send Escape once more with 50ms hold to ensure focus is clean in the viewport
            LogDiag($"[UiControlService] Step 3: Sending final Escape on {hwndHex} to guarantee neutral UI focus...");
            EnsureDesktopAttachment();
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            finally
            {
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            await Task.Delay(50, ct).ConfigureAwait(false);

            sw.Stop();
            LogDiag($"[UiControlService] In-game console dismissed and input focus cleared in {sw.ElapsedMilliseconds} ms.");

            return new UiConsoleCommandResult
            {
                Success = true,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                Command = "dismiss_console",
                ExecutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogWarn($"[UiControlService] Exception dismissing in-game console on '{target}': {ex.Message}");
            return new UiConsoleCommandResult
            {
                Success = false,
                Target = target,
                WindowTitle = windowTitle,
                Hwnd = hwndHex,
                Command = "dismiss_console",
                ErrorMessage = $"Failed to dismiss in-game console: {ex.Message}",
                ExecutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            };
        }
    }

    private static async Task SendKeyComboWithHoldAsync(byte modifierVk, byte keyVk, int holdMs, CancellationToken ct)
    {
        EnsureDesktopAttachment();
        keybd_event(modifierVk, 0, 0, UIntPtr.Zero);
        keybd_event(keyVk, 0, 0, UIntPtr.Zero);
        try
        {
            await Task.Delay(holdMs, ct).ConfigureAwait(false);
        }
        finally
        {
            EnsureDesktopAttachment();
            keybd_event(keyVk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(modifierVk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }

    private static void SendKeyComboWithHoldSync(byte modifierVk, byte keyVk, int holdMs, CancellationToken ct)
    {
        EnsureDesktopAttachment();
        keybd_event(modifierVk, 0, 0, UIntPtr.Zero);
        keybd_event(keyVk, 0, 0, UIntPtr.Zero);
        try
        {
            WaitWithCancellation(holdMs, ct);
        }
        finally
        {
            EnsureDesktopAttachment();
            keybd_event(keyVk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(modifierVk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }

    private static void WaitWithCancellation(int ms, CancellationToken ct)
    {
        if (ct.WaitHandle.WaitOne(ms))
        {
            throw new OperationCanceledException(ct);
        }
    }

    private sealed class DesktopScope : IDisposable
    {
        private readonly IntPtr _hDesktop;

        public DesktopScope(IntPtr hDesktop)
        {
            _hDesktop = hDesktop;
        }

        public void Dispose()
        {
            if (_hDesktop != IntPtr.Zero)
            {
                try { CloseDesktop(_hDesktop); } catch { }
            }
        }
    }

    private static IntPtr s_cachedWinsta = IntPtr.Zero;
    private static readonly Lock s_winstaLock = new();

    private static void EnsureProcessWindowStation()
    {
        if (s_cachedWinsta != IntPtr.Zero) return;
        lock (s_winstaLock)
        {
            if (s_cachedWinsta != IntPtr.Zero) return;
            try
            {
                IntPtr hWinsta = OpenWindowStation("WinSta0", false, 0x000F037F);
                if (hWinsta != IntPtr.Zero)
                {
                    if (SetProcessWindowStation(hWinsta))
                    {
                        s_cachedWinsta = hWinsta;
                    }
                    else
                    {
                        CloseWindowStation(hWinsta);
                    }
                }
            }
            catch { }
        }
    }

    public static IDisposable? EnsureDesktopAttachment()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        EnsureProcessWindowStation();

        try
        {
            IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
            if (hDesk == IntPtr.Zero)
            {
                hDesk = OpenInputDesktop(0, false, 0x01FF);
            }

            if (hDesk != IntPtr.Zero)
            {
                SetThreadDesktop(hDesk);
                return new DesktopScope(hDesk);
            }
        }
        catch { }

        return null;
    }

    private static void SendKeyCombo(byte modifierVk, byte keyVk)
    {
        EnsureDesktopAttachment();
        keybd_event(modifierVk, 0, 0, UIntPtr.Zero);
        keybd_event(keyVk, 0, 0, UIntPtr.Zero);
        keybd_event(keyVk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(modifierVk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// Places text onto the Windows clipboard using Win32 API synchronously.
    /// </summary>
    public static bool SetClipboardText(string text, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        EnsureDesktopAttachment();

        bool opened = false;
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                opened = true;
                break;
            }
            if (ct.WaitHandle.WaitOne(10))
                return false;
        }

        if (!opened) return false;

        try
        {
            if (!EmptyClipboard()) return false;
            if (string.IsNullOrEmpty(text)) return true;

            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (hGlobal == IntPtr.Zero) return false;

            IntPtr target = GlobalLock(hGlobal);
            if (target == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            try
            {
                Marshal.Copy(bytes, 0, target, bytes.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Places text onto the Windows clipboard using Win32 API.
    /// </summary>
    public static async Task<bool> SetClipboardTextAsync(string text, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        return await Task.Run(() => SetClipboardText(text, ct), ct).ConfigureAwait(false);
    }

    #endregion

    #region Input Dispatch Helpers

    private async Task SendHardwareLeftClickAsync(int screenX, int screenY, CancellationToken ct)
    {
        EnsureDesktopAttachment();
        SetCursorPos(screenX, screenY);
        await Task.Delay(25, ct);

        EnsureDesktopAttachment();
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);

        await Task.Delay(125, ct);

        EnsureDesktopAttachment();
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    private async Task SendHardwareDragAsync(int startX, int startY, int endX, int endY, int durationMs, CancellationToken ct)
    {
        EnsureDesktopAttachment();
        SetCursorPos(startX, startY);
        await Task.Delay(25, ct);

        EnsureDesktopAttachment();
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(50, ct);

        int steps = Math.Clamp(durationMs / 16, 5, 60);
        int stepDelay = Math.Max(5, durationMs / steps);

        for (int i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            double progress = (double)i / steps;
            int currX = (int)Math.Round(startX + (endX - startX) * progress);
            int currY = (int)Math.Round(startY + (endY - startY) * progress);

            EnsureDesktopAttachment();
            SetCursorPos(currX, currY);
            await Task.Delay(stepDelay, ct);
        }

        EnsureDesktopAttachment();
        SetCursorPos(endX, endY);
        await Task.Delay(50, ct);

        EnsureDesktopAttachment();
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    private static void SendUnicodeChar(char c)
    {
        EnsureDesktopAttachment();
        INPUT[] inputs = new INPUT[2];

        // Key Down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = 0;
        inputs[0].u.ki.wScan = (ushort)c;
        inputs[0].u.ki.dwFlags = KEYEVENTF_UNICODE;

        // Key Up
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = 0;
        inputs[1].u.ki.wScan = (ushort)c;
        inputs[1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public static bool IsWindowOffscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) return false;
        if (GetWindowRect(hwnd, out RECT rect))
        {
            return rect.Left >= 5000 || rect.Top >= 5000 || rect.Right <= -5000 || rect.Bottom <= -5000;
        }
        return false;
    }

    private static void BringWindowToForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        EnsureDesktopAttachment();
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }
        else if (IsWindowOffscreen(hwnd))
        {
            return;
        }

        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
    }

    public static byte ResolveVirtualKeyCode(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return 0;

        string normalized = keyName.Trim().ToLowerInvariant();

        return normalized switch
        {
            "enter" or "return" => VK_RETURN,
            "tab" => VK_TAB,
            "escape" or "esc" => VK_ESCAPE,
            "space" or "spacebar" => VK_SPACE,
            "backspace" or "back" => VK_BACK,
            "delete" or "del" => VK_DELETE,
            "insert" or "ins" => VK_INSERT,
            "home" => VK_HOME,
            "end" => VK_END,
            "pageup" or "pgup" => VK_PRIOR,
            "pagedown" or "pgdn" => VK_NEXT,
            "up" or "arrowup" => VK_UP,
            "down" or "arrowdown" => VK_DOWN,
            "left" or "arrowleft" => VK_LEFT,
            "right" or "arrowright" => VK_RIGHT,
            "f1" => 0x70,
            "f2" => 0x71,
            "f3" => 0x72,
            "f4" => 0x73,
            "f5" => 0x74,
            "f6" => 0x75,
            "f7" => 0x76,
            "f8" => 0x77,
            "f9" => 0x78,
            "f10" => 0x79,
            "f11" => 0x7A,
            "f12" => 0x7B,
            "shift" => 0x10,
            "ctrl" or "control" => 0x11,
            "alt" => 0x12,
            "capslock" => 0x14,
            "printscreen" or "prtscn" => 0x2C,
            _ => normalized.Length == 1 ? (byte)char.ToUpperInvariant(normalized[0]) : (byte)0
        };
    }

    #endregion

    #region Window Resolution & Bounds

    public IntPtr ResolveWindowHandle(string target, out string windowTitle, out int pid)
    {
        windowTitle = string.Empty;
        pid = 0;

        if (string.IsNullOrWhiteSpace(target))
        {
            target = "hon_x64";
        }

        string trimmed = target.Trim();

        // -1. Explicit PID or Client instance targeting (e.g. "pid:1234" or "client:1234")
        if (trimmed.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("client:", StringComparison.OrdinalIgnoreCase))
        {
            string pStr = trimmed[(trimmed.IndexOf(':') + 1)..].Trim();
            if (int.TryParse(pStr, out int targetPid) && targetPid > 0)
            {
                pid = targetPid;
                IntPtr byPid = FindWindowForProcessId(targetPid);
                if (byPid != IntPtr.Zero && IsWindow(byPid))
                {
                    windowTitle = GetWindowTitle(byPid);
                    return byPid;
                }

                // Self-Healing Stale PID Auto-Migration (MCP-069, MCP-074):
                // If the specified PID is dead or windowless, check if there is an active hon_x64 client process.
                // If exactly ONE active client process exists with a valid window, auto-migrate to it!
                if (!GameServerManageHandler.IsTestExecution)
                {
                    try
                    {
                        Process[] honProcs = Process.GetProcessesByName("hon_x64");
                        try
                        {
                            ClientProcessSupervisor supervisor = new ClientProcessSupervisor();
                        Process[] activeClients = honProcs
                            .Where(p => p.Id != targetPid && supervisor.IsClientProcess(p) && FindWindowForProcessId(p.Id) != IntPtr.Zero)
                            .ToArray();

                        if (activeClients.Length == 1)
                        {
                            Process autoMigrated = activeClients[0];
                            pid = autoMigrated.Id;
                            IntPtr autoH = autoMigrated.MainWindowHandle != IntPtr.Zero && IsWindow(autoMigrated.MainWindowHandle)
                                ? autoMigrated.MainWindowHandle
                                : FindWindowForProcessId(pid);

                            if (autoH != IntPtr.Zero && IsWindow(autoH))
                            {
                                windowTitle = GetWindowTitle(autoH);
                                _logger.LogWarning("Target PID {StalePid} is offline or windowless. Auto-migrated to single active client PID {ActivePid} ('{Title}').", targetPid, pid, windowTitle);
                                return autoH;
                            }
                        }
                    }
                    finally
                    {
                        foreach (Process p in honProcs) p.Dispose();
                    }
                }
                catch { }
                }

                return IntPtr.Zero;
            }
        }

        // 0. Primary check for HoN Game Render Window directly
        if (trimmed.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("game", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("client", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("game_client", StringComparison.OrdinalIgnoreCase))
        {
            IntPtr directGameWnd = FindWindow("K2_Direct3D", null);
            if (directGameWnd == IntPtr.Zero) directGameWnd = FindWindow("K2_Class", null);
            if (directGameWnd == IntPtr.Zero) directGameWnd = FindWindow("K2_Vulkan", null);
            if (directGameWnd == IntPtr.Zero) directGameWnd = FindWindow(null, "Heroes of Newerth x64");
            if (directGameWnd == IntPtr.Zero) directGameWnd = FindWindow(null, "Heroes of Newerth");
            if (directGameWnd != IntPtr.Zero && IsWindow(directGameWnd))
            {
                GetWindowThreadProcessId(directGameWnd, out uint gPid);
                pid = (int)gPid;
                windowTitle = GetWindowTitle(directGameWnd);
                return directGameWnd;
            }

            // Fallback: check if hon_x64 process exists even if window handle is offscreen or not found by FindWindow
            Process[] honProcs = Process.GetProcessesByName("hon_x64");
            try
            {
                if (honProcs.Length > 0)
                {
                    ClientProcessSupervisor supervisor = new ClientProcessSupervisor();
                    Process targetProc = honProcs.FirstOrDefault(p => supervisor.IsClientProcess(p) && FindWindowForProcessId(p.Id) != IntPtr.Zero)
                        ?? honProcs.FirstOrDefault(p => supervisor.IsClientProcess(p))
                        ?? honProcs.FirstOrDefault(p => FindWindowForProcessId(p.Id) != IntPtr.Zero)
                        ?? honProcs[0];

                    pid = targetProc.Id;
                    IntPtr h = targetProc.MainWindowHandle != IntPtr.Zero && IsWindow(targetProc.MainWindowHandle)
                        ? targetProc.MainWindowHandle
                        : FindWindowForProcessId(pid);

                    if (h != IntPtr.Zero && IsWindow(h))
                    {
                        windowTitle = GetWindowTitle(h);
                        return h;
                    }
                    return IntPtr.Zero;
                }
            }
            finally
            {
                foreach (Process p in honProcs) p.Dispose();
            }
        }

        // 1. "foreground" / "active"
        if (trimmed.Equals("foreground", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd != IntPtr.Zero && IsWindow(fgHwnd))
            {
                GetWindowThreadProcessId(fgHwnd, out uint fgPid);
                pid = (int)fgPid;
                windowTitle = GetWindowTitle(fgHwnd);
                return fgHwnd;
            }

            // Fall back to checking active hon_x64 or willowmaker processes
            string[] fallbackProcesses = ["hon_x64", "willowmaker", "WILLOWMAKER"];
            foreach (string procName in fallbackProcesses)
            {
                Process[] procMatches = Process.GetProcessesByName(procName);
                try
                {
                    foreach (Process p in procMatches)
                    {
                        try
                        {
                            IntPtr h = p.MainWindowHandle != IntPtr.Zero && IsWindow(p.MainWindowHandle)
                                ? p.MainWindowHandle
                                : FindWindowForProcessId(p.Id);

                            if (h != IntPtr.Zero && IsWindow(h))
                            {
                                pid = p.Id;
                                windowTitle = GetWindowTitle(h);
                                return h;
                            }
                        }
                        catch { }
                    }
                }
                finally
                {
                    foreach (Process p in procMatches) p.Dispose();
                }
            }
        }

        // 2. Explicit HWND hex ('0x001B0E24') or integer
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out long hexVal))
        {
            IntPtr candidate = new(hexVal);
            if (IsWindow(candidate))
            {
                GetWindowThreadProcessId(candidate, out uint hexPid);
                pid = (int)hexPid;
                windowTitle = GetWindowTitle(candidate);
                return candidate;
            }
        }

        if (long.TryParse(trimmed, out long intHwnd) && intHwnd > 1000)
        {
            IntPtr candidate = new(intHwnd);
            if (IsWindow(candidate))
            {
                GetWindowThreadProcessId(candidate, out uint intPid);
                pid = (int)intPid;
                windowTitle = GetWindowTitle(candidate);
                return candidate;
            }
        }

        // 3. Process name matching (hon_x64, willowmaker, nexus.controlpanel.gui, etc.)
        string procNameQuery = trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(trimmed)
            : trimmed;

        Process[] processes = Process.GetProcesses();
        IntPtr fallbackHwnd = IntPtr.Zero;
        string fallbackTitle = string.Empty;
        int fallbackPid = 0;

        foreach (Process p in processes)
        {
            try
            {
                if (p.ProcessName.Equals(procNameQuery, StringComparison.OrdinalIgnoreCase) ||
                    p.ProcessName.Contains(procNameQuery, StringComparison.OrdinalIgnoreCase))
                {
                    if (procNameQuery.Equals("hon_x64", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string? modulePath = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(modulePath) &&
                                (modulePath.Contains("server", StringComparison.OrdinalIgnoreCase) || modulePath.Contains("dedicated", StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }
                        }
                        catch { }
                    }

                    IntPtr h = p.MainWindowHandle != IntPtr.Zero && IsWindow(p.MainWindowHandle)
                        ? p.MainWindowHandle
                        : FindWindowForProcessId(p.Id);

                    if (h != IntPtr.Zero)
                    {
                        string title = GetWindowTitle(h);
                        // If it's a game client window (e.g. "Heroes of Newerth"), prioritize it
                        if (title.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase) ||
                            (!title.Contains("Unnamed_Server", StringComparison.OrdinalIgnoreCase) && !title.Contains("dedicated", StringComparison.OrdinalIgnoreCase)))
                        {
                            pid = p.Id;
                            windowTitle = title;
                            return h;
                        }

                        if (fallbackHwnd == IntPtr.Zero)
                        {
                            fallbackHwnd = h;
                            fallbackTitle = title;
                            fallbackPid = p.Id;
                        }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        if (fallbackHwnd != IntPtr.Zero)
        {
            pid = fallbackPid;
            windowTitle = fallbackTitle;
            return fallbackHwnd;
        }

        // 4. Window title query (substring / wildcard)
        string cleanTitleQuery = trimmed.Trim('*');
        List<(IntPtr Hwnd, string Title, int Pid)> matches = [];

        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h))
            {
                string title = GetWindowTitle(h);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    if (title.Contains(cleanTitleQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        GetWindowThreadProcessId(h, out uint pId);
                        matches.Add((h, title, (int)pId));
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        if (matches.Count > 0)
        {
            (IntPtr Hwnd, string Title, int Pid) best = matches[0];
            pid = best.Pid;
            windowTitle = best.Title;
            return best.Hwnd;
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindWindowForProcessId(int processId)
    {
        // Leverage WindowCapturer's robust window resolution (checks K2_ classes, window titles, offscreen coords, and desktop window stations)
        return WindowCapturer.FindWindowForProcessId(processId);
    }

    private static string BuildWindowNotFoundErrorMessage(string target, int pid)
    {
        bool isClientTarget = target.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
                              target.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase) ||
                              target.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                              target.Equals("client", StringComparison.OrdinalIgnoreCase) ||
                              target.Equals("game_client", StringComparison.OrdinalIgnoreCase) ||
                              target.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) ||
                              target.StartsWith("client:", StringComparison.OrdinalIgnoreCase);

        if (isClientTarget)
        {
            Process[] honProcs = Process.GetProcessesByName("hon_x64");
            bool isRunning = honProcs.Length > 0;
            string activePids = isRunning ? string.Join(", ", honProcs.Select(p => p.Id)) : "none";
            foreach (Process p in honProcs) p.Dispose();

            if (!isRunning)
            {
                return $"Target window '{target}' not found. The game client process 'hon_x64.exe' is not running. Launch it first via 'kongor_game_client_manage action: launch' or 'kongor_match_launch'.";
            }
            if (pid > 0)
            {
                return $"Target window '{target}' not found. Game client process (PID {pid}) was specified, but its Win32 window handle is unavailable or the process has exited. Active client PID(s): {activePids}. Route commands directly via in-engine Lua ('kongor_game_lua') or console ('kongor_game_command') instead of OS keystrokes.";
            }
            return $"Target window '{target}' not found. Game client process was detected, but no render window was found. Active client PID(s): {activePids}. Route commands via in-engine Lua ('kongor_game_lua') or console ('kongor_game_command').";
        }

        return $"Target window '{target}' not found.";
    }

    private static IntPtr FindChildWindowByTextOrId(IntPtr parentHwnd, string query)
    {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(parentHwnd, (childHwnd, _) =>
        {
            if (IsWindowVisible(childHwnd))
            {
                string text = GetWindowTitle(childHwnd);
                string className = GetWindowClassName(childHwnd);
                int ctrlId = GetDlgCtrlID(childHwnd);

                if (text.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                    text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    ctrlId.ToString() == query ||
                    className.Equals(query, StringComparison.OrdinalIgnoreCase))
                {
                    result = childHwnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static RECT GetAccurateWindowBounds(IntPtr hwnd)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            int hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmRect, Marshal.SizeOf<RECT>());
            if (hr == 0 && dwmRect.Right > dwmRect.Left && dwmRect.Bottom > dwmRect.Top)
            {
                return dwmRect;
            }
        }

        if (GetWindowRect(hwnd, out RECT winRect) && winRect.Right > winRect.Left && winRect.Bottom > winRect.Top)
        {
            return winRect;
        }

        if (GetClientRect(hwnd, out RECT clientRect))
        {
            POINT pt = new() { X = clientRect.Left, Y = clientRect.Top };
            ClientToScreen(hwnd, ref pt);
            return new RECT
            {
                Left = pt.X,
                Top = pt.Y,
                Right = pt.X + (clientRect.Right - clientRect.Left),
                Bottom = pt.Y + (clientRect.Bottom - clientRect.Top)
            };
        }

        return new RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        StringBuilder sb = new(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetWindowClassName(IntPtr hwnd)
    {
        StringBuilder sb = new(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    #endregion

    #region Automation Type Mappers

    private static IUIAutomation? CreateAutomation()
    {
        try
        {
            return new CUIAutomation8();
        }
        catch
        {
            try
            {
                return new CUIAutomation();
            }
            catch
            {
                return null;
            }
        }
    }

    public static string MapControlTypeId(int controlTypeId)
    {
        return controlTypeId switch
        {
            UIA_ControlTypeIds.UIA_ButtonControlTypeId => "Button",
            UIA_ControlTypeIds.UIA_EditControlTypeId => "TextBox",
            UIA_ControlTypeIds.UIA_ComboBoxControlTypeId => "ComboBox",
            UIA_ControlTypeIds.UIA_CheckBoxControlTypeId => "CheckBox",
            UIA_ControlTypeIds.UIA_RadioButtonControlTypeId => "RadioButton",
            UIA_ControlTypeIds.UIA_HyperlinkControlTypeId => "Hyperlink",
            UIA_ControlTypeIds.UIA_ListItemControlTypeId => "ListItem",
            UIA_ControlTypeIds.UIA_ListControlTypeId => "List",
            UIA_ControlTypeIds.UIA_MenuItemControlTypeId => "MenuItem",
            UIA_ControlTypeIds.UIA_MenuControlTypeId => "Menu",
            UIA_ControlTypeIds.UIA_MenuBarControlTypeId => "MenuBar",
            UIA_ControlTypeIds.UIA_ProgressBarControlTypeId => "ProgressBar",
            UIA_ControlTypeIds.UIA_ScrollBarControlTypeId => "ScrollBar",
            UIA_ControlTypeIds.UIA_SliderControlTypeId => "Slider",
            UIA_ControlTypeIds.UIA_SpinnerControlTypeId => "Spinner",
            UIA_ControlTypeIds.UIA_TabItemControlTypeId => "TabItem",
            UIA_ControlTypeIds.UIA_TabControlTypeId => "TabControl",
            UIA_ControlTypeIds.UIA_TextControlTypeId => "Text",
            UIA_ControlTypeIds.UIA_ToolBarControlTypeId => "ToolBar",
            UIA_ControlTypeIds.UIA_ToolTipControlTypeId => "ToolTip",
            UIA_ControlTypeIds.UIA_TreeItemControlTypeId => "TreeItem",
            UIA_ControlTypeIds.UIA_TreeControlTypeId => "Tree",
            UIA_ControlTypeIds.UIA_CustomControlTypeId => "Custom",
            UIA_ControlTypeIds.UIA_GroupControlTypeId => "Group",
            UIA_ControlTypeIds.UIA_DataGridControlTypeId => "DataGrid",
            UIA_ControlTypeIds.UIA_DataItemControlTypeId => "DataItem",
            UIA_ControlTypeIds.UIA_DocumentControlTypeId => "Document",
            UIA_ControlTypeIds.UIA_SplitButtonControlTypeId => "SplitButton",
            UIA_ControlTypeIds.UIA_WindowControlTypeId => "Window",
            UIA_ControlTypeIds.UIA_PaneControlTypeId => "Pane",
            _ => $"Control_{controlTypeId}"
        };
    }

    public static bool IsInteractiveControlType(int controlTypeId)
    {
        return controlTypeId is
            UIA_ControlTypeIds.UIA_ButtonControlTypeId or
            UIA_ControlTypeIds.UIA_EditControlTypeId or
            UIA_ControlTypeIds.UIA_ComboBoxControlTypeId or
            UIA_ControlTypeIds.UIA_CheckBoxControlTypeId or
            UIA_ControlTypeIds.UIA_RadioButtonControlTypeId or
            UIA_ControlTypeIds.UIA_HyperlinkControlTypeId or
            UIA_ControlTypeIds.UIA_ListItemControlTypeId or
            UIA_ControlTypeIds.UIA_MenuItemControlTypeId or
            UIA_ControlTypeIds.UIA_TabItemControlTypeId or
            UIA_ControlTypeIds.UIA_TreeItemControlTypeId or
            UIA_ControlTypeIds.UIA_SliderControlTypeId or
            UIA_ControlTypeIds.UIA_SpinnerControlTypeId or
            UIA_ControlTypeIds.UIA_SplitButtonControlTypeId;
    }

    public static string MapWin32ClassNameToControlType(string className)
    {
        string upper = className.ToUpperInvariant();
        if (upper.Contains("BUTTON")) return "Button";
        if (upper.Contains("EDIT")) return "TextBox";
        if (upper.Contains("COMBO")) return "ComboBox";
        if (upper.Contains("LIST")) return "ListBox";
        if (upper.Contains("STATIC")) return "Label";
        if (upper.Contains("SCROLL")) return "ScrollBar";
        return className;
    }

    #endregion

    #region Win32 P/Invoke & Constants

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int SW_RESTORE = 9;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const int INPUT_KEYBOARD = 1;

    private const byte VK_BACK = 0x08;
    private const byte VK_TAB = 0x09;
    private const byte VK_RETURN = 0x0D;
    private const byte VK_ESCAPE = 0x1B;
    private const byte VK_SPACE = 0x20;
    private const byte VK_PRIOR = 0x21; // Page Up
    private const byte VK_NEXT = 0x22;  // Page Down
    private const byte VK_END = 0x23;
    private const byte VK_HOME = 0x24;
    private const byte VK_LEFT = 0x25;
    private const byte VK_UP = 0x26;
    private const byte VK_RIGHT = 0x27;
    private const byte VK_DOWN = 0x28;
    private const byte VK_INSERT = 0x2D;
    private const byte VK_DELETE = 0x2E;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    #endregion
}

/// <summary>
/// Classification of UI control events for dual-tier droppable vs. reliable queueing (#233).
/// </summary>
public enum UiControlEventKind : byte
{
    MouseMove = 0,
    MouseHover = 1,
    MouseScroll = 2,
    MouseDown = 3,
    MouseUp = 4,
    KeyDown = 5,
    KeyUp = 6,
    LuaCommand = 7,
    ClipboardSet = 8
}

/// <summary>
/// Structured UI control event routed through <see cref="ScrcpyDroppableControlQueue{T}"/> (#233).
/// </summary>
public readonly record struct UiControlEvent(
    UiControlEventKind Kind,
    int X = 0,
    int Y = 0,
    string Payload = "",
    ulong Sequence = 0);

/// <summary>
/// Dual-tier control message queue ported from scrcpy (app/src/controller.c, #233).
/// Separates droppable events (mouse-move, hover, scroll) from non-droppable reliable events
/// (clicks, key-down, key-up, Lua commands) and coalesces window-resize events in a single
/// overwrite slot (PendingResize) so excess 1000Hz mouse hover/resize traffic is shed while
/// clicks and key transitions are preserved.
/// </summary>
public sealed class ScrcpyDroppableControlQueue<T>
{
    public const int TotalCapacity = 60;
    public const int NonDroppableCap = 56;
    public const int DroppableCap = 4;
    public const int DefaultMaxDroppableCapacity = 60;

    private readonly Lock _sync = new();
    private readonly Queue<T> _keepQueue = new();
    private readonly Queue<T> _droppableQueue = new();
    private (int Width, int Height)? _pendingResize;

    public int MaxDroppableCapacity { get; }
    public int MaxKeepCapacity { get; }
    public long DroppedCount { get; private set; }
    public long KeepEnqueuedCount { get; private set; }
    public long DroppableEnqueuedCount { get; private set; }
    public long CoalescedResizeCount { get; private set; }

    public (int Width, int Height)? PendingResize
    {
        get
        {
            lock (_sync)
            {
                return _pendingResize;
            }
        }
    }

    public int KeepCount
    {
        get
        {
            lock (_sync)
            {
                return _keepQueue.Count;
            }
        }
    }

    public int DroppableCount
    {
        get
        {
            lock (_sync)
            {
                return _droppableQueue.Count;
            }
        }
    }

    public int TotalCount
    {
        get
        {
            lock (_sync)
            {
                return _keepQueue.Count + _droppableQueue.Count + (_pendingResize.HasValue ? 1 : 0);
            }
        }
    }

    public ScrcpyDroppableControlQueue(
        int maxDroppableCapacity = DefaultMaxDroppableCapacity,
        int maxKeepCapacity = NonDroppableCap)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDroppableCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxKeepCapacity);
        MaxDroppableCapacity = maxDroppableCapacity;
        MaxKeepCapacity = maxKeepCapacity;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types")]
    public static ScrcpyDroppableControlQueue<T> CreateStrictScrcpyPartition()
    {
        return new ScrcpyDroppableControlQueue<T>(maxDroppableCapacity: DroppableCap, maxKeepCapacity: NonDroppableCap);
    }

    public bool Push(T message, bool isDroppable)
    {
        lock (_sync)
        {
            if (!isDroppable)
            {
                if (_keepQueue.Count >= MaxKeepCapacity)
                {
                    DroppedCount++;
                    return false;
                }

                _keepQueue.Enqueue(message);
                KeepEnqueuedCount++;
                return true;
            }

            if (_droppableQueue.Count >= MaxDroppableCapacity)
            {
                DroppedCount++;
                return false;
            }

            _droppableQueue.Enqueue(message);
            DroppableEnqueuedCount++;
            return true;
        }
    }

    /// <summary>
    /// Coalesces rapid window-resize control events into a single 1-slot latest-value state.
    /// Returns true if this was the first pending resize, or false if a previous unconsumed resize was coalesced.
    /// </summary>
    public bool PushResize(int width, int height)
    {
        lock (_sync)
        {
            bool coalesced = _pendingResize.HasValue;
            if (coalesced)
            {
                CoalescedResizeCount++;
            }

            _pendingResize = (width, height);
            return !coalesced;
        }
    }

    public bool TryTakePendingResize(out int width, out int height)
    {
        lock (_sync)
        {
            if (_pendingResize.HasValue)
            {
                (width, height) = _pendingResize.Value;
                _pendingResize = null;
                return true;
            }

            width = 0;
            height = 0;
            return false;
        }
    }

    public bool TryPop([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T message)
    {
        lock (_sync)
        {
            if (_keepQueue.Count > 0)
            {
                message = _keepQueue.Dequeue();
                return true;
            }

            if (_droppableQueue.Count > 0)
            {
                message = _droppableQueue.Dequeue();
                return true;
            }

            message = default;
            return false;
        }
    }

    public List<T> DrainAll()
    {
        lock (_sync)
        {
            List<T> drained = new List<T>(_keepQueue.Count + _droppableQueue.Count);
            while (_keepQueue.Count > 0)
            {
                drained.Add(_keepQueue.Dequeue());
            }
            while (_droppableQueue.Count > 0)
            {
                drained.Add(_droppableQueue.Dequeue());
            }
            return drained;
        }
    }
}

/// <summary>
/// Outcome of waiting on <see cref="ScrcpyAckSyncBarrier"/> (#235).
/// </summary>
public enum ScrcpyAckResult : byte
{
    Ok = 0,
    Timeout = 1,
    Intr = 2
}

/// <summary>
/// Sequence-correlated acknowledgment barrier ported from scrcpy (app/src/util/acksync.c, #235).
/// Synchronizes host clipboard/CVar mutations with in-game Lua acknowledgment before dispatching
/// subsequent keystrokes (eliminating arbitrary sleep races).
/// </summary>
public sealed class ScrcpyAckSyncBarrier
{
    private readonly object _monitor = new();
    private ulong _nextSequence;
    private ulong _lastAckedSequence;
    private bool _interrupted;

    public ulong LastAckedSequence
    {
        get
        {
            lock (_monitor)
            {
                return _lastAckedSequence;
            }
        }
    }

    public bool IsInterrupted
    {
        get
        {
            lock (_monitor)
            {
                return _interrupted;
            }
        }
    }

    public ulong NextSequence()
    {
        lock (_monitor)
        {
            _nextSequence++;
            return _nextSequence;
        }
    }

    public void Ack(ulong sequence)
    {
        lock (_monitor)
        {
            if (sequence > _lastAckedSequence)
            {
                _lastAckedSequence = sequence;
                Monitor.PulseAll(_monitor);
            }
        }
    }

    public void Interrupt()
    {
        lock (_monitor)
        {
            _interrupted = true;
            Monitor.PulseAll(_monitor);
        }
    }

    public void Reset()
    {
        lock (_monitor)
        {
            _interrupted = false;
            _lastAckedSequence = 0;
            _nextSequence = 0;
            Monitor.PulseAll(_monitor);
        }
    }

    private void PulseWaiters()
    {
        lock (_monitor)
        {
            Monitor.PulseAll(_monitor);
        }
    }

    public ScrcpyAckResult Wait(ulong sequence, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMs);

        using CancellationTokenRegistration reg = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(PulseWaiters)
            : default;

        long deadlineTicks = Stopwatch.GetTimestamp() + (long)(timeoutMs * (Stopwatch.Frequency / 1000.0));
        lock (_monitor)
        {
            while (!_interrupted && !cancellationToken.IsCancellationRequested && _lastAckedSequence < sequence)
            {
                long remainingTicks = deadlineTicks - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    return ScrcpyAckResult.Timeout;
                }

                int waitMs = Math.Max(1, (int)Math.Ceiling((remainingTicks * 1000.0) / Stopwatch.Frequency));
                if (!Monitor.Wait(_monitor, waitMs))
                {
                    if (!_interrupted && !cancellationToken.IsCancellationRequested && _lastAckedSequence < sequence && Stopwatch.GetTimestamp() >= deadlineTicks)
                    {
                        return ScrcpyAckResult.Timeout;
                    }
                }
            }

            return (_interrupted || cancellationToken.IsCancellationRequested) ? ScrcpyAckResult.Intr : ScrcpyAckResult.Ok;
        }
    }

    public ScrcpyAckResult WaitForAck(ulong targetSeq, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        int timeoutMs = timeout <= TimeSpan.Zero ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(timeout.TotalMilliseconds));
        return Wait(targetSeq, timeoutMs, cancellationToken);
    }

    public async Task<ScrcpyAckResult> WaitAsync(ulong sequence, int timeoutMs, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => Wait(sequence, timeoutMs, cancellationToken), CancellationToken.None).ConfigureAwait(false);
    }

    public Task<ScrcpyAckResult> WaitForAckAsync(ulong targetSeq, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        int timeoutMs = timeout <= TimeSpan.Zero ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(timeout.TotalMilliseconds));
        return WaitAsync(targetSeq, timeoutMs, cancellationToken);
    }
}


