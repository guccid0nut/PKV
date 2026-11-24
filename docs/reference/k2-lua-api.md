# K2 Engine Lua Native Widget API Reference

> **Authoritative In-Engine Reflection Dump**
> Captured via live reflection on `hon_x64.exe` (`main` interface metatable inspection).
> Total Exposed Widget Methods: **273**

---

## 1. Hierarchy & Child Traversal
- `widget:GetParent()` -> Returns the parent widget or `nil`.
- `widget:SetParent(parentWidget)` -> Reparents the widget.
- `widget:GetChildren()` -> Returns a table/collection of direct child widgets.
- `widget:GetChildWidget(name)` -> Finds a child widget by its relative name.
- `widget:HasChildWidget(name)` -> Returns `true` if a named child exists.
- `widget:ChildrenCall(methodName, ...)` -> Invokes method on all child widgets.
- `widget:ClearChildren()` -> Destroys or removes child widgets.
- `widget:GetPassiveChildren()` / `widget:SetPassiveChildren(bool)`
- `widget:GetInterface()` -> Returns the parent interface.
- `widget:LoadChildInterface(path)` -> Loads an embedded child interface.

---

## 2. Type & Identification
- `widget:GetType()` -> Returns widget type as string (e.g. `'panel'`, `'button'`, `'label'`, `'textbox'`, `'image'`). *(Note: `GetTypeName` does NOT exist; use `GetType()`)*.
- `widget:GetName()` -> Returns the widget's registered string name.
- `widget:GetGroup()` / `widget:GetGroupName()` / `widget:GroupCall(method, ...)`
- `widget:IsValid()` -> Returns `true` if native C++ widget pointer is valid.

---

## 3. Visibility & Active State
- `widget:IsVisible()` -> Returns `true` if visible (including inheritance).
- `widget:IsVisibleSelf()` -> Returns `true` if this specific widget is marked visible.
- `widget:SetVisible(bool)` -> Shows or hides the widget.
- `widget:ToggleVisible()` -> Toggles visibility.
- `widget:GetWasVisible()` / `widget:SetWasVisible(bool)`
- `widget:IsEnabled()` -> Returns `true` if the widget accepts interaction.
- `widget:IsEnabledSelf()` -> Returns `true` if this specific widget is enabled.
- `widget:SetEnabled(bool)` -> Enables or disables the widget.
- `widget:IsDormant()` / `widget:SetDormant(bool)`

---

## 4. Positioning, Bounds & Geometry
- `widget:GetX()` / `widget:GetY()` -> Returns relative X and Y coordinate strings (e.g. `'10%'`, `'20'` or numbers).
- `widget:GetXFromString(str)` / `widget:GetYFromString(str)`
- `widget:SetX(val)` / `widget:SetY(val)` / `widget:SetPos(x, y)`
- `widget:GetAbsoluteX()` / `widget:GetAbsoluteY()` -> Returns true pixel coordinates on the screen.
- `widget:SetAbsoluteX(x)` / `widget:SetAbsoluteY(y)`
- `widget:GetWidth()` / `widget:GetHeight()` -> Returns width/height in pixels or percentages.
- `widget:GetWidthFromString(str)` / `widget:GetHeightFromString(str)`
- `widget:SetWidth(w)` / `widget:SetHeight(h)`
- `widget:GetBaseWidth()` / `widget:GetBaseHeight()`
- `widget:GetBaseX()` / `widget:GetBaseY()`
- `widget:GetAlign()` / `widget:SetAlign(align)` (e.g. `'left'`, `'center'`, `'right'`)
- `widget:GetVAlign()` / `widget:SetVAlign(valign)` (e.g. `'top'`, `'center'`, `'bottom'`)
- `widget:RecalculateSize()` -> Forces immediate layout reflow.
- `widget:Scale(scale)` / `widget:ScaleWidth(sw)` / `widget:ScaleHeight(sh)`

---

## 5. Event Dispatching & Callbacks
- `widget:DoEvent()` -> **Takes 0 arguments.** Dispatches the primary XML `onevent` handler.
- `widget:DoEventN(index)` -> **Takes an integer index.** Dispatches `onevent0`, `onevent1`, etc.
- `widget:GetCallback(eventStr)` -> Returns registered Lua callback function.
- `widget:SetCallback(eventStr, fn)` -> Sets Lua callback (e.g. `'onclick'`, `'onframe'`, `'onwake'`, `'onmouseover'`, `'onmouseout'`).
- `widget:ClearCallback(eventStr)` -> Removes callback.
- `widget:RefreshCallbacks()` -> Flushes and updates callback bindings.
- `widget:UICmd(cmdStr)` -> Executes a UI console command in widget context.

---

## 6. Text, Input & Focus
- `widget:GetText()` -> Returns current text string.
- `widget:SetText(str)` -> Updates text string.
- `widget:ClearText()` -> Clears text.
- `widget:GetTextColor()` / `widget:SetTextColor(colorStr)`
- `widget:SetFont(fontName)`
- `widget:GetMaxLength()` / `widget:SetMaxLength(len)`
- `widget:GetStringWidth(str)` -> Measures pixel width of a string in current font.
- `widget:GetInputLine()` / `widget:SetInputLine(line)`
- `widget:EraseInputLine()`
- `widget:SetFocus(bool)` -> Focuses or unfocuses the widget.
- `widget:HasFocus()` -> Returns `true` if widget currently has input focus.
- `widget:SetDefaultFocus(bool)`

---

## 7. Animation, Sliding & Transitions
- `widget:SlideX(targetX, durationMs)` -> Smoothly interpolates X to `targetX` over `durationMs`. *(Note: `SlideTo(x, y)` does not exist; use `SlideX` and `SlideY`!)*
- `widget:SlideY(targetY, durationMs)` -> Smoothly interpolates Y to `targetY` over `durationMs`.
- `widget:Fade(fromAlpha, toAlpha, durationMs)` -> Fades alpha over duration.
- `widget:FadeIn(durationMs)` / `widget:FadeOut(durationMs)`
- `widget:FadeTo(targetAlpha, durationMs)`
- `widget:SetAlpha(alphaVal)`
- `widget:Sleep(durationMs, callbackFn)` -> Schedules wake timer.
- `widget:InterruptSleep()` -> Cancels active sleep timer.
- `widget:Wakeup()` -> Immediately fires sleep callback. *(Note: `Wake` does not exist; use `Wakeup()`)*.
- `widget:StartAnim(animName)` / `widget:PlayNextAnim()` / `widget:AdvanceAnim()`
- `widget:GetAnimSpeed()` / `widget:SetAnimSpeed(speed)`

---

## 8. Dynamic Instantiation
- `widget:Instantiate(templateName, paramTable)` -> Dynamically instantiates a named XML template under this widget.

---

## 9. Z-Order & Layering
- `widget:BringToFront()` -> Moves widget to top of sibling draw order.
- `widget:PushToBack()` -> Moves widget to bottom of sibling draw order.
- `widget:ForceToFront()`

---

## 10. Native Watchers
- `widget:RegisterWatch(cvarOrTrigger, callbackFn)` -> Subscribes to engine trigger or cvar updates.
- `widget:UnregisterWatch(cvarOrTrigger)` -> Unsubscribes watcher.
- `widget:SetWatch(cvarOrTrigger)`
